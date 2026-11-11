using System.ComponentModel;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickNote.Converters;
using QuickNote.Models;
using QuickNote.Services;
using QuickNote.ViewModels;

namespace QuickNote;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;

    public MainWindow()
    {
        InitializeComponent();

        // Reopen where the window was left before anything is shown.
        _settings = AppSettings.Current;
        RestoreWindowBounds();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.ScriptsReloading += (_, _) => _savedScrollOffset = ScriptListScroll.VerticalOffset;
        _viewModel.ScriptsReloaded += (_, _) => Dispatcher.BeginInvoke(
            new Action(() => ScriptListScroll.ScrollToVerticalOffset(_savedScrollOffset)),
            DispatcherPriority.Loaded);
        DataObject.AddPastingHandler(ContentEditor, OnEditorPaste);
        ContentEditor.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(Editor_PreviewMouseDown), handledEventsToo: true);

        SetupTrayIcon();
        _dockTimer.Tick += DockTimer_Tick;
    }

    // No clipboard monitor: the app never reads what you copied elsewhere. The
    // history panel fills only from copies QuickNote makes itself, so text that
    // happens to pass through the clipboard (customer phone numbers, addresses,
    // order ids) is never written to disk.

    private void ClipboardOverlay_BackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Source == sender)
            _viewModel.CloseClipboardCommand.Execute(null);
    }

    private void ClipboardItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ClipboardEntry entry)
            _viewModel.CopyClipboardItemCommand.Execute(entry);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsEditing) && _viewModel.IsEditing)
            LoadEditor();
    }

    // ─── Title bar ──────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
        {
            if (WindowState == WindowState.Maximized)
            {
                var point = PointToScreen(e.GetPosition(this));
                WindowState = WindowState.Normal;
                Left = point.X - Width / 2;
                Top = point.Y - 20;
            }
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        _viewModel.OpenSettingsCommand.Execute(null);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void HideToTray()
    {
        Hide();

        // The window is hidden, not closed: release idle managed memory so a
        // tray-resident instance keeps a low footprint.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void ScriptList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0)
            return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 300)
            _viewModel.LoadMoreScripts();
    }

    // ─── Script card click → copy ───────────────────────────────────────

    private void ScriptCard_Click(object sender, MouseButtonEventArgs e)
    {
        // A drag ends with a mouse-up as well; that must not copy the row.
        if (_suppressScriptClick)
        {
            _suppressScriptClick = false;
            e.Handled = true;
            return;
        }

        if (sender is FrameworkElement fe && fe.DataContext is Script script)
        {
            _viewModel.CopyScriptCommand.Execute(script);
        }
    }

    // ─── Drag a row to reorder ──────────────────────────────────────────

    private const string ScriptDragFormat = "QuickNote.ScriptId";

    private double _savedScrollOffset;

    private Point _dragStartPoint;
    private Script? _dragCandidate;
    private bool _suppressScriptClick;

    private void ScriptRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _suppressScriptClick = false;
        _dragCandidate = (sender as FrameworkElement)?.DataContext as Script;
        _dragStartPoint = e.GetPosition(ScriptListHost);
    }

    private void ScriptRow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        // Search results are a filtered subset; there is no meaningful drop
        // position inside them.
        if (!_viewModel.CanReorder)
        {
            _dragCandidate = null;
            return;
        }

        var position = e.GetPosition(ScriptListHost);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var script = _dragCandidate;
        _dragCandidate = null;
        if (sender is not FrameworkElement source)
            return;

        try
        {
            DragDrop.DoDragDrop(source, new DataObject(ScriptDragFormat, script.Id), DragDropEffects.Move);
        }
        finally
        {
            HideDropIndicator();
            _suppressScriptClick = true;
        }
    }

    private void ScriptList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ScriptDragFormat) || !_viewModel.CanReorder)
        {
            HideDropIndicator();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        AutoScrollDuringDrag(e.GetPosition(ScriptListScroll));

        var (_, top) = ResolveDropTarget(e.GetPosition(ScriptListHost));
        ShowDropIndicator(top);

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ScriptList_DragLeave(object sender, DragEventArgs e) => HideDropIndicator();

    private void ScriptList_Drop(object sender, DragEventArgs e)
    {
        HideDropIndicator();

        if (!e.Data.GetDataPresent(ScriptDragFormat) ||
            e.Data.GetData(ScriptDragFormat) is not long scriptId)
            return;

        var script = _viewModel.Scripts.FirstOrDefault(candidate => candidate.Id == scriptId);
        if (script == null)
            return;

        var sourceIndex = _viewModel.Scripts.IndexOf(script);
        if (sourceIndex < 0)
            return;

        var (insertBefore, _) = ResolveDropTarget(e.GetPosition(ScriptListHost));

        // The dragged row is still part of the list while dropping, so an
        // insertion point below it shifts up by one.
        var target = insertBefore > sourceIndex ? insertBefore - 1 : insertBefore;
        if (target != sourceIndex)
            _viewModel.ReorderScript(script, target);

        e.Handled = true;
    }

    /// <summary>
    /// Row index the insertion line should sit before, plus the y coordinate
    /// of that line in the list's own coordinates.
    /// </summary>
    private (int Index, double Top) ResolveDropTarget(Point position)
    {
        var count = ScriptList.Items.Count;
        var lastBottom = 0d;

        for (var i = 0; i < count; i++)
        {
            if (ScriptList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            var top = container.TranslatePoint(new Point(0, 0), ScriptListHost).Y;
            var bottom = top + container.ActualHeight;
            lastBottom = bottom;

            if (position.Y < top + (bottom - top) / 2)
                return (i, top);
        }

        return (count, lastBottom);
    }

    private void ShowDropIndicator(double top)
    {
        DropIndicator.Width = Math.Max(0, ScriptListHost.ActualWidth - 24);
        Canvas.SetLeft(DropIndicator, 12);
        Canvas.SetTop(DropIndicator, Math.Max(0, top - 1));
        DropIndicator.Visibility = Visibility.Visible;
    }

    private void HideDropIndicator() => DropIndicator.Visibility = Visibility.Collapsed;

    private void AutoScrollDuringDrag(Point viewportPosition)
    {
        const double edge = 28;
        const double step = 16;

        // DragOver only fires while the pointer moves, which is exactly when a
        // scroll should happen: holding still at the edge does nothing.
        if (viewportPosition.Y < edge)
            ScriptListScroll.ScrollToVerticalOffset(Math.Max(0, ScriptListScroll.VerticalOffset - step));
        else if (viewportPosition.Y > ScriptListScroll.ViewportHeight - edge)
            ScriptListScroll.ScrollToVerticalOffset(ScriptListScroll.VerticalOffset + step);
    }

    // ─── Window geometry ────────────────────────────────────────────────

    private void RestoreWindowBounds()
    {
        var width = _settings.WindowWidth;
        var height = _settings.WindowHeight;
        if (width >= MinWidth && height >= MinHeight)
        {
            Width = Math.Min(width, SystemParameters.VirtualScreenWidth);
            Height = Math.Min(height, SystemParameters.VirtualScreenHeight);
        }

        if (_settings.WindowLeft is not double left || _settings.WindowTop is not double top)
            return;

        var screen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        // The monitor may be gone since the last run; only restore a position
        // that still leaves a usable part of the window on screen.
        var visible = Rect.Intersect(screen, new Rect(left, top, Width, Height));
        if (visible.IsEmpty || visible.Width < 120 || visible.Height < 60)
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;

        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;

        // Docked to the screen edge the window sits in a thin sliver; keep the
        // last real position instead of saving that.
        if (!_dockEnabled || _settings.WindowLeft == null)
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
        }

        _settings.Save();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowBounds();
        base.OnClosing(e);
    }

    // ─── Tabs / file rows ───────────────────────────────────────────────

    private void ScriptsTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
            _viewModel.IsFilesTab = false;
    }

    private void FilesTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
            _viewModel.IsFilesTab = true;
    }

    private void FileRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is IndexedFile file)
            _viewModel.CopyFileCommand.Execute(file);
    }

    // ─── Script context menu button ─────────────────────────────────────

    private void ScriptMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Script script)
        {
            // Walk up to the card border that owns the context menu
            System.Windows.DependencyObject? parent = fe;
            Border? cardBorder = null;
            while (parent != null)
            {
                if (parent is Border b)
                {
                    cardBorder = b;
                    break;
                }
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }

            if (cardBorder?.ContextMenu is { } menu)
            {
                menu.PlacementTarget = cardBorder;
                menu.IsOpen = true;
            }
        }
    }

    // ─── Edit overlay background click → cancel ─────────────────────────

    private void EditOverlay_BackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Source == sender) // only when clicking the dimmed background
            _viewModel.CancelEditCommand.Execute(null);
    }

    // ─── Rich content editor (text + inline images) ─────────────────────

    private readonly Dictionary<Border, BlockUIContainer> _imageContainers = new();
    private Border? _selectedImageBorder;

    private void LoadEditor()
    {
        _imageContainers.Clear();
        _selectedImageBorder = null;
        SetImageActionsEnabled(false);

        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        foreach (var segment in _viewModel.EditSourceSegments)
        {
            if (segment.IsImage)
                doc.Blocks.Add(CreateImageContainer(new EditImage { StoredFileName = segment.Value }));
            else if (!string.IsNullOrWhiteSpace(segment.Value))
                doc.Blocks.Add(CreateParagraph(segment.Value));
        }

        if (doc.Blocks.Count == 0 || doc.Blocks.LastBlock is BlockUIContainer)
            doc.Blocks.Add(new Paragraph());

        ContentEditor.Document = doc;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ContentEditor.Focus();
            ContentEditor.CaretPosition = ContentEditor.Document.ContentStart;
        }), DispatcherPriority.Input);
    }

    private static Paragraph CreateParagraph(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 7) };
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                paragraph.Inlines.Add(new LineBreak());
            if (lines[i].Length > 0)
                paragraph.Inlines.Add(new Run(lines[i]));
        }
        return paragraph;
    }

    private BlockUIContainer CreateImageContainer(EditImage imageInfo)
    {
        var image = new Image
        {
            Source = PathToImageSourceConverter.LoadThumbnail(imageInfo.FullPath, 640),
            Stretch = Stretch.Uniform,
            MaxWidth = 320,
            MaxHeight = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Padding = new Thickness(2),
            Margin = new Thickness(0, 2, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            ToolTip = "点击选中，可前移 / 后移 / 删除",
            Tag = imageInfo,
            Child = image,
        };

        if (image.Source == null)
        {
            border.Child = new TextBlock
            {
                Text = "（图片无法显示）",
                FontSize = 12,
                Foreground = (Brush)FindResource("TextHintBrush"),
                Margin = new Thickness(10, 8, 10, 8),
            };
        }

        border.MouseLeftButtonDown += ImageBlock_Click;

        var container = new BlockUIContainer(border) { Margin = new Thickness(0) };
        _imageContainers[border] = container;
        return container;
    }

    private void Editor_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var border = FindImageBorder(e.OriginalSource as DependencyObject);
        if (border == null)
            return;

        SelectImage(border);
        e.Handled = true;
    }

    private Border? FindImageBorder(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Border border && border.Tag is EditImage && _imageContainers.ContainsKey(border))
                return border;
            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void ImageBlock_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && _imageContainers.ContainsKey(border))
        {
            SelectImage(border);
            e.Handled = true;
        }
    }

    private void SelectImage(Border border)
    {
        if (_selectedImageBorder != null && _selectedImageBorder != border)
            _selectedImageBorder.BorderBrush = Brushes.Transparent;

        _selectedImageBorder = border;
        border.BorderBrush = (Brush)FindResource("PrimaryBrush");
        SetImageActionsEnabled(true);
    }

    private void SetImageActionsEnabled(bool enabled)
    {
        ImageUpButton.IsEnabled = enabled;
        ImageDownButton.IsEnabled = enabled;
        ImageDeleteButton.IsEnabled = enabled;
    }

    private void InsertImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "插入图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        foreach (var path in dialog.FileNames)
            InsertImageAtCaret(new EditImage { SourcePath = path });

        ContentEditor.Focus();
    }

    private void InsertImageAtCaret(EditImage imageInfo)
    {
        var caret = ContentEditor.CaretPosition;
        var paragraph = caret.Paragraph;
        if (paragraph == null || !ContentEditor.Document.Blocks.Contains(paragraph))
        {
            paragraph = new Paragraph();
            ContentEditor.Document.Blocks.Add(paragraph);
            caret = paragraph.ContentEnd;
        }

        // Inserting mid-paragraph: keep the text after the caret below the image.
        var trailing = new TextRange(caret, paragraph.ContentEnd).Text.TrimEnd('\r', '\n');
        if (trailing.Length > 0)
            new TextRange(caret, paragraph.ContentEnd).Text = "";

        var container = CreateImageContainer(imageInfo);
        ContentEditor.Document.Blocks.InsertAfter(paragraph, container);

        var next = CreateParagraph(trailing);
        ContentEditor.Document.Blocks.InsertAfter(container, next);
        ContentEditor.CaretPosition = next.ContentEnd;
    }

    private void MoveImageUp_Click(object sender, RoutedEventArgs e) => MoveSelectedImage(-1);

    private void MoveImageDown_Click(object sender, RoutedEventArgs e) => MoveSelectedImage(1);

    private void MoveSelectedImage(int delta)
    {
        if (_selectedImageBorder == null ||
            !_imageContainers.TryGetValue(_selectedImageBorder, out var container))
            return;

        var blocks = ContentEditor.Document.Blocks.ToList();
        var index = blocks.IndexOf(container);
        var targetIndex = index + delta;
        if (index < 0 || targetIndex < 0 || targetIndex >= blocks.Count)
            return;

        var target = blocks[targetIndex];
        ContentEditor.Document.Blocks.Remove(container);
        if (delta < 0)
            ContentEditor.Document.Blocks.InsertBefore(target, container);
        else
            ContentEditor.Document.Blocks.InsertAfter(target, container);
        ContentEditor.Focus();
    }

    private void DeleteImage_Click(object sender, RoutedEventArgs e) => DeleteSelectedImage();

    private void DeleteSelectedImage()
    {
        if (_selectedImageBorder == null ||
            !_imageContainers.TryGetValue(_selectedImageBorder, out var container))
            return;

        _imageContainers.Remove(_selectedImageBorder);
        ContentEditor.Document.Blocks.Remove(container);
        _selectedImageBorder = null;
        SetImageActionsEnabled(false);

        if (!ContentEditor.Document.Blocks.Any(block => block is Paragraph))
            ContentEditor.Document.Blocks.Add(new Paragraph());
        ContentEditor.Focus();
    }

    private void SaveEditButton_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<EditContentItem>();
        foreach (var block in ContentEditor.Document.Blocks)
        {
            if (block is BlockUIContainer { Child: Border border } && border.Tag is EditImage imageInfo)
            {
                items.Add(new EditContentItem { IsImage = true, Image = imageInfo });
            }
            else if (block is Paragraph paragraph)
            {
                var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text
                    .Replace("\r\n", "\n")
                    .Trim('\r', '\n');
                if (!string.IsNullOrWhiteSpace(text))
                    items.Add(new EditContentItem { Text = text });
            }
        }

        _viewModel.SaveEdit(items);
    }

    private void ContentEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _viewModel.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnEditorPaste(object sender, DataObjectPastingEventArgs e)
    {
        // A screenshot or a picture file becomes a real image block; pasting a
        // script's own rich content still comes in as text only, because the
        // markup carries file references rather than pictures we could own.
        if (TryPasteImages(e))
            return;

        // Anything else is reduced to plain text, which is all the editor can
        // persist alongside images.
        var text = e.SourceDataObject?.GetDataPresent(DataFormats.UnicodeText) == true
            ? e.SourceDataObject.GetData(DataFormats.UnicodeText) as string
            : null;

        if (string.IsNullOrEmpty(text))
        {
            e.CancelCommand();
            return;
        }

        e.DataObject = new DataObject(DataFormats.UnicodeText, text);
    }

    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    /// <summary>
    /// Inserts every picture the paste source carries, at the caret. Returns
    /// false when the source holds no image, leaving the caller to paste text.
    /// </summary>
    private bool TryPasteImages(DataObjectPastingEventArgs e)
    {
        var source = e.SourceDataObject;
        if (source == null)
            return false;

        var paths = new List<string>();

        if (source.GetDataPresent(DataFormats.FileDrop) &&
            source.GetData(DataFormats.FileDrop) is string[] files)
        {
            paths.AddRange(files.Where(IsImageFile));
        }

        // Only a clipboard bitmap when no file came along, so a mixed copy of
        // pictures plus text still keeps the pictures.
        if (paths.Count == 0)
        {
            var temp = SaveClipboardBitmapAsPng(source);
            if (temp != null)
                paths.Add(temp);
        }

        if (paths.Count == 0)
            return false;

        e.CancelCommand();
        foreach (var path in paths)
            InsertImageAtCaret(new EditImage { SourcePath = path });
        ContentEditor.Focus();
        return true;
    }

    private static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Writes the pasted bitmap out as a PNG the app can copy into its images
    /// directory on save: a clipboard picture has no backing file of its own.
    /// Returns null when the source carries no decodable image.
    /// </summary>
    private static string? SaveClipboardBitmapAsPng(IDataObject source)
    {
        object? raw = null;
        if (source.GetDataPresent(DataFormats.Bitmap))
            raw = source.GetData(DataFormats.Bitmap);

        if (raw == null)
        {
            // Last resort: the clipboard itself. Mid-paste it can still be
            // locked by the source process, which is a miss rather than an
            // error. COMException derives from ExternalException, so this
            // catches both.
            try
            {
                if (System.Windows.Forms.Clipboard.ContainsImage())
                    raw = System.Windows.Forms.Clipboard.GetImage();
            }
            catch (ExternalException) { }
        }

        var path = Path.Combine(Path.GetTempPath(), $"QuickNote-paste-{Guid.NewGuid():N}.png");
        try
        {
            switch (raw)
            {
                case System.Drawing.Bitmap drawing:
                    drawing.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    return path;
                case BitmapSource bitmapSource:
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                    using (var stream = File.Create(path))
                        encoder.Save(stream);
                    return path;
                default:
                    return null;
            }
        }
        catch (Exception)
        {
            try { File.Delete(path); } catch (IOException) { }
            return null;
        }
    }

    // ─── Right-edge dock (mouse near edge reveals) ───────────────────────
    // No hooks or injected input: the timer only reads the global cursor
    // position and moves this window.

    private bool _dockEnabled;
    private bool _dockShown;
    private readonly DispatcherTimer _dockTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(120),
    };

    private void SetDockEnabled(bool enabled)
    {
        _dockEnabled = enabled;
        if (enabled)
        {
            Show();
            WindowState = WindowState.Normal;
            var right = GetDockRightDip();
            if (right > 0)
            {
                _dockShown = false;
                Topmost = false;
                var maxTop = Math.Max(0, SystemParameters.WorkArea.Height - Height);
                Top = Math.Min(Math.Max(Top, 0), maxTop);
                Left = right - 6; // leave a small sliver on screen
            }
            _dockTimer.Start();
        }
        else
        {
            _dockTimer.Stop();
            Topmost = false;
            Show();
            WindowState = WindowState.Normal;
            var right = GetDockRightDip();
            if (right > 0 && Left > right - 24)
                Left = right - Width - 24;
            Activate();
        }
    }

    private void DockTimer_Tick(object? sender, EventArgs e)
    {
        if (!_dockEnabled || !IsVisible || WindowState != WindowState.Normal)
            return;

        var right = GetDockRightDip();
        if (right <= 0)
            return;

        var scale = GetDeviceScale();
        var pos = System.Windows.Forms.Cursor.Position;
        var cx = pos.X / scale;
        var cy = pos.Y / scale;

        var winTop = Top;
        var winBottom = Top + Height;
        var winLeft = Left;
        var winRight = Left + Width;

        bool nearEdge = cx >= right - 12 && cy >= winTop && cy <= winBottom;
        bool overWindow = cx >= winLeft && cx <= winRight && cy >= winTop && cy <= winBottom;

        if (!_dockShown && nearEdge)
        {
            _dockShown = true;
            Topmost = true;
            AnimateLeft(right - Width);
            Activate();
        }
        else if (_dockShown && !overWindow && !nearEdge)
        {
            _dockShown = false;
            Topmost = false;
            AnimateLeft(right - 6);
        }
    }

    private void AnimateLeft(double target)
    {
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        anim.Completed += (_, _) => Left = target;
        BeginAnimation(LeftProperty, anim);
    }

    private double GetDeviceScale()
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    private double GetDockRightDip()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return 0;
        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        return screen.WorkingArea.Right / GetDeviceScale();
    }

    // ─── System tray ────────────────────────────────────────────────────

    private System.Windows.Forms.NotifyIcon? _trayIcon;

    private void SetupTrayIcon()
    {
        try
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "QuickNote",
                Visible = true,
            };

            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                var iconHandle = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (iconHandle != null)
                    _trayIcon.Icon = iconHandle;
            }

            _trayIcon.DoubleClick += (_, _) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示窗口", null, (_, _) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            });
            var dockItem = new System.Windows.Forms.ToolStripMenuItem("右侧贴边（鼠标靠近弹出）")
            {
                CheckOnClick = true,
            };
            dockItem.CheckedChanged += (_, _) => SetDockEnabled(dockItem.Checked);
            menu.Items.Add(dockItem);
            menu.Items.Add("数据位置…", null, (_, _) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
                _viewModel.OpenSettingsCommand.Execute(null);
            });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) =>
            {
                _trayIcon?.Dispose();
                Application.Current.Shutdown();
            });
            _trayIcon.ContextMenuStrip = menu;
        }
        catch
        {
            // Tray icon may fail in some environments
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _dockTimer.Stop();
        _trayIcon?.Dispose();
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape to cancel editing or close the clipboard history
        if (e.Key == Key.Escape && _viewModel.IsEditing)
        {
            _viewModel.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _viewModel.IsClipboardOpen)
        {
            _viewModel.CloseClipboardCommand.Execute(null);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
