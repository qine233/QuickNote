using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickNote.Services;

/// <summary>
/// Where the scripts database lives.
/// </summary>
public enum DataScope
{
    /// <summary>Only this Windows account sees it (the default).</summary>
    CurrentUser,

    /// <summary>Every Windows account on this machine shares it.</summary>
    AllUsers,

    /// <summary>A folder the user picked, e.g. a shared or synced drive.</summary>
    Custom,
}

/// <summary>
/// Small settings file kept in the user's own profile. It holds the window
/// geometry and where the data lives; deliberately a separate JSON file, so
/// replacing scripts.db never resets the UI.
///
/// It stays per-user even when the data is shared: two accounts should not
/// fight over one window position.
/// </summary>
public class AppSettings
{
    /// <summary>Where the window was last left; null until it has been moved.</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>
    /// Chosen data scope. Null means "never chosen": an existing machine-wide
    /// database is then picked up automatically, so sharing has to be enabled
    /// only once for every account on the computer.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DataScope? DataScope { get; set; }

    /// <summary>Folder used when <see cref="DataScope"/> is <see cref="DataScope.Custom"/>.</summary>
    public string? CustomDataDirectory { get; set; }

    /// <summary>
    /// Exported scripts waiting to be merged into a newly chosen data folder.
    /// Set when switching to a shared database that already holds data, and
    /// consumed on the next start.
    /// </summary>
    public string? PendingImport { get; set; }

    /// <summary>
    /// Per-user folder holding this file. Never follows the data directory,
    /// which may be shared with other Windows accounts.
    /// </summary>
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickNote");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    private static AppSettings? _current;

    /// <summary>The settings of this process.</summary>
    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A corrupt or unreadable settings file must never block startup;
            // the defaults are always usable.
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        catch (Exception)
        {
            // Losing a setting is not worth an error dialog.
        }
    }
}
