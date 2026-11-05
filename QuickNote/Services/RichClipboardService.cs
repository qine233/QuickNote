using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using QuickNote.Models;

namespace QuickNote.Services;

/// <summary>
/// Writes a script to the system clipboard as one composite payload so that
/// pasting into a chat window (微信 / QQ / 千牛 / 钉钉) keeps the text and the
/// images in the order they were authored:
///
///   CF_UNICODETEXT      plain text fallback for edit boxes with no rich paste
///   "HTML Format"       CF_HTML, images inlined as file:/// URLs
///   "Rich Text Format"  RTF, images embedded as hex blobs (for RichEdit hosts)
///   CF_DIB + CF_HDROP   image-only payload, used when the script has no text
///
/// The clipboard is filled through the raw Win32 API rather than the managed
/// DataObject: the payload for a registered (non-standard) format ends up as
/// exactly the bytes we allocate, with no serializer in between guessing at
/// the encoding. That is what makes the CF_HTML byte offsets reliable.
/// </summary>
public static class RichClipboardService
{
    /// <summary>
    /// Result of one rich copy, used for the toast text.
    /// </summary>
    public sealed class Result
    {
        public int ImageCount { get; init; }
        public int MissingImageCount { get; init; }
        public bool HasText { get; init; }

        /// <summary>True when at least one usable image joined the payload.</summary>
        public bool IsRich => ImageCount > 0;
    }

    /// <summary>
    /// One ordered piece of the payload: a text run or an image on disk.
    /// Carrying the order end to end is what makes the paste come out the way
    /// the script was written.
    /// </summary>
    private readonly record struct Block(string? Text, string? ImagePath)
    {
        public bool IsText => Text != null;
        public static Block FromText(string value) => new(value, null);
        public static Block FromImage(string path) => new(null, path);
    }

    // RTF carries image bytes inline, so a long image list turns into a huge
    // string. Past this many raw image bytes we ship HTML only — a file URL is
    // always affordable, an inline blob is not.
    private const long MaxRtfImageBytes = 12 * 1024 * 1024;

    // Pictures are sized by the paste target, so cap the width we ask for.
    private const int MaxDisplayWidthPx = 360;

    private const int ClipboardRetryCount = 5;

    // ─── Public entry point ─────────────────────────────────────────────

    /// <summary>
    /// Copies one script, honouring the stored segment order. Throws when the
    /// clipboard cannot be taken over; images whose file is gone are skipped.
    /// </summary>
    public static Result Copy(IReadOnlyList<ContentSegment> segments)
    {
        var blocks = new List<Block>();
        var texts = new List<string>();
        var imagePaths = new List<string>();

        foreach (var segment in segments)
        {
            if (segment.IsImage)
            {
                var path = DatabaseService.GetImagePath(segment.Value);
                if (File.Exists(path))
                {
                    imagePaths.Add(path);
                    blocks.Add(Block.FromImage(path));
                }
            }
            else if (!string.IsNullOrWhiteSpace(segment.Value))
            {
                var text = segment.Value.Trim();
                texts.Add(text);
                blocks.Add(Block.FromText(text));
            }
        }

        var missingImageCount = segments.Count(s => s.IsImage) - imagePaths.Count;
        var plainText = string.Join("\n", texts);
        var hasText = !string.IsNullOrWhiteSpace(plainText);

        if (blocks.Count == 0)
            return new Result { ImageCount = 0, MissingImageCount = missingImageCount, HasText = false };

        var payload = new List<ClipboardItem>();

        if (hasText)
            payload.Add(ClipboardItem.FromText(plainText));

        if (imagePaths.Count > 0)
        {
            // Rich hosts read CF_HTML first, and it costs nothing to carry the
            // images because the markup only points at the files on disk.
            payload.Add(ClipboardItem.FromHtml(BuildCfHtml(BuildHtmlFragment(blocks))));

            if (hasText)
            {
                var rtf = TryBuildRtf(blocks);
                if (rtf != null)
                    payload.Add(ClipboardItem.FromRtf(rtf));
            }
            else
            {
                // Text-free script: behave like a screenshot so even a target
                // that only accepts a plain image paste gets the picture.
                foreach (var path in imagePaths)
                {
                    var dib = TryBuildDib(path);
                    if (dib != null)
                    {
                        payload.Add(ClipboardItem.FromDib(dib));
                        break;
                    }
                }
                payload.Add(ClipboardItem.FromDropFiles(imagePaths));
            }
        }

        Write(payload);

        return new Result
        {
            ImageCount = imagePaths.Count,
            MissingImageCount = missingImageCount,
            HasText = hasText,
        };
    }

