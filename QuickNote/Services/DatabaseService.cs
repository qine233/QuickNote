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

    // When a batch is open every write joins it: one commit for thousands of
    // imported rows instead of one fsync per row.
    private SqliteTransaction? _batch;

    // Positions are spaced out so a row can be dropped between two neighbours
    // without rewriting anything else.
    private const int SortStep = 1000;

    // The list order used everywhere. SortOrder stays unique thanks to
    // NormalizeSortOrders, the other two only break ties from old imports.
    private const string OrderBy = "ORDER BY SortOrder ASC, UpdatedAt DESC, Id ASC";

    // Bump when the tokenizer or the indexing scheme changes; the index is then
    // rebuilt once on the next start instead of silently drifting.
    private const string SearchIndexVersion = "1";

    /// <summary>File name of the SQLite database inside <see cref="AppDataDir"/>.</summary>
    public const string DatabaseFileName = "scripts.db";

    /// <summary>
    /// Base directory for the data (db + images). Chosen per settings: this
    /// account only, every account on the machine, or a folder the user picked.
    /// </summary>
    public static string AppDataDir { get; } = DataLocation.Resolve(AppSettings.Current);

    /// <summary>
    /// Directory where script images are stored.
    /// </summary>
    public static string ImagesDir { get; } = Path.Combine(AppDataDir, "images");

    /// <summary>
    /// True when this SQLite build has FTS5 and the search index is usable.
    /// A build without it keeps working through the old substring scan.
    /// </summary>
    public bool FullTextSearchAvailable { get; private set; }

    public DatabaseService()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(ImagesDir);

        var dbPath = Path.Combine(AppDataDir, DatabaseFileName);
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        // WAL lets several QuickNote processes on this machine read and write
        // the same db safely, and busy_timeout makes a concurrent writer wait
        // for the lock instead of throwing. WAL needs shared memory, which does
        // not work over SMB, so a network folder keeps the rollback journal.
        var network = DataLocation.IsNetworkPath(dbPath);
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = network
                ? "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000;"
                : "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=10000;";
            pragma.ExecuteNonQuery();
        }

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var cmd = CreateCommand();
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
            CREATE INDEX IF NOT EXISTS idx_scripts_order ON Scripts(SortOrder, UpdatedAt DESC);

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

            CREATE TABLE IF NOT EXISTS Meta (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL DEFAULT ''
            );
        """;
        cmd.ExecuteNonQuery();

        EnsureSegmentsColumn();
        EnsureClipboardSegmentsColumn();

        FullTextSearchAvailable = EnsureSearchTable();
        EnsureSearchIndex();
        EnsureOrderedPositions();
    }

    private void EnsureSegmentsColumn() => EnsureColumn("Scripts", "Segments");

    private void EnsureClipboardSegmentsColumn() => EnsureColumn("ClipboardHistory", "Segments");

    /// <summary>
    /// Adds a column that databases from an older build predate. Both arguments
    /// are literals from this file, never user input.
    /// </summary>
    private void EnsureColumn(string table, string column)
    {
        using (var check = CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alter = CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT NOT NULL DEFAULT ''";
        alter.ExecuteNonQuery();
    }

    // ─── Search index ───────────────────────────────────────────────────

    /// <summary>
    /// Creates the full-text table that keeps a search independent of how many
    /// scripts exist. Old SQLite builds without FTS5 simply keep the LIKE scan.
    /// </summary>
    private bool EnsureSearchTable()
    {
        try
        {
            using var cmd = CreateCommand();
            cmd.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS ScriptsFts USING fts5(SearchText, tokenize = 'unicode61');";
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fills the index once for databases written by builds that did not have
    /// one. Normal edits keep it current, so this does nothing after the first
    /// successful run.
    /// </summary>
    private void EnsureSearchIndex()
    {
        if (!FullTextSearchAvailable)
            return;
        if (GetMeta("search_index") == SearchIndexVersion)
            return;

        try
        {
            RebuildSearchIndex();
        }
        catch (SqliteException)
        {
            // A corrupt index must never stop the app from opening. Drop it,
            // search through the plain scan for this session and let the next
            // start try again with a clean table.
            FullTextSearchAvailable = false;
            try
            {
                Execute("DROP TABLE IF EXISTS ScriptsFts");
            }
            catch (SqliteException)
            {
            }
        }
    }

    /// <summary>
    /// Rebuilds the whole search index from scratch. Batched reads keep memory
    /// flat no matter how many scripts there are.
    /// </summary>
    public void RebuildSearchIndex()
    {
        if (!FullTextSearchAvailable)
            return;

        RunInTransaction(() =>
        {
            Execute("DELETE FROM ScriptsFts");

            long lastId = 0;
            while (true)
            {
                var page = ReadSearchSourcePage(lastId, 500);
                if (page.Count == 0)
                    break;

                foreach (var row in page)
                    WriteSearchRow(row.Id, row.Title, row.Content, row.Category);

                lastId = page[^1].Id;
                if (page.Count < 500)
                    break;
            }

            SetMeta("search_index", SearchIndexVersion);
        });
    }

    private List<(long Id, string Title, string Content, string Category)> ReadSearchSourcePage(long afterId, int limit)
    {
        var rows = new List<(long, string, string, string)>(limit);
        using var cmd = CreateCommand();
        cmd.CommandText = "SELECT Id, Title, Content, Category FROM Scripts WHERE Id > $id ORDER BY Id LIMIT $limit";
        cmd.Parameters.AddWithValue("$id", afterId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return rows;
    }

    /// <summary>
    /// Writes (or rewrites) the index row for a script.
    /// </summary>
    private void WriteSearchRow(Script script)
    {
        WriteSearchRow(script.Id, script.Title, script.Content, script.Category);
    }

    private void WriteSearchRow(long id, string title, string content, string category)
    {
        if (!FullTextSearchAvailable)
            return;

        RemoveSearchRow(id);

        using var cmd = CreateCommand();
        cmd.CommandText = "INSERT INTO ScriptsFts (rowid, SearchText) VALUES ($id, $text)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$text", ScriptSearch.BuildIndexText(title, content, category));
        cmd.ExecuteNonQuery();
    }

    private void RemoveSearchRow(long id)
    {
        if (!FullTextSearchAvailable)
            return;

        using var cmd = CreateCommand();
        cmd.CommandText = "DELETE FROM ScriptsFts WHERE rowid = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ─── List positions ─────────────────────────────────────────────────

    /// <summary>
    /// Old databases share SortOrder values (every import kept its source
    /// numbers), which leaves no room to insert between two rows. Renumbering
    /// once, in the order the list already shows, fixes that without moving
    /// anything the user can see.
    /// </summary>
    private void EnsureOrderedPositions()
    {
        if (GetMeta("sort_order") == "1")
            return;

        RunInTransaction(() =>
        {
            NormalizeSortOrders();
            SetMeta("sort_order", "1");
        });
    }

    /// <summary>
    /// Rewrites SortOrder as 1000, 2000, 3000... in the current display order.
    /// Only runs when positions collide or a gap has run out.
    /// </summary>
    public void NormalizeSortOrders()
    {
        var ids = new List<long>();
        using (var cmd = CreateCommand())
        {
            cmd.CommandText = $"SELECT Id FROM Scripts {OrderBy}";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
        }

        RunInTransaction(() =>
        {
            using var cmd = CreateCommand();
            cmd.CommandText = "UPDATE Scripts SET SortOrder = $sort WHERE Id = $id";
            var sortParameter = cmd.Parameters.Add("$sort", SqliteType.Integer);
            var idParameter = cmd.Parameters.Add("$id", SqliteType.Integer);

            // 1000 apart is plenty of room; a library large enough for that to
            // overflow int gets tighter spacing instead.
            var step = ids.Count * (long)SortStep <= int.MaxValue
                ? SortStep
                : Math.Max(1, int.MaxValue / Math.Max(ids.Count, 1));

            long sort = step;
            foreach (var id in ids)
            {
                sortParameter.Value = sort;
                idParameter.Value = id;
                cmd.ExecuteNonQuery();
                sort += step;
            }
        });
    }

    /// <summary>
    /// Position for a script appended to the end of the list.
    /// </summary>
    public int NextSortOrder()
    {
        var max = ScalarLong("SELECT COALESCE(MAX(SortOrder), 0) FROM Scripts");
        return (int)Math.Min(max + SortStep, int.MaxValue);
    }

    /// <summary>
    /// Inserts a script directly above or below <paramref name="anchorId"/>.
    /// Neighbouring rows are left alone: the new position is the midpoint of
    /// the gap in front of (or after) the anchor. The table is only renumbered
    /// when that gap has run out.
    /// </summary>
    public Script InsertScriptNear(Script script, long anchorId, bool above)
    {
        RunInTransaction(() =>
        {
            var anchorSort = ScalarLongOrNull("SELECT SortOrder FROM Scripts WHERE Id = $id", ("$id", anchorId));
            if (anchorSort == null)
            {
                // The anchor disappeared (deleted in another window): append.
                script.SortOrder = NextSortOrder();
                InsertScriptCore(script);
                return;
            }

            var position = FindInsertPosition(anchorSort.Value, above);
            if (position == null)
            {
                NormalizeSortOrders();
                var moved = ScalarLong("SELECT SortOrder FROM Scripts WHERE Id = $id", ("$id", anchorId));
                position = FindInsertPosition(moved, above) ?? (above ? moved - 1 : moved + 1);
            }

            script.SortOrder = (int)Math.Clamp(position.Value, int.MinValue, int.MaxValue);
            InsertScriptCore(script);
        });

        return script;
    }

    /// <summary>
    /// Midpoint of the free gap on the requested side of the anchor, or null
    /// when the two positions are already adjacent.
    /// </summary>
    private long? FindInsertPosition(long anchorSort, bool above)
    {
        if (above)
        {
            var previous = ScalarLongOrNull(
                "SELECT MAX(SortOrder) FROM Scripts WHERE SortOrder < $sort", ("$sort", anchorSort));
            var low = previous ?? anchorSort - 2L * SortStep;
            return anchorSort - low > 1 ? low + (anchorSort - low) / 2 : null;
        }

        var next = ScalarLongOrNull(
            "SELECT MIN(SortOrder) FROM Scripts WHERE SortOrder > $sort", ("$sort", anchorSort));
        var high = next ?? anchorSort + 2L * SortStep;
        return high - anchorSort > 1 ? anchorSort + (high - anchorSort) / 2 : null;
    }

    /// <summary>
    /// Moves an existing script between two neighbours without touching any
    /// other row: the new position is the midpoint of the gap on that spot, and
    /// the table is renumbered only when the gap has run out.
    /// </summary>
    /// <param name="beforeId">Row that should end up above it; null for "top".</param>
    /// <param name="afterId">Row that should end up below it; null for "bottom".</param>
    public void MoveScript(long scriptId, long? beforeId, long? afterId)
    {
        RunInTransaction(() =>
        {
            var (before, after) = ResolveNeighbours(beforeId, afterId);
            var position = Midpoint(before, after);

            if (position == null)
            {
                // Both neighbours are adjacent in the numbering; spread the
                // whole list out once and place the row in the new gap.
                NormalizeSortOrders();
                (before, after) = ResolveNeighbours(beforeId, afterId);
                position = Midpoint(before, after);
            }

            if (position == null)
                return;

            using var cmd = CreateCommand();
            cmd.CommandText = "UPDATE Scripts SET SortOrder = $sort WHERE Id = $id";
            cmd.Parameters.AddWithValue("$sort", (int)Math.Clamp(position.Value, int.MinValue, int.MaxValue));
            cmd.Parameters.AddWithValue("$id", scriptId);
            cmd.ExecuteNonQuery();
        });
    }

    private long? ReadSortOrder(long? scriptId)
    {
        return scriptId == null
            ? null
            : ScalarLongOrNull("SELECT SortOrder FROM Scripts WHERE Id = $id", ("$id", scriptId.Value));
    }

    /// <summary>
    /// Positions of the two rows around a drop. A missing side is looked up in
    /// the database, which covers "to the very top", "to the very bottom" and a
    /// list that is only partially loaded.
    /// </summary>
    private (long? Before, long? After) ResolveNeighbours(long? beforeId, long? afterId)
    {
        var before = ReadSortOrder(beforeId);
        var after = ReadSortOrder(afterId);

        if (before == null && after == null)
            return (null, null);

        before ??= ScalarLongOrNull(
            "SELECT MAX(SortOrder) FROM Scripts WHERE SortOrder < $sort", ("$sort", after!.Value));
        after ??= ScalarLongOrNull(
            "SELECT MIN(SortOrder) FROM Scripts WHERE SortOrder > $sort", ("$sort", before!.Value));

        return (before, after);
    }

    private static long? Midpoint(long? before, long? after)
    {
        if (before != null && after != null)
        {
            var gap = after.Value - before.Value;
            return gap > 1 ? before.Value + gap / 2 : null;
        }

        if (before != null)
            return before.Value + SortStep;
        if (after != null)
            return after.Value - SortStep;

        return null;
    }

    // ─── Sharing between accounts ───────────────────────────────────────

    /// <summary>
    /// Counter that changes whenever another connection commits to the same
    /// database. Polling it is how a running window notices that another
    /// Windows account added or edited a script.
    /// </summary>
    public long GetDataVersion()
    {
        using var cmd = CreateCommand();
        cmd.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Writes a consistent copy of the database and its images into an empty
    /// folder. Used when the data location is switched to another scope; the
    /// original files are left untouched.
    /// </summary>
    public void CopyTo(string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        var targetDb = Path.Combine(targetDirectory, DatabaseFileName);
        if (File.Exists(targetDb))
            throw new IOException("目标位置已经有 scripts.db，未覆盖。");

        // Images first, database last: the database file is what marks the
        // target as complete, so a half-finished copy is never used.
        var targetImages = Path.Combine(targetDirectory, "images");
        Directory.CreateDirectory(targetImages);
        foreach (var source in Directory.EnumerateFiles(ImagesDir))
        {
            var destination = Path.Combine(targetImages, Path.GetFileName(source));
            if (!File.Exists(destination))
                File.Copy(source, destination);
        }

        try
        {
            using var cmd = CreateCommand();
            // VACUUM INTO writes a clean, fully check-pointed copy of the live
            // database, which a plain file copy of a WAL database would not be.
            cmd.CommandText = "VACUUM INTO $path";
            cmd.Parameters.AddWithValue("$path", targetDb);
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // A failed VACUUM can leave a partial file behind; delete it so the
            // next attempt starts clean instead of "using" a broken database.
            try
            {
                if (File.Exists(targetDb))
                    File.Delete(targetDb);
            }
            catch (IOException)
            {
            }
            throw;
        }
    }

    // ─── CRUD ───────────────────────────────────────────────────────────

    public List<Script> GetAllScripts()
    {
        using var cmd = CreateCommand();
        cmd.CommandText = $"SELECT Id, Title, Content, Category, ImagePaths, SortOrder, CreatedAt, UpdatedAt, Segments FROM Scripts {OrderBy}";
        return ReadScripts(cmd);
    }

    private static bool IsAllCategory(string category) =>
        string.IsNullOrEmpty(category) || category == "全部";

    public int CountScripts(string category)
    {
        using var cmd = CreateCommand();
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
        using var cmd = CreateCommand();
        if (IsAllCategory(category))
        {
            cmd.CommandText = $"""
                SELECT Id, Title, Content, Category, ImagePaths, SortOrder, CreatedAt, UpdatedAt, Segments FROM Scripts
                {OrderBy}
                LIMIT $limit OFFSET $offset
            """;
        }
        else
        {
            cmd.CommandText = $"""
                SELECT Id, Title, Content, Category, ImagePaths, SortOrder, CreatedAt, UpdatedAt, Segments FROM Scripts
                WHERE Category = $cat
                {OrderBy}
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
        using var cmd = CreateCommand();
        var match = FullTextSearchAvailable ? ScriptSearch.BuildMatchQuery(keyword) : null;
        if (match != null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM ScriptsFts WHERE ScriptsFts MATCH $q";
            cmd.Parameters.AddWithValue("$q", match);
        }
        else
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM Scripts
                WHERE Title LIKE $kw OR Content LIKE $kw OR Category LIKE $kw
            """;
            cmd.Parameters.AddWithValue("$kw", $"%{keyword}%");
        }
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<Script> SearchScriptsPage(string keyword, int offset, int limit)
    {
        using var cmd = CreateCommand();
        var match = FullTextSearchAvailable ? ScriptSearch.BuildMatchQuery(keyword) : null;
        if (match != null)
        {
            // The subquery is answered from the FTS index; only the matching
            // ids reach the Scripts table, so the cost no longer grows with the
            // length of every script in the database.
            cmd.CommandText = $"""
                SELECT Id, Title, Content, Category, ImagePaths, SortOrder, CreatedAt, UpdatedAt, Segments FROM Scripts
                WHERE Id IN (SELECT rowid FROM ScriptsFts WHERE ScriptsFts MATCH $q)
                {OrderBy}
                LIMIT $limit OFFSET $offset
            """;
            cmd.Parameters.AddWithValue("$q", match);
        }
        else
        {
            cmd.CommandText = $"""
                SELECT Id, Title, Content, Category, ImagePaths, SortOrder, CreatedAt, UpdatedAt, Segments FROM Scripts
                WHERE Title LIKE $kw OR Content LIKE $kw OR Category LIKE $kw
                {OrderBy}
                LIMIT $limit OFFSET $offset
            """;
            cmd.Parameters.AddWithValue("$kw", $"%{keyword}%");
        }
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return ReadScripts(cmd);
    }

    public Script? GetScript(long id)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = "SELECT Id, Title, Content, Category, ImagePaths, SortOrder, CreatedAt, UpdatedAt, Segments FROM Scripts WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadScripts(cmd).FirstOrDefault();
    }

    public Script InsertScript(Script script)
    {
        InsertScriptCore(script);
        return script;
    }

    private void InsertScriptCore(Script script)
    {
        using var cmd = CreateCommand();
        var now = DateTime.Now;
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
        cmd.Parameters.AddWithValue("$created", now.ToString("o"));
        cmd.Parameters.AddWithValue("$updated", now.ToString("o"));
        cmd.Parameters.AddWithValue("$segments", script.Segments);
        script.Id = Convert.ToInt64(cmd.ExecuteScalar());
        script.CreatedAt = now;
        script.UpdatedAt = now;

        WriteSearchRow(script);
    }

    public void UpdateScript(Script script)
    {
        using var cmd = CreateCommand();
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

        WriteSearchRow(script);
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

        using (var cmd = CreateCommand())
        {
            cmd.CommandText = "DELETE FROM Scripts WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        RemoveSearchRow(id);
    }

    // ─── Categories ─────────────────────────────────────────────────────

    public List<string> GetAllCategories()
    {
        using var cmd = CreateCommand();
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

        using var cmd = CreateCommand();
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
        using var cmd = CreateCommand();
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
        using var cmd = CreateCommand();
        cmd.CommandText = "DELETE FROM ClipboardHistory";
        cmd.ExecuteNonQuery();
    }

    // ─── Folder categories ──────────────────────────────────────────────

    public List<FolderCategory> GetFolderCategories()
    {
        using var cmd = CreateCommand();
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
        using var cmd = CreateCommand();
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
        using var cmd = CreateCommand();
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
        RunInTransaction(() =>
        {
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
                InsertScriptCore(script);
                count++;
            }
        });
        return count;
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private SqliteCommand CreateCommand()
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = _batch;
        return cmd;
    }

    private void Execute(string sql)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long ScalarLong(string sql, params (string Name, object Value)[] args)
    {
        return Convert.ToInt64(ScalarObject(sql, args));
    }

    private long? ScalarLongOrNull(string sql, params (string Name, object Value)[] args)
    {
        var value = ScalarObject(sql, args);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private object? ScalarObject(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar();
    }

    private string GetMeta(string key)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = "SELECT Value FROM Meta WHERE Key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string ?? "";
    }

    private void SetMeta(string key, string value)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = """
            INSERT INTO Meta (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
        """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs <paramref name="action"/> in one transaction (or joins the batch
    /// that is already open). Used for bulk imports and for anything that has
    /// to keep the search index and the scripts in step.
    /// </summary>
    public void RunInTransaction(Action action)
    {
        if (_batch != null)
        {
            action();
            return;
        }

        using var transaction = _connection.BeginTransaction();
        _batch = transaction;
        try
        {
            action();
            transaction.Commit();
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (SqliteException)
            {
                // SQLite may already have rolled back (I/O error); the original
                // exception is the one worth propagating.
            }
            throw;
        }
        finally
        {
            _batch = null;
        }
    }

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
