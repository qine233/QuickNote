using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using QuickNote.Models;
using QuickNote.Services;
using Microsoft.Win32;

namespace QuickNote.ViewModels;

/// <summary>
/// Main view model for the script clipboard (话术剪切板) application.
/// </summary>
public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DatabaseService _db;

    // ─── Observable collections ─────────────────────────────────────────

    public ObservableCollection<Script> Scripts { get; } = [];
    public ObservableCollection<string> Categories { get; } = [];

    // Cards are materialized page by page. Pages are read straight from SQLite
    // so a large library never sits fully in memory.
    private const int PageSize = 60;
    private int _totalScriptCount;
    private int _renderedCount;

    // ─── Selected state ─────────────────────────────────────────────────

    private string _selectedCategory = "全部";
    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            // Category chips re-select the same value while the list is being
            // merged; reloading for that would thrash the whole list.
            if (value != null && value == _selectedCategory)
                return;

            _selectedCategory = value ?? "全部";
            OnPropertyChanged();
            LoadScripts();
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (value == _searchText)
                return;

            _searchText = value;
            OnPropertyChanged();
            if (IsFilesTab)
                FilterFiles();
            else
                LoadScripts();
        }
    }

    private Script? _selectedScript;
    public Script? SelectedScript
    {
        get => _selectedScript;
        set
        {
            _selectedScript = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedScript));
        }
    }

    public bool HasSelectedScript => _selectedScript != null;

    // ─── Clipboard history ──────────────────────────────────────────────

    // Filled only by QuickNote's own copies; nothing is read back off the
    // clipboard, so text copied from elsewhere never lands here.
    public ObservableCollection<ClipboardEntry> ClipboardHistory { get; } = [];

    // Copied text can be large; cap what we store so repeated copies of a
    // huge script don't bloat the database.
    private const int MaxClipboardChars = 50000;

    private bool _isClipboardOpen;
    public bool IsClipboardOpen
    {
        get => _isClipboardOpen;
        set
        {
            _isClipboardOpen = value;
            OnPropertyChanged();
            if (value) LoadClipboardHistory();
        }
    }

    // ─── File tab (local folders, independent from scripts) ─────────────

    public ObservableCollection<FolderCategory> FolderCategories { get; } = [];
    public ObservableCollection<IndexedFile> IndexedFiles { get; } = [];

    // Huge folders are capped: rows are virtualized, but holding tens of
    // thousands of file objects would still cost memory for no real benefit.
    private const int MaxIndexedFiles = 2000;
    private List<IndexedFile> _indexedFiles = [];
    private bool _indexTruncated;
    private CancellationTokenSource? _indexCts;

    private FolderCategory? _selectedFolderCategory;
    public FolderCategory? SelectedFolderCategory
    {
        get => _selectedFolderCategory;
        set
        {
            if (ReferenceEquals(_selectedFolderCategory, value)) return;
            _selectedFolderCategory = value;
            OnPropertyChanged();
            LoadFolderIndex();
        }
    }

    private bool _isFilesTab;
    public bool IsFilesTab
    {
        get => _isFilesTab;
        set
        {
            if (_isFilesTab == value) return;
            _isFilesTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsScriptsTab));
            OnPropertyChanged(nameof(SearchHint));
            UpdateFooter();

            if (value)
            {
                if (SelectedFolderCategory == null && FolderCategories.Count > 0)
                    SelectedFolderCategory = FolderCategories[0];
                else
                    LoadFolderIndex();
            }
            else
            {
                LoadScripts();
            }
        }
    }

    public bool IsScriptsTab => !_isFilesTab;

    public string SearchHint => IsFilesTab ? "搜索文件" : "搜索话术";

    public string FooterText => IsFilesTab
        ? $"共 {IndexedFiles.Count} 个文件"
        : $"共 {TotalScriptCount} 条话术";

    public string FooterHint => IsFilesTab
        ? _indexTruncated
            ? $"文件较多，仅显示前 {MaxIndexedFiles} 个"
            : "点击文件即可复制 · 右键文件夹可移除"
        : "点击复制 · 拖动排序 · 右键插入 / 编辑";

    // ─── Edit dialog state ──────────────────────────────────────────────

    private string _editTitle = string.Empty;
    public string EditTitle { get => _editTitle; set { _editTitle = value; OnPropertyChanged(); } }

    private string _editCategory = string.Empty;
    public string EditCategory { get => _editCategory; set { _editCategory = value; OnPropertyChanged(); } }

    private bool _isEditing;
    public bool IsEditing { get => _isEditing; set { _isEditing = value; OnPropertyChanged(); } }

    private string _editHeading = "新建话术";
    /// <summary>Dialog title: new, editing, or inserting between two rows.</summary>
    public string EditHeading { get => _editHeading; private set { _editHeading = value; OnPropertyChanged(); } }

    private string _editInsertHint = "";
    /// <summary>Where a new script will land; empty unless inserting.</summary>
    public string EditInsertHint { get => _editInsertHint; private set { _editInsertHint = value; OnPropertyChanged(); } }

    // Where "insert above/below" puts the next script; 0 means "append".
    private long _insertAnchorId;
    private bool _insertAbove;

    /// <summary>
    /// Ordered content of the script being edited, loaded into the rich editor.
    /// </summary>
    private List<ContentSegment> _editSourceSegments = [];
    public IReadOnlyList<ContentSegment> EditSourceSegments => _editSourceSegments;

    private long _editingScriptId;

    // ─── Stats ──────────────────────────────────────────────────────────

    public int TotalScriptCount => _totalScriptCount;

    private bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    /// Whether rows may be dragged into a new order. Search results are a
    /// filtered subset, so dropping inside them would have no clear meaning.
    /// </summary>
    public bool CanReorder => !IsSearching;

    // ─── Commands ────────────────────────────────────────────────────────

    public ICommand CopyScriptCommand { get; }
    public ICommand AddScriptCommand { get; }
    public ICommand InsertAboveCommand { get; }
    public ICommand InsertBelowCommand { get; }
    public ICommand EditScriptCommand { get; }
    public ICommand DeleteScriptCommand { get; }
    public ICommand CancelEditCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand AddCategoryCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenClipboardCommand { get; }
    public ICommand CloseClipboardCommand { get; }
    public ICommand CopyClipboardItemCommand { get; }
    public ICommand ClearClipboardCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand RefreshFolderCommand { get; }
    public ICommand CopyFileCommand { get; }

    // ─── Constructor ────────────────────────────────────────────────────

    public MainViewModel()
    {
        _db = new DatabaseService();

        CopyScriptCommand = new RelayCommand(obj => CopyScript(obj as Script));
        AddScriptCommand = new RelayCommand(AddScript);
        InsertAboveCommand = new RelayCommand(obj => StartInsert(obj as Script, above: true));
        InsertBelowCommand = new RelayCommand(obj => StartInsert(obj as Script, above: false));
        EditScriptCommand = new RelayCommand(obj => EditScript(obj as Script));
        DeleteScriptCommand = new RelayCommand(obj => DeleteScript(obj as Script));
        CancelEditCommand = new RelayCommand(CancelEdit);
        ExportCommand = new RelayCommand(Export);
        ImportCommand = new RelayCommand(Import);
        AddCategoryCommand = new RelayCommand(AddCategory);
        RefreshCommand = new RelayCommand(RefreshAll);
        OpenClipboardCommand = new RelayCommand(() => IsClipboardOpen = true);
        CloseClipboardCommand = new RelayCommand(() => IsClipboardOpen = false);
        CopyClipboardItemCommand = new RelayCommand(obj => CopyClipboardEntry(obj as ClipboardEntry));
        ClearClipboardCommand = new RelayCommand(ClearClipboardHistory);
        AddFolderCommand = new RelayCommand(AddFolder);
        RemoveFolderCommand = new RelayCommand(obj => RemoveFolder(obj as FolderCategory));
        RefreshFolderCommand = new RelayCommand(LoadFolderIndex);
        CopyFileCommand = new RelayCommand(obj => CopyFile(obj as IndexedFile));

        // Clipboard history loads only when the panel is opened.
        RefreshAll();
        LoadFolderCategories();
    }

    // ─── Data loading ───────────────────────────────────────────────────

    public void RefreshAll()
    {
        LoadCategories();
        LoadScripts();
    }

    /// <summary>
    /// Merges the stored categories into the chip list. Clearing the list would
    /// reset the chip selection (and reload the list) every time a script is
    /// saved, so entries are added, moved and removed in place instead.
    /// </summary>
    private void LoadCategories()
    {
        var names = new List<string> { "全部" };
        names.AddRange(_db.GetAllCategories());

        for (var i = Categories.Count - 1; i >= 0; i--)
        {
            if (!names.Contains(Categories[i]))
                Categories.RemoveAt(i);
        }

        for (var i = 0; i < names.Count; i++)
        {
            if (i >= Categories.Count)
                Categories.Add(names[i]);
            else if (Categories[i] != names[i])
                Categories.Insert(i, names[i]);
        }
    }

    private void LoadScripts()
    {
        _totalScriptCount = IsSearching
            ? _db.CountSearchScripts(SearchText)
            : _db.CountScripts(SelectedCategory);

        _renderedCount = 0;
        Scripts.Clear();
        LoadMoreScripts();
        OnPropertyChanged(nameof(TotalScriptCount));
        OnPropertyChanged(nameof(CanReorder));
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(FooterHint));
    }

    public void LoadMoreScripts()
    {
        if (_renderedCount >= _totalScriptCount)
            return;

        var page = IsSearching
            ? _db.SearchScriptsPage(SearchText, _renderedCount, PageSize)
            : _db.GetScriptsPage(SelectedCategory, _renderedCount, PageSize);

        foreach (var s in page)
            Scripts.Add(s);
        _renderedCount = Scripts.Count;
    }

    /// <summary>
    /// Moves a script to a new index in the visible list and stores that order.
    /// </summary>
    public void ReorderScript(Script script, int newIndex)
    {
        var oldIndex = Scripts.IndexOf(script);
        if (oldIndex < 0 || newIndex < 0 || newIndex >= Scripts.Count || oldIndex == newIndex)
            return;

        // Remove + insert rather than Move: ItemsControl regenerates the rows
        // reliably, and a reorder happens at most once per drag.
        Scripts.RemoveAt(oldIndex);
        Scripts.Insert(newIndex, script);

        var before = newIndex > 0 ? Scripts[newIndex - 1] : null;
        var after = newIndex + 1 < Scripts.Count ? Scripts[newIndex + 1] : null;

        try
        {
            _db.MoveScript(script.Id, before?.Id, after?.Id);
        }
        catch (Exception ex)
        {
            ShowToast($"排序没能保存：{ex.Message}");
            RefreshAll();
        }
    }

    // ─── Anti-flood: throttle clipboard operations ──────────────────────

    private DateTime _lastCopyTime = DateTime.MinValue;
    private const int MinCopyIntervalMs = 800; // minimum 800ms between copies

    // ─── Actions ────────────────────────────────────────────────────────

    private void CopyScript(Script? script)
    {
        if (script == null) return;

        // Rate limit: prevent rapid-fire copies that could trigger behavioral detection
        var now = DateTime.Now;
        if ((now - _lastCopyTime).TotalMilliseconds < MinCopyIntervalMs)
        {
            ShowToast("操作太快啦，请稍等一下");
            return;
        }
        _lastCopyTime = now;

        try
        {
            // Segments carry the authored order, so the paste comes out as the
            // same text/image sequence the script was written in.
            var segments = script.SegmentsList;
            var result = RichClipboardService.Copy(segments);

            // Record it now, with its segments: the text poller would otherwise
            // notice the copy a second later and file it as text only.
            RecordCopy(result.PlainText, result.CopiedSegments.Count > 0 ? result.CopiedSegments : null);

            var what = result switch
            {
                { IsRich: true, HasText: true } => $"文字 + {result.ImageCount} 张图片",
                { IsRich: true } => $"{result.ImageCount} 张图片",
                _ => "文字",
            };
            var name = string.IsNullOrWhiteSpace(script.Title) ? "话术" : script.Title;
            var lost = result.MissingImageCount > 0
                ? $"，{result.MissingImageCount} 张图片文件已丢失"
                : "";
            ShowToast($"已复制「{name}」（{what}{lost}）");
        }
        catch (Exception ex)
        {
            // A rich payload can fail (another process grabs the clipboard at
            // that moment). Never leave the user with nothing.
            if (CopyTextOnly(script.CopyText))
                ShowToast($"图文复制失败，已只复制文字：{ex.Message}");
            else
                ShowToast($"复制失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Puts plain text on the clipboard. Used when a rich payload was refused,
    /// and for history rows that never carried pictures.
    /// </summary>
    private bool CopyTextOnly(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Files a copy QuickNote just made in the clipboard history, together with
    /// its ordered segments so the row can put the pictures back later.
    /// </summary>
    private void RecordCopy(string content, IReadOnlyList<ContentSegment>? segments)
    {
        if (string.IsNullOrWhiteSpace(content) && segments == null)
            return;

        var stored = content.Length > MaxClipboardChars ? content[..MaxClipboardChars] : content;

        try
        {
            _db.AddClipboardEntry(stored, segments);
            LoadClipboardHistory();
        }
        catch
        {
            // A failed history write must never interrupt a copy.
        }
    }

    // ─── Clipboard history actions ──────────────────────────────────────

    public void LoadClipboardHistory()
    {
        ClipboardHistory.Clear();
        foreach (var entry in _db.GetClipboardEntries())
            ClipboardHistory.Add(entry);
    }

    private void CopyClipboardEntry(ClipboardEntry? entry)
    {
        if (entry == null) return;

        try
        {
            // A row that was copied with pictures replays them, in order.
            if (entry.HasImages)
            {
                var segments = entry.SegmentsList;
                var result = RichClipboardService.Copy(segments);
                RecordCopy(entry.Content, segments);
                ShowToast(result.IsRich
                    ? $"已复制（{result.ImageCount} 张图片）"
                    : "已复制文字（原图片文件已丢失）");
                return;
            }

            Clipboard.SetText(entry.Content);
            RecordCopy(entry.Content, null);
            ShowToast("已复制到剪切板");
        }
        catch (Exception ex)
        {
            if (CopyTextOnly(entry.Content))
                ShowToast($"图文复制失败，已只复制文字：{ex.Message}");
            else
                ShowToast($"复制失败：{ex.Message}");
        }
    }

    private void ClearClipboardHistory()
    {
        if (ClipboardHistory.Count == 0) return;

        var result = MessageBox.Show(
            "确定清空剪切板历史吗？",
            "确认清空",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _db.ClearClipboardHistory();
        ClipboardHistory.Clear();
        ShowToast("已清空剪切板历史");
    }

    // ─── File tab actions ───────────────────────────────────────────────

    private void LoadFolderCategories()
    {
        FolderCategories.Clear();
        foreach (var folder in _db.GetFolderCategories())
            FolderCategories.Add(folder);

        if (SelectedFolderCategory == null && FolderCategories.Count > 0)
            SelectedFolderCategory = FolderCategories[0];
    }

    private async void LoadFolderIndex()
    {
        _indexCts?.Cancel();
        var cts = new CancellationTokenSource();
        _indexCts = cts;

        IndexedFiles.Clear();
        _indexedFiles = [];
        _indexTruncated = false;
        UpdateFooter();

        var folder = SelectedFolderCategory;
        if (folder == null) return;

        List<IndexedFile> files;
        try
        {
            var path = folder.Path;
            files = await Task.Run(() => IndexFolder(path, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ShowToast($"文件夹索引失败：{ex.Message}");
            return;
        }

        if (cts.IsCancellationRequested) return;
        _indexedFiles = files;
        _indexTruncated = files.Count >= MaxIndexedFiles;
        FilterFiles();
    }

    /// <summary>
    /// Lists the files directly inside the folder (subfolders are not walked).
    /// </summary>
    private static List<IndexedFile> IndexFolder(string path, CancellationToken token)
    {
        var list = new List<IndexedFile>();
        if (!Directory.Exists(path)) return list;

        var dir = new DirectoryInfo(path);
        foreach (var file in dir.EnumerateFiles())
        {
            token.ThrowIfCancellationRequested();
            if (list.Count >= MaxIndexedFiles)
                break;
            if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                continue;

            list.Add(new IndexedFile
            {
                Name = file.Name,
                FullPath = file.FullName,
                Size = file.Length,
                ModifiedAt = file.LastWriteTime,
            });
        }

        list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return list;
    }

    private void FilterFiles()
    {
        var keyword = SearchText.Trim();
        var filtered = keyword.Length == 0
            ? _indexedFiles
            : _indexedFiles
                .Where(f => f.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();

        IndexedFiles.Clear();
        foreach (var file in filtered)
            IndexedFiles.Add(file);
        UpdateFooter();
    }

    private void AddFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要添加的文件分类文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        var owner = new Win32Window(Application.Current.MainWindow);
        if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;

        var path = dialog.SelectedPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        if (FolderCategories.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("该文件夹已在列表中");
            return;
        }

        var name = Path.GetFileName(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = path;

        var folder = _db.InsertFolderCategory(name, path, FolderCategories.Count);
        FolderCategories.Add(folder);
        SelectedFolderCategory = folder;
        ShowToast($"已添加文件夹：{name}");
    }

    private void RemoveFolder(FolderCategory? folder)
    {
        if (folder == null) return;

        var result = MessageBox.Show(
            $"确定从列表移除文件夹「{folder.Name}」吗？\n不会删除磁盘上的文件。",
            "确认移除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _db.DeleteFolderCategory(folder.Id);
        FolderCategories.Remove(folder);
        if (ReferenceEquals(SelectedFolderCategory, folder))
            SelectedFolderCategory = FolderCategories.FirstOrDefault();
        ShowToast("已移除文件夹");
    }

    private void CopyFile(IndexedFile? file)
    {
        if (file == null) return;

        if (!File.Exists(file.FullPath))
        {
            ShowToast("文件不存在，可能已被移动或删除");
            LoadFolderIndex();
            return;
        }

        var now = DateTime.Now;
        if ((now - _lastCopyTime).TotalMilliseconds < MinCopyIntervalMs)
        {
            ShowToast("操作太快啦，请稍等一下");
            return;
        }
        _lastCopyTime = now;

        try
        {
            // FileDrop lets the user paste the file straight into a chat window.
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { file.FullPath });
            Clipboard.SetDataObject(data, true);
            ShowToast($"已复制文件：{file.Name}");
        }
        catch (Exception ex)
        {
            ShowToast($"复制失败：{ex.Message}");
        }
    }

    private sealed class Win32Window : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; }

        public Win32Window(Window window) =>
            Handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
    }

    private void AddScript()
    {
        EditTitle = string.Empty;
        EditCategory = SelectedCategory == "全部" ? "" : SelectedCategory;
        _editSourceSegments = [];
        _editingScriptId = 0;
        _insertAnchorId = 0;
        EditHeading = "新建话术";
        EditInsertHint = "";
        IsEditing = true;
    }

    private void EditScript(Script? script)
    {
        if (script == null) return;
        EditTitle = script.Title;
        EditCategory = script.Category;
        _editSourceSegments = script.SegmentsList.ToList();
        _editingScriptId = script.Id;
        _insertAnchorId = 0;
        EditHeading = "编辑话术";
        EditInsertHint = "";
        IsEditing = true;
    }

    /// <summary>
    /// Opens an empty editor that will be saved directly above or below
    /// <paramref name="anchor"/> instead of at the end of the list.
    /// </summary>
    private void StartInsert(Script? anchor, bool above)
    {
        if (anchor == null) return;

        EditTitle = string.Empty;
        EditCategory = anchor.Category;
        _editSourceSegments = [];
        _editingScriptId = 0;
        _insertAnchorId = anchor.Id;
        _insertAbove = above;

        var name = !string.IsNullOrWhiteSpace(anchor.Title) ? anchor.Title : anchor.Preview;
        var where = string.IsNullOrWhiteSpace(name) ? "该话术" : $"「{Shorten(name, 14)}」";
        EditHeading = "插入话术";
        EditInsertHint = $"将插入到{where}{(above ? "上方" : "下方")}";
        IsEditing = true;
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// Persists the ordered text/image items produced by the rich editor.
    /// </summary>
    public void SaveEdit(IReadOnlyList<EditContentItem> items)
    {
        var hasContent = items.Any(i => i.IsImage ? i.Image != null : !string.IsNullOrWhiteSpace(i.Text));
        if (string.IsNullOrWhiteSpace(EditTitle) && !hasContent)
        {
            ShowToast("标题和内容不能都为空");
            return;
        }

        var storedNames = new List<string>();
        var newFiles = new List<string>();
        var segments = new List<ContentSegment>();
        var textParts = new List<string>();

        try
        {
            foreach (var item in items)
            {
                if (item.IsImage)
                {
                    var img = item.Image;
                    if (img == null) continue;

                    var name = img.StoredFileName;
                    if (string.IsNullOrEmpty(name))
                    {
                        if (string.IsNullOrEmpty(img.SourcePath) || !File.Exists(img.SourcePath))
                            continue;
                        name = _db.StoreImage(img.SourcePath);
                        newFiles.Add(name);
                    }

                    if (!storedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        storedNames.Add(name);
                    segments.Add(ContentSegment.Image(name));
                }
                else if (!string.IsNullOrWhiteSpace(item.Text))
                {
                    segments.Add(ContentSegment.Text(item.Text));
                    textParts.Add(item.Text);
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var file in newFiles)
                _db.RemoveImage(file);
            ShowToast($"图片保存失败：{ex.Message}");
            return;
        }

        var content = string.Join("\n", textParts).Trim();
        var imageJson = JsonSerializer.Serialize(storedNames);
        var segmentsJson = JsonSerializer.Serialize(segments);

        var isNew = _editingScriptId == 0;
        var anchorId = _insertAnchorId;
        var above = _insertAbove;
        Script? inserted = null;

        try
        {
            if (isNew)
            {
                inserted = new Script
                {
                    Title = EditTitle.Trim(),
                    Content = content,
                    Category = EditCategory.Trim(),
                    ImagePaths = imageJson,
                    Segments = segmentsJson,
                };

                if (anchorId != 0)
                {
                    // Slot it between the rows around the one the user picked.
                    _db.InsertScriptNear(inserted, anchorId, above);
                }
                else
                {
                    inserted.SortOrder = _db.NextSortOrder();
                    _db.InsertScript(inserted);
                }
            }
            else
            {
                var existing = _db.GetScript(_editingScriptId);
                if (existing != null)
                {
                    var obsolete = existing.ImageFileNames
                        .Except(storedNames, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    existing.Title = EditTitle.Trim();
                    existing.Content = content;
                    existing.Category = EditCategory.Trim();
                    existing.ImagePaths = imageJson;
                    existing.Segments = segmentsJson;
                    _db.UpdateScript(existing);
                    foreach (var name in obsolete)
                        _db.RemoveImage(name);
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var file in newFiles)
                _db.RemoveImage(file);
            ShowToast($"保存失败：{ex.Message}");
            return;
        }

        IsEditing = false;
        _insertAnchorId = 0;

        // Keep the chip list current without clearing the selected category.
        LoadCategories();

        if (inserted != null && TryShowInserted(inserted, anchorId, above))
            ShowToast(anchorId != 0 ? "已插入" : "已保存");
        else
        {
            RefreshAll();
            ShowToast(isNew ? "已保存" : "已更新");
        }
    }

    /// <summary>
    /// Puts a saved script straight into the list on screen instead of
    /// reloading from page one. Only safe while the loaded page is contiguous
    /// with the database order, which is why a search or a category mismatch
    /// falls back to a normal reload.
    /// </summary>
    private bool TryShowInserted(Script script, long anchorId, bool above)
    {
        if (IsSearching)
            return false;

        var category = SelectedCategory;
        if (!string.IsNullOrEmpty(category) && category != "全部" &&
            !string.Equals(script.Category, category, StringComparison.Ordinal))
            return false;

        if (anchorId == 0)
        {
            // Appending is only visually correct once every row is loaded.
            if (_renderedCount < _totalScriptCount)
                return false;

            Scripts.Add(script);
        }
        else
        {
            var anchorIndex = -1;
            for (var i = 0; i < Scripts.Count; i++)
            {
                if (Scripts[i].Id == anchorId)
                {
                    anchorIndex = i;
                    break;
                }
            }
            if (anchorIndex < 0)
                return false;

            Scripts.Insert(above ? anchorIndex : anchorIndex + 1, script);
        }

        _totalScriptCount++;
        _renderedCount = Scripts.Count;
        OnPropertyChanged(nameof(TotalScriptCount));
        UpdateFooter();
        return true;
    }

    private void CancelEdit()
    {
        IsEditing = false;
    }

    private void DeleteScript(Script? script)
    {
        if (script == null) return;

        var result = MessageBox.Show(
            $"确定要删除话术「{script.Title}」吗？",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _db.DeleteScript(script.Id);
            if (SelectedScript == script)
                SelectedScript = null;
            RefreshAll();
            ShowToast("已删除");
        }
    }

    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出话术",
            Filter = "JSON 文件|*.json",
            FileName = $"话术备份_{DateTime.Now:yyyyMMdd_HHmmss}.json",
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                _db.ExportToJson(dialog.FileName);
                ShowToast($"已导出到：{dialog.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private bool _isImporting;
    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            _isImporting = value;
            OnPropertyChanged();
        }
    }

    private string _importStatus = string.Empty;
    public string ImportStatus
    {
        get => _importStatus;
        private set
        {
            _importStatus = value;
            OnPropertyChanged();
        }
    }

    private async void Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入话术",
            Filter = "所有支持的文件|*.json;*.db;*.ebk;*.xlsx|JSON 文件|*.json|易歪歪数据|*.ebk;*.db|Excel 文件|*.xlsx|所有文件|*.*",
        };

        if (dialog.ShowDialog() != true) return;

        var path = dialog.FileName;
        var ext = Path.GetExtension(path).ToLowerInvariant();

        ImportStatus = "正在导入话术，请稍候…";
        IsImporting = true;
        try
        {
            // Import runs on a pooled thread with its own connection so thousands
            // of inserts and image copies do not freeze the UI thread.
            var count = await Task.Run(() =>
            {
                using var db = new DatabaseService();
                var yyb = new YybImportService(db);
                var imported = ext switch
                {
                    ".json" => db.ImportFromJson(path),
                    ".ebk" => yyb.ImportFromEbk(path),
                    ".db" or ".sqlite" or ".sqlite3" => yyb.ImportFromDatabase(path),
                    ".xlsx" => yyb.ImportFromExcel(path),
                    ".xls" => throw new NotSupportedException(
                        "暂不支持旧版 .xls，请在 Excel 中另存为 .xlsx 后再导入。"),
                    _ => throw new NotSupportedException("不支持的文件类型。"),
                };

                // Imported rows carry the source file's numbering, which would
                // leave no room to insert between them later.
                db.NormalizeSortOrders();
                return imported;
            });

            RefreshAll();
            var note = ext == ".xlsx" ? "（Excel 不含图片）" : "";
            ShowToast($"已导入 {count} 条话术{note}");
        }
        catch (NotSupportedException ex)
        {
            MessageBox.Show(ex.Message, "无法导入", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsImporting = false;
        }
    }

    private void AddCategory()
    {
        var dialog = new Views.InputDialog("请输入新分类名称：");
        dialog.Owner = Application.Current.MainWindow;
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Result))
        {
            if (!Categories.Contains(dialog.Result))
            {
                Categories.Add(dialog.Result);
                SelectedCategory = dialog.Result;
            }
        }
    }

    // ─── Toast notification ─────────────────────────────────────────────

    private string? _toastMessage;
    public string? ToastMessage
    {
        get => _toastMessage;
        set { _toastMessage = value; OnPropertyChanged(); }
    }

    private bool _showToast;
    public bool ShowToastFlag
    {
        get => _showToast;
        set { _showToast = value; OnPropertyChanged(); }
    }

    private async void ShowToast(string message)
    {
        ToastMessage = message;
        ShowToastFlag = true;

        await Task.Delay(2000);
        ShowToastFlag = false;
    }

    // ─── INotifyPropertyChanged ─────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Dispose()
    {
        _indexCts?.Cancel();
        _db?.Dispose();
    }
}
