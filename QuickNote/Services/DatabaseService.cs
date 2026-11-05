using Microsoft.Data.Sqlite;
using QuickNote.Models;
using System.IO;
using System.Text.Json;

namespace QuickNote.Services;

/// <summary>
/// SQLite-backed local storage for scripts (话术) and their images.
/// </summary>
public class DatabaseService : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>
    /// Base directory for app data (db + images).
    /// </summary>
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickNote");

    /// <summary>
    /// Directory where script images are stored.
    /// </summary>
    public static string ImagesDir { get; } = Path.Combine(AppDataDir, "images");

    public DatabaseService()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(ImagesDir);

        var dbPath = Path.Combine(AppDataDir, "scripts.db");
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        // WAL lets several QuickNote processes read/write the same db safely;
        // busy_timeout makes concurrent writers wait instead of throwing.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Scripts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL DEFAULT '',
                Content TEXT NOT NULL DEFAULT '',
                Category TEXT NOT NULL DEFAULT '',
                ImagePaths TEXT NOT NULL DEFAULT '[]',
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT '',
                UpdatedAt TEXT NOT NULL DEFAULT '',
                Segments TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_scripts_category ON Scripts(Category);
            CREATE INDEX IF NOT EXISTS idx_scripts_sort ON Scripts(SortOrder);

            CREATE TABLE IF NOT EXISTS ClipboardHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Content TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                Segments TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS FolderCategories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL DEFAULT '',
                Path TEXT NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT ''
            );
        """;
        cmd.ExecuteNonQuery();

        EnsureSegmentsColumn();
        EnsureClipboardSegmentsColumn();
    }

    private void EnsureSegmentsColumn() => EnsureColumn("Scripts", "Segments");

    private void EnsureClipboardSegmentsColumn() => EnsureColumn("ClipboardHistory", "Segments");

    /// <summary>
    /// Adds a column that databases from an older build predate. Both arguments
    /// are literals from this file, never user input.
    /// </summary>
    private void EnsureColumn(string table, string column)
    {
        using (var check = _connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT NOT NULL DEFAULT ''";
        alter.ExecuteNonQuery();
    }

    // ─── CRUD ───────────────────────────────────────────────────────────

    public List<Script> GetAllScripts()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, Content, Category, ImagePaths, SortOrder, CreatedAt, UpdatedAt, Segments FROM Scripts ORDER BY SortOrder ASC, UpdatedAt DESC";
        return ReadScripts(cmd);
    }

    private static bool IsAllCategory(string category) =>
        string.IsNullOrEmpty(category) || category == "全部";

    public int CountScripts(string category)
    {
        using var cmd = _connection.CreateCommand();
        if (IsAllCategory(category))
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Scripts";
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Scripts WHERE Category = $cat";
            cmd.Parameters.AddWithValue("$cat", category);
        }
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// One page of scripts; the UI never holds more than a few pages worth of
    /// objects in memory even with thousands of rows.
    /// </summary>
    public List<Script> GetScriptsPage(string category, int offset, int limit)
    {
        using var cmd = _connection.CreateCommand();
        if (IsAllCategory(category))
        {
            cmd.CommandText = """
                SELECT Id, Title, Content, Category, ImagePaths, SortOrder, CreatedAt, UpdatedAt, Segments FROM Scripts
                ORDER BY SortOrder ASC, UpdatedAt DESC
                LIMIT $limit OFFSET $offset
            """;
        }
        else
        {
            cmd.CommandText = """
                SELECT Id, Title, Content, Category, ImagePaths, SortOrder, CreatedAt, UpdatedAt, Segments FROM Scripts
                WHERE Category = $cat
                ORDER BY SortOrder ASC, UpdatedAt DESC
                LIMIT $limit OFFSET $offset
            """;
            cmd.Parameters.AddWithValue("$cat", category);
        }
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return ReadScripts(cmd);
    }

    public int CountSearchScripts(string keyword)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM Scripts
            WHERE Title LIKE $kw OR Content LIKE $kw OR Category LIKE $kw
        """;
        cmd.Parameters.AddWithValue("$kw", $"%{keyword}%");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<Script> SearchScriptsPage(string keyword, int offset, int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Title, Content, Category, ImagePaths, SortOrder, CreatedAt, UpdatedAt, Segments FROM Scripts
            WHERE Title LIKE $kw OR Content LIKE $kw OR Category LIKE $kw
            ORDER BY SortOrder ASC, UpdatedAt DESC
            LIMIT $limit OFFSET $offset
        """;
        cmd.Parameters.AddWithValue("$kw", $"%{keyword}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return ReadScripts(cmd);
    }

    public Script? GetScript(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, Content, Category, ImagePaths, SortOrder, CreatedAt, UpdatedAt, Segments FROM Scripts WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadScripts(cmd).FirstOrDefault();
    }

    public Script InsertScript(Script script)
    {
        using var cmd = _connection.CreateCommand();
        var now = DateTime.Now.ToString("o");
        cmd.CommandText = """
            INSERT INTO Scripts (Title, Content, Category, ImagePaths, SortOrder, CreatedAt, UpdatedAt, Segments)
            VALUES ($title, $content, $category, $images, $sort, $created, $updated, $segments);
            SELECT last_insert_rowid();
        """;
        cmd.Parameters.AddWithValue("$title", script.Title);
        cmd.Parameters.AddWithValue("$content", script.Content);
        cmd.Parameters.AddWithValue("$category", script.Category);
        cmd.Parameters.AddWithValue("$images", script.ImagePaths);
        cmd.Parameters.AddWithValue("$sort", script.SortOrder);
        cmd.Parameters.AddWithValue("$created", now);
        cmd.Parameters.AddWithValue("$updated", now);
        cmd.Parameters.AddWithValue("$segments", script.Segments);
        script.Id = Convert.ToInt64(cmd.ExecuteScalar());
        script.CreatedAt = DateTime.Parse(now);
        script.UpdatedAt = DateTime.Parse(now);
        return script;
    }

    public void UpdateScript(Script script)
    {
        using var cmd = _connection.CreateCommand();
        script.UpdatedAt = DateTime.Now;
        cmd.CommandText = """
            UPDATE Scripts SET
                Title = $title, Content = $content, Category = $category,
                ImagePaths = $images, SortOrder = $sort, UpdatedAt = $updated,
                Segments = $segments
            WHERE Id = $id
        """;
        cmd.Parameters.AddWithValue("$title", script.Title);
        cmd.Parameters.AddWithValue("$content", script.Content);
        cmd.Parameters.AddWithValue("$category", script.Category);
        cmd.Parameters.AddWithValue("$images", script.ImagePaths);
        cmd.Parameters.AddWithValue("$sort", script.SortOrder);
        cmd.Parameters.AddWithValue("$updated", script.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$segments", script.Segments);
        cmd.Parameters.AddWithValue("$id", script.Id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteScript(long id)
    {
        // Delete associated images
        var script = GetScript(id);
        if (script != null)
        {
            foreach (var img in script.ImageFileNames)
                RemoveImage(img);
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Scripts WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ─── Categories ─────────────────────────────────────────────────────

    public List<string> GetAllCategories()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Category FROM Scripts WHERE Category != '' ORDER BY Category ASC";
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var cat = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(cat))
                list.Add(cat);
        }
        return list;
    }

    // ─── Clipboard history ──────────────────────────────────────────────

    /// <summary>
    /// Adds a clipboard snapshot, moving an identical older entry to the top,
    /// and keeps only the latest 20 entries.
    /// </summary>
    /// <param name="segments">
    /// Ordered content QuickNote actually copied, so the row can put the
    /// pictures back on the clipboard. A script with no images has none and is
    /// re-copied as text.
    /// </param>
    public void AddClipboardEntry(string content, IReadOnlyList<ContentSegment>? segments = null)
    {
        var segmentsJson = segments is { Count: > 0 } ? JsonSerializer.Serialize(segments) : "";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM ClipboardHistory WHERE Content = $content AND Segments = $segments;
            INSERT INTO ClipboardHistory (Content, CreatedAt, Segments) VALUES ($content, $now, $segments);
            DELETE FROM ClipboardHistory WHERE Id NOT IN (
                SELECT Id FROM ClipboardHistory ORDER BY Id DESC LIMIT 20
            );
        """;
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$segments", segmentsJson);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<ClipboardEntry> GetClipboardEntries()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Content, CreatedAt, Segments FROM ClipboardHistory ORDER BY Id DESC LIMIT 20";
        var list = new List<ClipboardEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ClipboardEntry
            {
                Id = reader.GetInt64(0),
                Content = reader.GetString(1),
                CreatedAt = ParseDateTime(reader.GetString(2)),
                Segments = reader.IsDBNull(3) ? "" : reader.GetString(3),
            });
        }
        return list;
    }

    public void ClearClipboardHistory()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ClipboardHistory";
        cmd.ExecuteNonQuery();
    }

    // ─── Folder categories ──────────────────────────────────────────────

    public List<FolderCategory> GetFolderCategories()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Path FROM FolderCategories ORDER BY SortOrder ASC, Id ASC";
        var list = new List<FolderCategory>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FolderCategory
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Path = reader.GetString(2),
            });
        }
        return list;
    }

    public FolderCategory InsertFolderCategory(string name, string path, int sortOrder)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO FolderCategories (Name, Path, SortOrder, CreatedAt)
            VALUES ($name, $path, $sort, $created);
            SELECT last_insert_rowid();
        """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$sort", sortOrder);
        cmd.Parameters.AddWithValue("$created", DateTime.Now.ToString("o"));
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        return new FolderCategory { Id = id, Name = name, Path = path };
    }

    public void DeleteFolderCategory(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM FolderCategories WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ─── Image helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Copy an image file into the images directory and return its stored filename.
    /// </summary>
    public string StoreImage(string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp"))
            ext = ".png";

        var fileName = $"{Guid.NewGuid()}{ext}";
        var destPath = Path.Combine(ImagesDir, fileName);
        File.Copy(sourcePath, destPath, overwrite: true);
        return fileName;
    }

    /// <summary>
    /// Store image from base64 data (for import).
    /// </summary>
    public string StoreImageFromBase64(string base64, string originalExt)
    {
        var ext = originalExt.ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp"))
            ext = ".png";

        var fileName = $"{Guid.NewGuid()}{ext}";
        var destPath = Path.Combine(ImagesDir, fileName);
        File.WriteAllBytes(destPath, Convert.FromBase64String(base64));
        return fileName;
    }

    /// <summary>
    /// Get full path for a stored image filename.
    /// </summary>
    public static string GetImagePath(string fileName)
    {
        return Path.Combine(ImagesDir, fileName);
    }

    /// <summary>
    /// Delete a stored image file.
    /// </summary>
    public void RemoveImage(string fileName)
    {
        var path = Path.Combine(ImagesDir, fileName);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A viewer may still hold the file briefly; leaving an orphan is
            // better than failing the whole save operation.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ─── Import / Export ────────────────────────────────────────────────

    /// <summary>
    /// Export all scripts to a JSON file, embedding images as base64.
    /// </summary>
    public void ExportToJson(string filePath)
    {
        var scripts = GetAllScripts();
        var exportData = new ExportData
        {
            Version = "1.0",
            ExportTime = DateTime.Now.ToString("o"),
            Scripts = scripts.Select(s => new ExportScript
            {
                Title = s.Title,
                Content = s.Content,
                Category = s.Category,
                SortOrder = s.SortOrder,
                Segments = s.SegmentsList,
                Images = s.ImageFileNames.Select(img =>
                {
                    var path = GetImagePath(img);
                    if (!File.Exists(path)) return new ExportImage { FileName = img };
                    return new ExportImage
                    {
                        FileName = img,
                        Base64 = Convert.ToBase64String(File.ReadAllBytes(path)),
                    };
                }).ToList(),
            }).ToList(),
        };

        var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Import scripts from a JSON file, restoring images.
    /// </summary>
    public int ImportFromJson(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var data = JsonSerializer.Deserialize<ExportData>(json);
        if (data?.Scripts == null) return 0;

        int count = 0;
        foreach (var es in data.Scripts)
        {
            var imageFileNames = new List<string>();
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (es.Images != null)
            {
                foreach (var img in es.Images)
                {
                    if (!string.IsNullOrEmpty(img.Base64))
                    {
                        var ext = Path.GetExtension(img.FileName);
                        var stored = StoreImageFromBase64(img.Base64, ext);
                        imageFileNames.Add(stored);
                        if (!string.IsNullOrEmpty(img.FileName))
                            renamed[img.FileName] = stored;
                    }
                }
            }

            var segments = new List<ContentSegment>();
            foreach (var seg in es.Segments ?? [])
            {
                if (seg.IsImage)
                {
                    if (renamed.TryGetValue(seg.Value, out var stored))
                        segments.Add(ContentSegment.Image(stored));
                }
                else if (!string.IsNullOrWhiteSpace(seg.Value))
                {
                    segments.Add(ContentSegment.Text(seg.Value));
                }
            }

            if (segments.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(es.Content))
                    segments.Add(ContentSegment.Text(es.Content.Trim()));
                segments.AddRange(imageFileNames.Select(ContentSegment.Image));
            }

            var script = new Script
            {
                Title = es.Title ?? "",
                Content = es.Content ?? "",
                Category = es.Category ?? "",
                ImagePaths = JsonSerializer.Serialize(imageFileNames.Distinct().ToList()),
                Segments = JsonSerializer.Serialize(segments),
                SortOrder = es.SortOrder,
            };
            InsertScript(script);
            count++;
        }
        return count;
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private List<Script> ReadScripts(SqliteCommand cmd)
    {
        var list = new List<Script>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Script
            {
                Id = reader.GetInt64(0),
                Title = reader.GetString(1),
                Content = reader.GetString(2),
                Category = reader.GetString(3),
                ImagePaths = reader.GetString(4),
                SortOrder = reader.GetInt32(5),
                CreatedAt = ParseDateTime(reader.GetString(6)),
                UpdatedAt = ParseDateTime(reader.GetString(7)),
                Segments = reader.IsDBNull(8) ? "" : reader.GetString(8),
            });
        }
        return list;
    }

    private static DateTime ParseDateTime(string s)
    {
        return DateTime.TryParse(s, out var dt) ? dt : DateTime.Now;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}

// ─── Import/Export DTOs ─────────────────────────────────────────────────

public class ExportData
{
    public string Version { get; set; } = "1.0";
    public string ExportTime { get; set; } = "";
    public List<ExportScript> Scripts { get; set; } = [];
}

public class ExportScript
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "";
    public int SortOrder { get; set; }
    public List<ContentSegment>? Segments { get; set; }
    public List<ExportImage>? Images { get; set; }
}

public class ExportImage
{
    public string FileName { get; set; } = "";
    public string? Base64 { get; set; }
}
