using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickNote.Models;

/// <summary>
/// One recorded clipboard snapshot (the app keeps the latest 20). Copies that
/// QuickNote made itself also keep their ordered text/image segments, so
/// re-pasting from the history brings the pictures back too.
/// </summary>
public class ClipboardEntry
{
    public long Id { get; init; }
    public string Content { get; init; } = "";
    public DateTime CreatedAt { get; init; }

    /// <summary>JSON array of ordered segments; empty for text copied elsewhere.</summary>
    public string Segments { get; init; } = "";

    /// <summary>
    /// Ordered content of this copy. Text-only rows resolve to a single text
    /// segment, so callers can always replay the list.
    /// </summary>
    [JsonIgnore]
    public List<ContentSegment> SegmentsList
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Segments))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<ContentSegment>>(Segments);
                    if (parsed is { Count: > 0 })
                        return parsed;
                }
                catch (JsonException)
                {
                    // fall through to the text content
                }
            }

            return string.IsNullOrWhiteSpace(Content)
                ? []
                : [ContentSegment.Text(Content)];
        }
    }

    [JsonIgnore]
    public int ImageCount => SegmentsList.Count(s => s.IsImage);

    [JsonIgnore]
    public bool HasImages => ImageCount > 0;

    /// <summary>Row badge text; empty when the row carries no pictures.</summary>
    [JsonIgnore]
    public string ImageBadge => HasImages ? $"{ImageCount} 张图" : "";

    /// <summary>
    /// Trimmed text for the list row; long copies are shortened for display.
    /// </summary>
    [JsonIgnore]
    public string Preview
    {
        get
        {
            var text = Content.Trim();
            if (text.Length == 0)
                return HasImages ? "［图片］" : "";
            return text.Length <= 300 ? text : text[..297] + "...";
        }
    }

    [JsonIgnore]
    public string TimeText => CreatedAt.Date == DateTime.Today
        ? CreatedAt.ToString("HH:mm")
        : CreatedAt.ToString("MM-dd HH:mm");
}
