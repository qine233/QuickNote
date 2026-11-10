using System.IO;
using System.Text.Json;

namespace QuickNote.Services;

/// <summary>
/// Small settings file kept next to the database. It holds the window geometry
/// so the app reopens where it was left; deliberately a separate JSON file, so
/// replacing scripts.db never resets the UI.
/// </summary>
public class AppSettings
{
    /// <summary>Where the window was last left; null until it has been moved.</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }

    private static string FilePath => Path.Combine(DatabaseService.AppDataDir, "settings.json");

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
            Directory.CreateDirectory(DatabaseService.AppDataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        catch (Exception)
        {
            // Losing the window position is not worth an error dialog.
        }
    }
}
