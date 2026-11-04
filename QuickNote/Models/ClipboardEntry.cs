using System.Text.Json.Serialization;

namespace QuickNote.Models;

/// <summary>
/// One recorded clipboard text snapshot (the app keeps the latest 20).
/// </summary>
public class ClipboardEntry
{
    public long Id { get; init; }
    public string Content { get; init; } = "";
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Trimmed text for the list row; long copies are shortened for display.
    /// </summary>
    [JsonIgnore]
    public string Preview
    {
        get
        {
            var text = Content.Trim();
            return text.Length <= 300 ? text : text[..297] + "...";
        }
    }

    [JsonIgnore]
    public string TimeText => CreatedAt.Date == DateTime.Today
        ? CreatedAt.ToString("HH:mm")
        : CreatedAt.ToString("MM-dd HH:mm");
}
