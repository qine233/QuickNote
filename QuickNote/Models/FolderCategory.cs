namespace QuickNote.Models;

/// <summary>
/// A local folder added to the file tab; independent from script categories.
/// </summary>
public class FolderCategory
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
}
