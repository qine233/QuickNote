using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using QuickNote.Models;

namespace QuickNote.Services;

/// <summary>
/// Imports scripts exported by 易歪歪 (Yiyiwa). Supports its local SQLite
/// database (database.db + image folder), a .ebk backup that contains that
/// database, and Excel exports (text only).
/// </summary>
public class YybImportService
{
    private readonly DatabaseService _db;

    public YybImportService(DatabaseService db)
    {
        _db = db;
    }

    /// <summary>
    /// Import from a Yiyiwa account folder's database.db. Images are read from
    /// the sibling "image" directory.
    /// </summary>
    public int ImportFromDatabase(string dbPath)
    {
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".";
        var imageDir = Path.Combine(sourceDir, "image");

        // Work on a copy so a running 易歪歪 instance never gets locked.
        var tempDb = Path.Combine(Path.GetTempPath(), $"quicknote_yyw_{Guid.NewGuid():N}.db");
        File.Copy(dbPath, tempDb, overwrite: true);
        try
        {
            return ImportCore(tempDb, imageDir);
        }
        finally
        {
            TryDelete(tempDb);
        }
    }

    /// <summary>
    /// Import from a .ebk backup. The format is not documented; backups seen so
    /// far are ZIP archives containing the Yiyiwa database and image folder.
    /// </summary>
    public int ImportFromEbk(string ebkPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"quicknote_ebk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var zip = ZipFile.OpenRead(ebkPath);

            var dbEntry = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("database.db", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
            if (dbEntry == null)
                throw new NotSupportedException(
                    "这个 .ebk 不是可识别的易歪歪备份（未找到数据库文件）。可以改选易歪歪安装目录下的 database.db 导入。");

            var tempDb = Path.Combine(tempDir, "database.db");
            dbEntry.ExtractToFile(tempDb, overwrite: true);

            var imageDir = Path.Combine(tempDir, "image");
            Directory.CreateDirectory(imageDir);
            var dbPathInZip = dbEntry.FullName.Replace('\\', '/');
            var slash = dbPathInZip.LastIndexOf('/');
            var dbPrefix = slash >= 0 ? dbPathInZip[..(slash + 1)] : "";
            var imagePrefix = dbPrefix + "image/";

            foreach (var entry in zip.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/');
                if (normalized.EndsWith('/')) continue;
                if (!normalized.StartsWith(imagePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                var fileName = Path.GetFileName(normalized);
                if (string.IsNullOrEmpty(fileName)) continue;
                entry.ExtractToFile(Path.Combine(imageDir, fileName), overwrite: true);
            }

            return ImportCore(tempDb, imageDir);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Import an Excel export. Excel files from 易歪歪 contain text only.
    /// Expects a header row with columns such as 分类 / 标题 / 内容.
    /// </summary>
    public int ImportFromExcel(string xlsxPath)
    {
        using var zip = ZipFile.OpenRead(xlsxPath);

        var sharedStrings = ReadSharedStrings(zip);
        var sheet = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Excel 文件中没有找到工作表。");

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XDocument doc;
        using (var stream = sheet.Open())
            doc = XDocument.Load(stream);

        var rows = new List<Dictionary<int, string>>();
        foreach (var row in doc.Descendants(ns + "row"))
        {
            var cells = new Dictionary<int, string>();
            var implicitIndex = 0;
            foreach (var cell in row.Elements(ns + "c"))
            {
                var reference = (string?)cell.Attribute("r");
                var column = reference == null ? implicitIndex : ColumnIndex(reference);
                implicitIndex = column + 1;

                var type = (string?)cell.Attribute("t");
                string value;
                if (type == "s")
                {
                    var indexText = cell.Element(ns + "v")?.Value;
                    value = int.TryParse(indexText, out var idx) && idx >= 0 && idx < sharedStrings.Count
                        ? sharedStrings[idx]
                        : "";
                }
                else if (type == "inlineStr")
                {
                    value = string.Concat(cell.Descendants(ns + "t").Select(t => t.Value));
                }
                else
                {
                    value = cell.Element(ns + "v")?.Value ?? "";
                }

                if (!string.IsNullOrWhiteSpace(value))
                    cells[column] = value.Trim();
            }

            if (cells.Count > 0)
                rows.Add(cells);
        }

        var headerIndex = rows.FindIndex(r => r.Values.Any(v =>
            v.Contains("内容") || v.Contains("话术") || v.Contains("分类") || v.Contains("标题")));
        if (headerIndex < 0)
            throw new InvalidDataException("没有识别到表头。请确认 Excel 第一行包含「分类」「标题」「内容」等列名。");

        var header = rows[headerIndex];
        int categoryCol = -1, titleCol = -1, contentCol = -1;
        foreach (var (col, name) in header)
        {
            if (categoryCol < 0 && IsCategoryHeader(name)) categoryCol = col;
            else if (titleCol < 0 && IsTitleHeader(name)) titleCol = col;
            else if (contentCol < 0 && IsContentHeader(name)) contentCol = col;
        }

        if (contentCol < 0)
            throw new InvalidDataException("Excel 中未找到「内容」或「话术」列，无法导入。");

        var count = 0;
        var sort = 0;
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            row.TryGetValue(contentCol, out var content);
            row.TryGetValue(titleCol, out var title);
            row.TryGetValue(categoryCol, out var category);
            if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(title)) continue;

            content = content?.Trim() ?? "";
            title = title?.Trim() ?? "";

            var excelSegments = new List<ContentSegment>();
            if (!string.IsNullOrWhiteSpace(content))
                excelSegments.Add(ContentSegment.Text(content));

            _db.InsertScript(new Script
            {
                Title = title,
                Content = content,
                Category = category?.Trim() ?? "",
                Segments = JsonSerializer.Serialize(excelSegments),
                SortOrder = sort++,
            });
            count++;
        }

        return count;
    }

    // ─── Core SQLite import ─────────────────────────────────────────────

    private int ImportCore(string dbPath, string imageDir)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        var categories = new Dictionary<string, string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT sign, label FROM word_class";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var sign = reader.GetString(0);
                var label = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                if (!string.IsNullOrEmpty(sign))
                    categories[sign] = label;
            }
        }

