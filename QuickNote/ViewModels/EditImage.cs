using QuickNote.Services;

namespace QuickNote.ViewModels;

/// <summary>
/// An image shown in the edit dialog: either already stored in the app's images
/// directory (StoredFileName) or a local file picked by the user that is copied
/// into the images directory when the script is saved (SourcePath).
/// </summary>
public class EditImage
{
    public string? StoredFileName { get; init; }
    public string? SourcePath { get; init; }

    public string FullPath =>
        StoredFileName != null ? DatabaseService.GetImagePath(StoredFileName) : SourcePath ?? string.Empty;
}

/// <summary>
/// One item read from the rich editor when saving: a text run or an image.
/// </summary>
public class EditContentItem
{
    public bool IsImage { get; init; }
    public string Text { get; init; } = "";
    public EditImage? Image { get; init; }
}