    // ─── HTML (CF_HTML) ─────────────────────────────────────────────────

    private const string HtmlPrefix =
        "<html>\r\n<head>\r\n" +
        "<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">\r\n" +
        "</head>\r\n<body>\r\n<!--StartFragment-->";

    private const string HtmlSuffix = "<!--EndFragment-->\r\n</body>\r\n</html>";

    // "<!--StartFragment-->" — the fragment starts right after this marker.
    private const int HtmlStartMarkerBytes = 20;

    /// <summary>
    /// One block per segment, in order. No font or colour is forced so the
    /// paste target keeps its own look.
    /// </summary>
    private static string BuildHtmlFragment(IReadOnlyList<Block> blocks)
    {
        var sb = new StringBuilder();

        foreach (var block in blocks)
        {
            sb.Append("<p style=\"margin:0 0 6px 0\">");
            if (block.IsText)
            {
                sb.Append(EscapeHtml(block.Text!)
                    .Replace("\r\n", "\n")
                    .Replace('\r', '\n')
                    .Replace("\n", "<br />\r\n"));
            }
            else
            {
                sb.Append("<img src=\"").Append(ToFileUrl(block.ImagePath!))
                  .Append("\" style=\"max-width:").Append(MaxDisplayWidthPx)
                  .Append("px;height:auto\">");
            }
            sb.Append("</p>\r\n");
        }

        return sb.ToString();
    }

    private static string ToFileUrl(string path)
    {
        try
        {
            return new Uri(path, UriKind.Absolute).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            // A path the Uri builder rejects (an illegal character) is escaped
            // by hand so the markup still points at the file.
            var escaped = path.Replace('\\', '/');
            foreach (var (from, to) in new[] { ("%", "%25"), (" ", "%20"), ("#", "%23") })
                escaped = escaped.Replace(from, to);
            return "file:///" + escaped;
        }
    }

