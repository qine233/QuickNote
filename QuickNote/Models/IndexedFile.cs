using System.IO;
using System.Windows.Media;

namespace QuickNote.Models;

/// <summary>
/// One file discovered while indexing a folder category. Files are listed on
/// demand and never persisted.
/// </summary>
public class IndexedFile
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public long Size { get; init; }
    public DateTime ModifiedAt { get; init; }

    public string Glyph => Extension switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => "\uE8B9",
        ".mp4" or ".avi" or ".mov" or ".mkv" or ".wmv" or ".flv" => "\uE714",
        ".mp3" or ".wav" or ".m4a" or ".flac" or ".aac" => "\uE8D6",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\uE8B7",
        ".exe" or ".msi" => "\uE713",
        _ => "\uE8A5",
    };

    public Brush IconForeground => Extension switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => OrangeFg,
        ".mp4" or ".avi" or ".mov" or ".mkv" or ".wmv" or ".flv" => PurpleFg,
        ".mp3" or ".wav" or ".m4a" or ".flac" or ".aac" => PinkFg,
        ".pdf" => RedFg,
        ".doc" or ".docx" or ".txt" or ".rtf" or ".md" => BlueFg,
        ".xls" or ".xlsx" or ".csv" => GreenFg,
        ".ppt" or ".pptx" => OrangeFg,
        _ => GrayFg,
    };

    public Brush IconBackground => Extension switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => OrangeBg,
        ".mp4" or ".avi" or ".mov" or ".mkv" or ".wmv" or ".flv" => PurpleBg,
        ".mp3" or ".wav" or ".m4a" or ".flac" or ".aac" => PinkBg,
        ".pdf" => RedBg,
        ".doc" or ".docx" or ".txt" or ".rtf" or ".md" => BlueBg,
        ".xls" or ".xlsx" or ".csv" => GreenBg,
        ".ppt" or ".pptx" => OrangeBg,
        _ => GrayBg,
    };

    public string DetailText => $"{TypeText} · {SizeText} · {ModifiedAt:yyyy-MM-dd HH:mm}";

    private string Extension
    {
        get
        {
            try { return Path.GetExtension(Name).ToLowerInvariant(); }
            catch { return ""; }
        }
    }

    private string TypeText => Extension switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => "图片",
        ".mp4" or ".avi" or ".mov" or ".mkv" or ".wmv" or ".flv" => "视频",
        ".mp3" or ".wav" or ".m4a" or ".flac" or ".aac" => "音频",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "压缩包",
        ".pdf" => "PDF",
        ".doc" or ".docx" => "Word",
        ".xls" or ".xlsx" => "Excel",
        ".ppt" or ".pptx" => "演示文稿",
        ".txt" => "文本",
        "" => "文件",
        var ext => ext.TrimStart('.').ToUpperInvariant(),
    };

    private string SizeText
    {
        get
        {
            double size = Size;
            if (size < 1024) return $"{size:0} B";
            if (size < 1024 * 1024) return $"{size / 1024:0.#} KB";
            if (size < 1024L * 1024 * 1024) return $"{size / (1024 * 1024):0.#} MB";
            return $"{size / (1024L * 1024 * 1024):0.#} GB";
        }
    }

    // Frozen shared brushes: one instance per color instead of one per row.
    private static readonly Brush OrangeFg = Frozen("#FF9500");
    private static readonly Brush OrangeBg = Frozen("#FFF1E0");
    private static readonly Brush PurpleFg = Frozen("#AF52DE");
    private static readonly Brush PurpleBg = Frozen("#F5ECFB");
    private static readonly Brush PinkFg = Frozen("#FF2D55");
    private static readonly Brush PinkBg = Frozen("#FFECF1");
    private static readonly Brush RedFg = Frozen("#FF3B30");
    private static readonly Brush RedBg = Frozen("#FFEBEA");
    private static readonly Brush BlueFg = Frozen("#007AFF");
    private static readonly Brush BlueBg = Frozen("#E8F2FF");
    private static readonly Brush GreenFg = Frozen("#34C759");
    private static readonly Brush GreenBg = Frozen("#E9F9EE");
    private static readonly Brush GrayFg = Frozen("#8E8E93");
    private static readonly Brush GrayBg = Frozen("#EFEFF4");

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
