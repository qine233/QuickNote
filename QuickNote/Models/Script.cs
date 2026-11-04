using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QuickNote.Models;

/// <summary>
/// A script/话术 entry: text content + optional images, organized by category.
/// </summary>
public class Script : INotifyPropertyChanged
{
    private long _id;
    private string _title = string.Empty;
    private string _content = string.Empty;
    private string _category = string.Empty;
    private string _imagePaths = string.Empty;   // JSON array of relative file names stored in images dir
    private string _segments = string.Empty;     // JSON array of ordered text/image segments
    private int _sortOrder;
    private DateTime _createdAt = DateTime.Now;
    private DateTime _updatedAt = DateTime.Now;

    public long Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(Preview)); }
    }

    public string Content
    {
        get => _content;
        set { _content = value; OnPropertyChanged(); OnPropertyChanged(nameof(Preview)); }
    }

    public string Category
    {
        get => _category;
        set { _category = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// JSON array of image file names (stored in the images directory).
    /// </summary>
    public string ImagePaths
    {
        get => _imagePaths;
        set { _imagePaths = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasImages)); OnPropertyChanged(nameof(ImageFileNames)); }
    }

    /// <summary>
    /// JSON array of ordered text/image segments (full content layout).
    /// </summary>
    public string Segments
    {
        get => _segments;
        set { _segments = value ?? ""; OnPropertyChanged(); }
    }

    public int SortOrder
    {
        get => _sortOrder;
        set { _sortOrder = value; OnPropertyChanged(); }
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        set { _createdAt = value; OnPropertyChanged(); }
    }

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set { _updatedAt = value; OnPropertyChanged(); }
    }

    // ─── Derived properties ─────────────────────────────────────────────

    [JsonIgnore]
    public bool HasImages => !string.IsNullOrWhiteSpace(_imagePaths) && _imagePaths != "[]";

    /// <summary>
    /// Parsed list of image file names.
    /// </summary>
    [JsonIgnore]
    public List<string> ImageFileNames
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_imagePaths) || _imagePaths == "[]")
                return [];
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<string>>(_imagePaths) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    /// <summary>
    /// Ordered segments; older records without stored segments fall back to
    /// the plain content followed by all images.
    /// </summary>
    [JsonIgnore]
    public List<ContentSegment> SegmentsList
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_segments))
            {
                try
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<List<ContentSegment>>(_segments);
                    if (parsed is { Count: > 0 })
                        return parsed;
                }
                catch
                {
                    // fall through to content + images
                }
            }

            var fallback = new List<ContentSegment>();
            if (!string.IsNullOrWhiteSpace(_content))
                fallback.Add(ContentSegment.Text(_content.Trim()));
            foreach (var name in ImageFileNames)
                fallback.Add(ContentSegment.Image(name));
            return fallback;
        }
    }

    /// <summary>
    /// First ~120 chars of content for the row preview; empty when there is no text.
    /// </summary>
    [JsonIgnore]
    public string Preview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_content)) return "";
            // Collapse newlines and runs of whitespace so the single-line row never wraps.
            var text = Regex.Replace(_content.Trim(), @"\s+", " ");
            return text.Length <= 120 ? text : text[..117] + "...";
        }
    }

    /// <summary>
    /// Text to copy: content only (the title is not included).
    /// </summary>
    [JsonIgnore]
    public string CopyText => _content;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