    private static string EscapeHtml(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    /// <summary>
    /// Wraps a body in the CF_HTML header. Offsets are byte offsets into the
    /// UTF-8 stream (Version 0.999999 is the UTF-8 flavour of the format) and
    /// are always eight digits, so the header keeps its own length stable.
    /// </summary>
    private static byte[] BuildCfHtml(string fragment)
    {
        var utf8 = Encoding.UTF8;
        var headerLength = utf8.GetByteCount(CfHtmlHeader(0, 0, 0, 0));
        var prefixLength = utf8.GetByteCount(HtmlPrefix);
        var suffixLength = utf8.GetByteCount(HtmlSuffix);
        var bodyLength = utf8.GetByteCount(fragment);

        var startHtml = headerLength;
        var endHtml = headerLength + prefixLength + bodyLength + suffixLength;
        var startFragment = headerLength + prefixLength + HtmlStartMarkerBytes;
        var endFragment = startFragment + bodyLength;

        var document = CfHtmlHeader(startHtml, endHtml, startFragment, endFragment)
            + HtmlPrefix + fragment + HtmlSuffix;

        // A trailing NUL sits past EndHTML and is ignored by readers, but
        // native producers write one and a few expect a terminated stream.
        var encoded = utf8.GetBytes(document);
        var bytes = new byte[encoded.Length + 1];
        Buffer.BlockCopy(encoded, 0, bytes, 0, encoded.Length);
        return bytes;
    }

    private static string CfHtmlHeader(long startHtml, long endHtml, long startFragment, long endFragment) =>
        "Version:0.999999\r\n" +
        $"StartHTML:{CfHtmlDigits(startHtml)}\r\n" +
        $"EndHTML:{CfHtmlDigits(endHtml)}\r\n" +
        $"StartFragment:{CfHtmlDigits(startFragment)}\r\n" +
        $"EndFragment:{CfHtmlDigits(endFragment)}\r\n";

    private static string CfHtmlDigits(long value) =>
        value.ToString("D8", CultureInfo.InvariantCulture);

    // ─── RTF ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an RTF document with every decodable image embedded as a PNG hex
    /// blob. Returns null when the pictures are too large, leaving HTML as the
    /// rich format for this copy.
    /// </summary>
    private static byte[]? TryBuildRtf(IReadOnlyList<Block> blocks)
    {
        long totalBytes = 0;
        foreach (var block in blocks)
        {
            if (block.ImagePath == null)
                continue;
            try
            {
                totalBytes += new FileInfo(block.ImagePath).Length;
            }
            catch (IOException)
            {
                return null;
            }
            if (totalBytes > MaxRtfImageBytes)
                return null;
        }

        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\ansicpg936\deff0\nouicompat\deflang2052\deflangfe2052");
        rtf.Append(@"{\fonttbl{\f0\fnil\fcharset134 Microsoft YaHei;}}");
        rtf.Append(@"\uc1\f0\fs28 ");

        foreach (var block in blocks)
        {
            if (block.IsText)
            {
                rtf.Append(EscapeRtfText(block.Text!));
            }
            else
            {
                var picture = BuildRtfPicture(block.ImagePath!);
                if (picture != null)
                    rtf.Append(picture);
            }
            rtf.Append(@"\par ");
        }

        rtf.Append('}');
        return Encoding.ASCII.GetBytes(rtf.ToString());
    }

    private static string? BuildRtfPicture(string imagePath)
    {
        int width, height;
        double dpiX, dpiY;
        byte[] png;

        try
        {
            using var bitmap = System.Drawing.Image.FromFile(imagePath);
            width = bitmap.Width;
            height = bitmap.Height;
            dpiX = bitmap.HorizontalResolution;
            dpiY = bitmap.VerticalResolution;

            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            png = stream.ToArray();
        }
        catch
        {
            // WebP and other codecs System.Drawing cannot decode stay out of
            // the RTF; CF_HTML still references the file directly.
            return null;
        }

        if (width <= 0 || height <= 0 || png.Length == 0)
            return null;

        if (dpiX <= 0) dpiX = 96;
        if (dpiY <= 0) dpiY = 96;

        // \picw / \pich are in 0.01 mm; \picwgoal / \picbgoal are twips (1/20 pt)
        // and decide how large the host draws the picture.
        var picw = (long)Math.Round(width / dpiX * 2540);
        var pich = (long)Math.Round(height / dpiY * 2540);

        var displayWidth = Math.Min(width, MaxDisplayWidthPx);
        var displayHeight = (long)Math.Round((double)height * displayWidth / width);

        var hex = Convert.ToHexString(png).ToLowerInvariant();

        var sb = new StringBuilder(png.Length * 2 + 128);
        sb.Append(@"{\pict\pngblip")
          .Append(@"\picw").Append(picw)
          .Append(@"\pich").Append(pich)
          .Append(@"\picwgoal").Append(displayWidth * 15)
          .Append(@"\picbgoal").Append(displayHeight * 15)
          .Append(' ')
          .Append(hex)
          .Append('}');
        return sb.ToString();
    }

    private static string EscapeRtfText(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\':
                    sb.Append(@"\\");
                    break;
                case '{':
                    sb.Append(@"\{");
                    break;
                case '}':
                    sb.Append(@"\}");
                    break;
                case '\r':
                    break;
                case '\n':
                    sb.Append(@"\par ");
                    break;
                case '\t':
                    sb.Append(@"\tab ");
                    break;
                default:
                    if (c < 0x20)
                        break;
                    if (c < 0x80)
                    {
                        sb.Append(c);
                        break;
                    }
                    // \u takes a signed 16-bit value, which is why a surrogate
                    // pair becomes two escapes. The '?' is the ANSI fallback
                    // glyph consumed by the \uc1 count.
                    sb.Append(@"\u").Append((short)c).Append('?');
                    break;
            }
        }
        return sb.ToString();
    }

    // ─── CF_DIB ─────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes an image to a device-independent bitmap. CF_DIB is the BMP
    /// body, i.e. the file bytes without the 14-byte BITMAPFILEHEADER.
    /// </summary>
    private static byte[]? TryBuildDib(string imagePath)
    {
        try
        {
            using var bitmap = new System.Drawing.Bitmap(imagePath);
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Bmp);
            var bytes = stream.ToArray();
            return bytes.Length > 14 ? bytes[14..] : null;
        }
        catch
        {
            return null;
        }
    }

    // ─── Win32 clipboard ────────────────────────────────────────────────

    /// <summary>
    /// One clipboard format and the exact bytes to put under it.
    /// </summary>
    private sealed record ClipboardItem(uint Format, byte[] Data, string Description)
    {
        public static ClipboardItem FromText(string text) =>
            new(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"), "文本");

        public static ClipboardItem FromHtml(byte[] cfHtml) =>
            new(Register("HTML Format"), cfHtml, "图文");

        public static ClipboardItem FromRtf(byte[] rtf) =>
            new(Register("Rich Text Format"), rtf, "图文");

        public static ClipboardItem FromDib(byte[] dib) =>
            new(CF_DIB, dib, "图片");

        public static ClipboardItem FromDropFiles(IReadOnlyList<string> paths) =>
            new(CF_HDROP, BuildHdrop(paths), "图片文件");
    }

    private static uint Register(string format)
    {
        var id = RegisterClipboardFormat(format);
        if (id == 0)
            throw new ExternalException($"系统剪贴板缺少 {format} 格式。");
        return id;
    }

    /// <summary>
    /// DROPFILES header followed by the double-null terminated wide file list.
    /// </summary>
    private static byte[] BuildHdrop(IReadOnlyList<string> paths)
    {
        var listBytes = Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0");
        var buffer = new byte[HdropHeaderBytes + listBytes.Length];

        BitConverter.GetBytes((uint)HdropHeaderBytes).CopyTo(buffer, 0);   // DWORD pFiles
        BitConverter.GetBytes(0u).CopyTo(buffer, 4);                       // POINT pt.x
        BitConverter.GetBytes(0u).CopyTo(buffer, 8);                       // POINT pt.y
        BitConverter.GetBytes(0u).CopyTo(buffer, 12);                      // BOOL fNC
        BitConverter.GetBytes(1u).CopyTo(buffer, 16);                      // BOOL fWide
        listBytes.CopyTo(buffer, HdropHeaderBytes);

        return buffer;
    }

    /// <summary>
    /// Replaces the clipboard contents with every payload format at once.
    /// Retries briefly because another process can be holding the clipboard
    /// open at the moment of the copy.
    /// </summary>
    private static void Write(IReadOnlyList<ClipboardItem> items)
    {
        var owner = ResolveOwner();
        ExternalException? last = null;

        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(30 * attempt);

            if (!OpenClipboard(owner))
            {
                last = LastError("剪贴板被其他程序占用");
                continue;
            }

            var written = 0;
            try
            {
                if (!EmptyClipboard())
                {
                    last = LastError("无法清空剪贴板");
                    continue;
                }

                foreach (var item in items)
                {
                    var handle = ToGlobal(item.Data);
                    if (SetClipboardData(item.Format, handle) == IntPtr.Zero)
                    {
                        GlobalFree(handle);
                        last = LastError($"写入{item.Description}失败");
                        break;
                    }
                    written++;
                }

                // Half a payload would paste as half a script, so only accept
                // the attempt when everything the caller asked for is on board.
                if (written == items.Count)
                    return;
            }
            finally
            {
                CloseClipboard();
            }
        }

        throw last ?? new ExternalException("剪贴板写入失败。");
    }

    private static IntPtr ToGlobal(byte[] data)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero)
            throw new OutOfMemoryException("剪贴板内存不足。");

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw LastError("无法锁定剪贴板内存");
        }

        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    /// <summary>
    /// EmptyClipboard only takes ownership when OpenClipboard was given a real
    /// window handle, so fall back to the foreground window.
    /// </summary>
    private static IntPtr ResolveOwner()
    {
        var window = System.Windows.Application.Current?.MainWindow;
        var handle = window != null
            ? new System.Windows.Interop.WindowInteropHelper(window).Handle
            : IntPtr.Zero;
        return handle != IntPtr.Zero ? handle : GetForegroundWindow();
    }

    private static ExternalException LastError(string message) =>
        new($"{message}（Win32 {Marshal.GetLastWin32Error()}）");

    // ─── P/Invoke ───────────────────────────────────────────────────────

    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int HdropHeaderBytes = 20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
