using System.Text.Json.Serialization;

namespace QuickNote.Models;

/// <summary>
/// One ordered piece of a script: either a text run or an image file name.
/// </summary>
public class ContentSegment
{
    public string Type { get; set; } = "text";
    public string Value { get; set; } = "";

    [JsonIgnore]
    public bool IsImage => string.Equals(Type, "image", StringComparison.OrdinalIgnoreCase);

    public static ContentSegment Text(string value) => new() { Type = "text", Value = value };

    public static ContentSegment Image(string fileName) => new() { Type = "image", Value = fileName };
}