        var count = 0;
        using (var cmd = conn.CreateCommand())
        {
            // type = 1 rows are section dividers / headings, not real scripts.
            cmd.CommandText = """
                SELECT parentSign, label, jsonValue, texts, images, position
                FROM word_list
                WHERE type <> 1
                ORDER BY position
            """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var parentSign = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var label = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                var jsonValue = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var texts = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var imagesField = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var position = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5));

                var (segments, imageNames) = ParseContent(jsonValue, texts, imagesField);
                if (segments.Count == 0)
                    continue;

                var storedImages = new List<string>();
                var imageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in imageNames)
                {
                    var source = Path.Combine(imageDir, name);
                    if (!File.Exists(source)) continue;
                    try
                    {
                        var stored = _db.StoreImage(source);
                        storedImages.Add(stored);
                        imageMap[name] = stored;
                    }
                    catch { /* skip unreadable image */ }
                }

                var finalSegments = new List<ContentSegment>();
                foreach (var seg in segments)
                {
                    if (seg.IsImage)
                    {
                        if (imageMap.TryGetValue(seg.Value, out var stored))
                            finalSegments.Add(ContentSegment.Image(stored));
                    }
                    else if (!string.IsNullOrWhiteSpace(seg.Value))
                    {
                        finalSegments.Add(seg);
                    }
                }

                var content = string.Join("\n", finalSegments
                    .Where(seg => !seg.IsImage)
                    .Select(seg => seg.Value)).Trim();

                _db.InsertScript(new Script
                {
                    Title = label,
                    Content = content,
                    Category = categories.TryGetValue(parentSign, out var cat) ? cat : "",
                    ImagePaths = JsonSerializer.Serialize(storedImages.Distinct().ToList()),
                    Segments = JsonSerializer.Serialize(finalSegments),
                    SortOrder = position,
                });
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Yiyiwa stores content as an ordered JSON array of text/image segments.
    /// Returns the segments in their original order; images keep their source
    /// file names (the caller maps them to stored copies).
    /// </summary>
    private static (List<ContentSegment> Segments, List<string> Images) ParseContent(
        string json, string texts, string imagesField)
    {
        var segments = new List<ContentSegment>();
        var images = new List<string>();

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var segment in doc.RootElement.EnumerateArray())
                    {
                        var type = segment.TryGetProperty("type", out var t) && t.TryGetInt32(out var tv) ? tv : 1;
                        var value = segment.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                        if (type == 2)
                        {
                            var name = value.Trim();
                            if (name.Length == 0) continue;
                            if (!images.Contains(name, StringComparer.OrdinalIgnoreCase))
                                images.Add(name);
                            segments.Add(ContentSegment.Image(name));
                        }
                        else if (!string.IsNullOrWhiteSpace(value))
                        {
                            segments.Add(ContentSegment.Text(value.Trim()));
                        }
                    }
                }
            }
            catch (JsonException)
            {
                segments.Clear();
                images.Clear();
            }
        }

        if (!segments.Any(s => !s.IsImage) && !string.IsNullOrWhiteSpace(texts))
            segments.Insert(0, ContentSegment.Text(texts.Trim()));

        if (!segments.Any(s => s.IsImage) && !string.IsNullOrWhiteSpace(imagesField))
        {
            foreach (var name in imagesField
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (images.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                images.Add(name);
                segments.Add(ContentSegment.Image(name));
            }
        }

        return (segments, images);
    }

    // ─── Excel helpers ─────────────────────────────────────────────────

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase));
        if (entry == null) return result;

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var si in doc.Descendants(ns + "si"))
            result.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
        return result;
    }

    private static int ColumnIndex(string cellReference)
    {
        var index = 0;
        foreach (var ch in cellReference)
        {
            if (ch is >= 'A' and <= 'Z') index = index * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') index = index * 26 + (ch - 'a' + 1);
            else break;
        }
        return Math.Max(0, index - 1);
    }

    private static bool IsCategoryHeader(string name) =>
        new[] { "分类", "类别", "分组", "目录", "所属分类", "分类名称" }
            .Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool IsTitleHeader(string name) =>
        new[] { "标题", "名称", "主题", "话术标题" }
            .Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool IsContentHeader(string name) =>
        new[] { "内容", "话术", "文本", "回复" }
            .Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
