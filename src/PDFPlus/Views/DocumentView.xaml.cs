using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PDFPlus.Controls;
using PDFPlus.Core;
using PDFPlus.Services;
using LinkTarget = PDFPlus.Core.LinkTarget;

namespace PDFPlus.Views;

public sealed class ThumbnailItem(DocumentView owner, int index, double width, double height) : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private bool _requested;

    internal int Generation;

    public int Index { get; } = index;
    public string Label => (Index + 1).ToString(CultureInfo.CurrentCulture);
    public double Width { get; } = width;
    public double Height { get; } = height;

    public BitmapSource? Image
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                owner.RequestThumbnail(this);
            }
            return _image;
        }
    }

    internal void SetImage(BitmapSource? image)
    {
        if (image == null) return;
        _image = image;
        OnChanged(nameof(Image));
    }

    internal void Invalidate()
    {
        _requested = false;
        Generation++;
        OnChanged(nameof(Image));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class DocumentView : UserControl, IDisposable
{
    internal const string PdfFilter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*";
    private const string PageDragFormat = "PDFPlus.Pages";
    private const double ThumbWidth = 150;
    private const double ThumbMaxHeight = 200;

    private List<ThumbnailItem> _thumbs = new();
    private int _thumbListVersion;
    private readonly HashSet<int> _dirtyThumbs = new();
    private readonly DispatcherTimer _thumbTimer;
    private bool _syncingThumbs;
    private Point _thumbDragStart;
    private ListBoxItem? _thumbDragItem;
    private bool _thumbDeferredSelect;
    private InsertionAdorner? _insertion;
    private bool _bookmarksLoaded;

    private CancellationTokenSource? _searchCts;
    private string _searchKey = "";
    private List<SearchHit> _hits = new();
    private int _hitIndex = -1;
    private bool _searching;

    private StampKind? _armed;
    private SavedSignature? _armedSignature;
    private double _sidebarWidth = 210;
    private bool _disposed;

    public PdfDocument Document { get; }
    public PdfView Viewer => View;
    public StampKind? ArmedStamp => _armed;
    public bool HasLiveStamp => Stamps.HasLive;
    public bool SidebarVisible => Sidebar.Visibility == Visibility.Visible;

    public event EventHandler? StatusChanged;

    public DocumentView(PdfDocument document)
    {
        InitializeComponent();
        Document = document;
        View.Document = document;
        Stamps.Attach(View, document);
        Annotations.Attach(View, document);
        Annotations.SelectionChanged += (_, _) => RaiseStatus();
        View.PreviewPageClick = (hit, clicks) => Annotations.TrySelectAt(hit, clicks);

        _thumbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _thumbTimer.Tick += (_, _) => FlushThumbnails();
        RebuildThumbnails();
        Thumbnails.ContextMenu = BuildPageMenu();

        document.PagesChanged += OnPagesChanged;
        document.PageContentChanged += OnPageContentChanged;
        document.StateChanged += OnDocumentStateChanged;
        document.NavigateRequested += OnNavigateRequested;
        document.NamedActionRequested += OnNamedAction;

        View.CurrentPageChanged += (_, _) =>
        {
            SyncThumbnailSelection();
            RaiseStatus();
        };
        View.ZoomChanged += (_, _) => RaiseStatus();
        View.LinkActivated += (_, target) => Navigate(target);
        View.PlacementRequested += OnPlacementRequested;
        View.MouseRightButtonUp += OnViewRightClick;
        View.PreviewMouseDown += (_, _) =>
        {
            if (View.PlacementMode) return;
            Stamps.Commit();
            Annotations.ClearSelection();
        };

        PreviewKeyDown += OnPreviewKeyDown;
        SetSidebarVisible(AppSettings.Current.SidebarVisible);
    }

    private Window? OwnerWindow => Window.GetWindow(this);
    private string BaseName => System.IO.Path.GetFileNameWithoutExtension(Document.Title);

    private void RaiseStatus() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private void OnDocumentStateChanged(object? sender, EventArgs e) => RaiseStatus();

    public void FocusViewer() => Dispatcher.BeginInvoke(() => View.Focus(), DispatcherPriority.Input);

    public void CommitStamps() => Stamps.Commit();

    // ---------------------------------------------------------------- sidebar

    public void SetSidebarVisible(bool visible)
    {
        if (!visible && SidebarVisible && SidebarColumn.ActualWidth > 60) _sidebarWidth = SidebarColumn.ActualWidth;
        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = Sidebar.Visibility;
        SidebarColumn.MinWidth = visible ? 140 : 0;
        SidebarColumn.Width = new GridLength(visible ? _sidebarWidth : 0);
        if (visible) SyncThumbnailSelection();
    }

    public void ToggleSidebar()
    {
        SetSidebarVisible(!SidebarVisible);
        AppSettings.Current.SidebarVisible = SidebarVisible;
        AppSettings.Current.Save();
    }

    private void OnPagesTab(object sender, RoutedEventArgs e)
    {
        PagesTab.IsChecked = true;
        BookmarksTab.IsChecked = false;
        Thumbnails.Visibility = Visibility.Visible;
        BookmarksPanel.Visibility = Visibility.Collapsed;
    }

    private void OnBookmarksTab(object sender, RoutedEventArgs e)
    {
        PagesTab.IsChecked = false;
        BookmarksTab.IsChecked = true;
        Thumbnails.Visibility = Visibility.Collapsed;
        BookmarksPanel.Visibility = Visibility.Visible;
        LoadBookmarks();
    }

    private void LoadBookmarks()
    {
        if (_bookmarksLoaded) return;
        _bookmarksLoaded = true;
        var bookmarks = Document.GetBookmarks();
        BookmarkTree.ItemsSource = bookmarks;
        NoBookmarks.Visibility = bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBookmarkClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<ToggleButton>(source) != null) return;
        if (BookmarkTree.SelectedItem is Bookmark { Target: { } target }) Navigate(target);
    }

    private void OnBookmarkKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BookmarkTree.SelectedItem is Bookmark { Target: { } target })
        {
            Navigate(target);
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- thumbnails

    private void RebuildThumbnails()
    {
        _thumbListVersion++;
        var sizes = Document.PageSizes;
        var items = new List<ThumbnailItem>(sizes.Count);
        for (var i = 0; i < sizes.Count; i++)
        {
            var width = ThumbWidth;
            var height = ThumbWidth * sizes[i].Height / sizes[i].Width;
            if (height > ThumbMaxHeight)
            {
                height = ThumbMaxHeight;
                width = ThumbMaxHeight * sizes[i].Width / sizes[i].Height;
            }
            items.Add(new ThumbnailItem(this, i, Math.Round(width), Math.Round(height)));
        }
        _thumbs = items;
        _dirtyThumbs.Clear();
        _syncingThumbs = true;
        Thumbnails.ItemsSource = items;
        _syncingThumbs = false;
    }

    internal void RequestThumbnail(ThumbnailItem item)
    {
        if (_disposed) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var width = Math.Max(1, (int)Math.Round(item.Width * dpi));
        var height = Math.Max(1, (int)Math.Round(item.Height * dpi));
        var generation = item.Generation;
        var listVersion = _thumbListVersion;
        RenderService.Enqueue(new RenderRequest
        {
            Document = Document,
            PageIndex = item.Index,
            PageWidth = width,
            PageHeight = height,
            Clip = new Int32Rect(0, 0, width, height),
            Priority = 2,
            IsStale = () => item.Generation != generation || _thumbListVersion != listVersion || _disposed,
            Completed = bitmap =>
            {
                if (item.Generation == generation) item.SetImage(bitmap);
            },
        });
    }

    private void OnPageContentChanged(object? sender, int index)
    {
        _dirtyThumbs.Add(index);
        _thumbTimer.Stop();
        _thumbTimer.Start();
    }

    private void FlushThumbnails()
    {
        _thumbTimer.Stop();
        foreach (var index in _dirtyThumbs)
            if (index < _thumbs.Count) _thumbs[index].Invalidate();
        _dirtyThumbs.Clear();
    }

    private void OnPagesChanged(object? sender, EventArgs e)
    {
        RebuildThumbnails();
        _bookmarksLoaded = false;
        if (BookmarksPanel.Visibility == Visibility.Visible) LoadBookmarks();
        ResetSearch();
        SyncThumbnailSelection();
        RaiseStatus();
    }

    private void SyncThumbnailSelection()
    {
        if (_thumbs.Count == 0 || Thumbnails.SelectedItems.Count > 1) return;
        var index = View.CurrentPageIndex;
        if (index >= _thumbs.Count || Thumbnails.SelectedIndex == index) return;
        _syncingThumbs = true;
        Thumbnails.SelectedIndex = index;
        if (SidebarVisible && Thumbnails.Visibility == Visibility.Visible) Thumbnails.ScrollIntoView(_thumbs[index]);
        _syncingThumbs = false;
    }

    private void OnThumbnailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingThumbs) return;
        // Keyboard navigation in the list jumps the viewer; mouse clicks are handled on button-up.
        if (Mouse.LeftButton == MouseButtonState.Released && Thumbnails.IsKeyboardFocusWithin &&
            Thumbnails.SelectedItems.Count == 1 && Thumbnails.SelectedItem is ThumbnailItem item)
            View.GoToPage(item.Index);
    }

    private int[] SelectedThumbnailPages() =>
        Thumbnails.SelectedItems.OfType<ThumbnailItem>().Select(t => t.Index).OrderBy(i => i).ToArray();

    /// <summary>Pages selected in the sidebar, or the current page.</summary>
    public int[] SelectedPages()
    {
        if (!SidebarVisible) return [View.CurrentPageIndex];
        var selected = SelectedThumbnailPages();
        return selected.Length > 0 ? selected : [View.CurrentPageIndex];
    }

    private ListBoxItem? ContainerFrom(object? source)
    {
        try
        {
            return source is DependencyObject element ? ItemsControl.ContainerFromElement(Thumbnails, element) as ListBoxItem : null;
        }
        catch
        {
            return null;
        }
    }

    private void OnThumbPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _thumbDragStart = e.GetPosition(Thumbnails);
        _thumbDragItem = ContainerFrom(e.OriginalSource);
        _thumbDeferredSelect = false;
        if (_thumbDragItem is { IsSelected: true } && Thumbnails.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Keep a multi-selection intact so it can be dragged; collapse it on mouse-up instead.
            _thumbDeferredSelect = true;
            e.Handled = true;
        }
    }

    private void OnThumbPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var container = ContainerFrom(e.OriginalSource);
        if (_thumbDeferredSelect && container != null)
        {
            Thumbnails.SelectedItems.Clear();
            container.IsSelected = true;
        }
        _thumbDeferredSelect = false;
        _thumbDragItem = null;
        if (container?.DataContext is ThumbnailItem item && Keyboard.Modifiers == ModifierKeys.None && Thumbnails.SelectedItems.Count <= 1)
            View.GoToPage(item.Index);
    }

    private void OnThumbPreviewRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        var container = ContainerFrom(e.OriginalSource);
        if (container is { IsSelected: false })
        {
            Thumbnails.SelectedItems.Clear();
            container.IsSelected = true;
        }
    }

    private void OnThumbPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _thumbDragItem == null) return;
        var delta = e.GetPosition(Thumbnails) - _thumbDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2)
            return;

        if (!_thumbDragItem.IsSelected)
        {
            Thumbnails.SelectedItems.Clear();
            _thumbDragItem.IsSelected = true;
        }
        _thumbDeferredSelect = false;
        _thumbDragItem = null;
        var pages = SelectedThumbnailPages();
        if (pages.Length == 0) return;

        Stamps.Commit();
        DragDrop.DoDragDrop(Thumbnails, new DataObject(PageDragFormat, pages), DragDropEffects.Move);
        RemoveInsertion();
    }

    private (int Index, double Y) InsertionPoint(Point position)
    {
        var container = ContainerFrom(Thumbnails.InputHitTest(position));
        if (container?.DataContext is not ThumbnailItem item)
        {
            if (_thumbs.Count > 0 && Thumbnails.ItemContainerGenerator.ContainerFromIndex(_thumbs.Count - 1) is ListBoxItem last)
                return (_thumbs.Count, last.TranslatePoint(new Point(0, last.ActualHeight), Thumbnails).Y);
            return (_thumbs.Count, position.Y);
        }
        var top = container.TranslatePoint(new Point(), Thumbnails).Y;
        return position.Y < top + container.ActualHeight / 2
            ? (item.Index, top)
            : (item.Index + 1, top + container.ActualHeight);
    }

    private void OnThumbDragOver(object sender, DragEventArgs e)
    {
        var pages = e.Data.GetDataPresent(PageDragFormat);
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Handled = true;
        if (!pages && !files)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = pages ? DragDropEffects.Move : DragDropEffects.Copy;

        var position = e.GetPosition(Thumbnails);
        if (FindDescendant<ScrollViewer>(Thumbnails) is { } scroller)
        {
            if (position.Y < 40) scroller.LineUp();
            else if (position.Y > Thumbnails.ActualHeight - 40) scroller.LineDown();
        }

        var (_, y) = InsertionPoint(position);
        if (_insertion == null)
        {
            _insertion = new InsertionAdorner(Thumbnails);
            AdornerLayer.GetAdornerLayer(Thumbnails)?.Add(_insertion);
        }
        _insertion.Y = y;
        _insertion.InvalidateVisual();
    }

    private void OnThumbDragLeave(object sender, DragEventArgs e) => RemoveInsertion();

    private void RemoveInsertion()
    {
        if (_insertion == null) return;
        AdornerLayer.GetAdornerLayer(Thumbnails)?.Remove(_insertion);
        _insertion = null;
    }

    private void OnThumbDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var (index, _) = InsertionPoint(e.GetPosition(Thumbnails));
        RemoveInsertion();
        if (e.Data.GetData(PageDragFormat) is int[] pages)
        {
            Stamps.Commit();
            Document.MovePages(pages, index);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            InsertFiles(files.Where(File.Exists), index);
        }
    }

    private void OnThumbKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeletePages();
            e.Handled = true;
        }
    }

    private ContextMenu BuildPageMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemFor("Rotate right", "", () => RotatePages(1)));
        menu.Items.Add(MenuItemFor("Rotate left", "", () => RotatePages(-1)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Insert blank page after", "", InsertBlankPage));
        menu.Items.Add(MenuItemFor("Insert pages from file…", "", InsertPagesFromFile));
        menu.Items.Add(MenuItemFor("Extract pages…", "", ExtractPages));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Delete", "", DeletePages, "Del"));
        return menu;
    }

    internal static MenuItem MenuItemFor(string header, string? icon, Action action, string? gesture = null, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, Icon = icon, InputGestureText = gesture ?? "", IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    // ---------------------------------------------------------------- right-click menu

    private void OnViewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (View.PlacementMode || Annotations.Tool != AnnotationTool.None) return;
        e.Handled = true;
        var menu = BuildViewContextMenu(e.GetPosition(View));
        menu.PlacementTarget = View;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>Builds the page context menu for a point in viewer coordinates, based on what's under it.</summary>
    internal ContextMenu BuildViewContextMenu(Point viewPosition)
    {
        Stamps.Commit();
        var menu = new ContextMenu();

        void Add(string header, string? icon, Action action, string? gesture = null, bool enabled = true)
        {
            var item = MenuItemFor(header, icon, action, gesture);
            item.IsEnabled = enabled;
            menu.Items.Add(item);
        }

        void AddSeparator()
        {
            if (menu.Items.Count > 0 && menu.Items[menu.Items.Count - 1] is not Separator) menu.Items.Add(new Separator());
        }

        if (Document.HasFormFocus)
        {
            var hasFieldSelection = Document.FormSelectedText().Length > 0;
            Add("Cut", "", () => { if (View.CopySelection()) Document.FormReplaceSelection(""); }, "Ctrl+X", hasFieldSelection);
            Add("Copy", "", () => View.CopySelection(), "Ctrl+C", hasFieldSelection);
            Add("Paste", "", PasteIntoFormField, "Ctrl+V", ClipboardHasText());
            AddSeparator();
            Add("Select all", "", () => Document.FormSelectAll(), "Ctrl+A");
            return menu;
        }

        var hit = View.HitTest(viewPosition);
        if (hit is { } target)
        {
            if (Annotations.TrySelectAt(target, 1) && Annotations.Selected is { } annotation)
            {
                if (annotation.IsNote) Add("Edit note", "", Annotations.EditSelectedNote);
                Add("Delete annotation", "", Annotations.DeleteSelected, "Del");
                AddSeparator();
            }
            else if (Document.LinkAt(target.PageIndex, View.DisplayToPage(target.PageIndex, target.Display)) is { } link)
            {
                if (link.Uri is { } uri)
                {
                    Add("Open link", "", () => Navigate(link));
                    Add("Copy link address", "", () => CopyToClipboard(uri));
                }
                else
                {
                    Add($"Go to page {link.PageIndex + 1}", "", () => Navigate(link));
                }
                AddSeparator();
            }
        }

        var hasSelection = View.HasSelection;
        Add("Copy", "", () => View.CopySelection(), "Ctrl+C", hasSelection);
        if (hasSelection)
        {
            Add("Highlight", "", () => MarkupSelection(MarkupKind.Highlight));
            Add("Underline", "", () => MarkupSelection(MarkupKind.Underline));
            Add("Strike out", null, () => MarkupSelection(MarkupKind.Strikeout));
            var text = View.SelectedText().Trim();
            if (text.Length is > 0 and <= 40 && !text.Contains('\n'))
                Add($"Find “{text}”", "", () => { ShowSearch(); FindNext(false); });
        }
        if (hit is { } selectTarget)
            Add("Select all text on page", "", () => View.SelectAllOnPage(selectTarget.PageIndex), "Ctrl+A");

        AddSeparator();
        Add("Zoom in", "", View.ZoomIn, "Ctrl++");
        Add("Zoom out", "", View.ZoomOut, "Ctrl+-");
        Add("Fit width", "", () => { View.FitMode = FitMode.Width; RaiseStatus(); }, "Ctrl+2");
        Add("Fit page", "", () => { View.FitMode = FitMode.Page; RaiseStatus(); }, "Ctrl+0");

        if (hit is { } rotateTarget)
        {
            AddSeparator();
            Add("Rotate page right", "", () => Document.RotatePages([rotateTarget.PageIndex], 1));
            Add("Rotate page left", "", () => Document.RotatePages([rotateTarget.PageIndex], -1));
        }
        return menu;
    }

    private void MarkupSelection(MarkupKind kind)
    {
        Color color;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(AppSettings.Current.AnnotationColor);
        }
        catch
        {
            color = Color.FromRgb(0xFF, 0xD4, 0x00);
        }
        // A black highlight would hide the text, so fall back to yellow.
        if (kind == MarkupKind.Highlight && color.R + color.G + color.B < 150) color = Color.FromRgb(0xFF, 0xD4, 0x00);

        foreach (var (page, rects) in View.SelectionTextRects()) Document.AddTextMarkup(page, kind, rects, color);
        View.ClearSelection();
    }

    private void PasteIntoFormField()
    {
        try
        {
            if (Clipboard.ContainsText()) Document.FormReplaceSelection(Clipboard.GetText());
        }
        catch
        {
            // Clipboard can be locked by another app; ignore.
        }
    }

    private static bool ClipboardHasText()
    {
        try
        {
            return Clipboard.ContainsText();
        }
        catch
        {
            return false;
        }
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard can be locked by another app; ignore.
        }
    }

    // ---------------------------------------------------------------- navigation

    private void Navigate(LinkTarget target)
    {
        if (target.Uri is { } uri) OpenExternal(uri);
        else View.NavigateTo(target);
    }

    private void OnNavigateRequested(object? sender, LinkTarget target) => Navigate(target);

    private void OpenExternal(string uri)
    {
        var owner = OwnerWindow;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "mailto"))
        {
            Dialogs.Error(owner, "Link not opened", $"PDFPlus only opens web and email links.\n\n{uri}");
            return;
        }
        if (Dialogs.Ask(owner, "Open this link?", parsed.AbsoluteUri, "Open link", null) != AskResult.Primary) return;
        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialogs.Error(owner, "Couldn't open link", ex.Message);
        }
    }

    private void OnNamedAction(object? sender, string action)
    {
        switch (action)
        {
            case "NextPage": View.GoToPage(View.CurrentPageIndex + 1); break;
            case "PrevPage": View.GoToPage(View.CurrentPageIndex - 1); break;
            case "FirstPage": View.GoToPage(0); break;
            case "LastPage": View.GoToPage(Document.PageCount - 1); break;
        }
    }

    // ---------------------------------------------------------------- fill & sign

    // ---------------------------------------------------------------- annotations

    public AnnotationTool AnnotationTool => Annotations.Tool;
    public bool HasSelectedAnnotation => Annotations.Selected != null;

    public Color AnnotationColor
    {
        get => Annotations.Color;
        set => Annotations.Color = value;
    }

    public double AnnotationWidth
    {
        get => Annotations.StrokeWidth;
        set => Annotations.StrokeWidth = value;
    }

    public void SetAnnotationTool(AnnotationTool tool)
    {
        Stamps.Commit();
        Disarm();
        View.ClearSelection();
        Annotations.Tool = tool;
        FocusViewer();
        RaiseStatus();
    }

    public void DeleteSelectedAnnotation() => Annotations.DeleteSelected();

    // ---------------------------------------------------------------- security & export

    /// <summary>Raised when an action (like setting a password) should be saved right away.</summary>
    public event EventHandler? SaveRequested;

    public void ProtectWithPassword()
    {
        var protection = ToolsDialogs.Protect(OwnerWindow, Document.Title);
        if (protection == null) return;
        Stamps.Commit();
        Document.SetProtection(protection);
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    public void RemovePassword()
    {
        if (!Document.IsProtected) return;
        if (Dialogs.Ask(OwnerWindow, "Remove the password?",
                "Anyone with the file will be able to open, print and copy it. This takes effect when you save.",
                "Remove password", null) != AskResult.Primary)
            return;
        Stamps.Commit();
        Document.ClearProtection();
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    public async void ExportImages()
    {
        Stamps.Commit();
        var owner = OwnerWindow;
        var options = ToolsDialogs.ExportImages(owner, Document.PageCount, SelectedPages());
        if (options == null) return;
        var folder = new OpenFolderDialog { Title = "Choose where to save the images" };
        if (Document.FilePath != null) folder.InitialDirectory = System.IO.Path.GetDirectoryName(Document.FilePath);
        if (folder.ShowDialog(owner) != true) return;

        var extension = options.Format == ImageExportFormat.Png ? "png" : "jpg";
        var digits = Document.PageCount.ToString().Length;
        var doc = Document;
        var baseName = BaseName;
        try
        {
            ShowToast($"Exporting {options.Pages.Length} page{(options.Pages.Length == 1 ? "" : "s")}…");
            await Task.Run(() =>
            {
                foreach (var page in options.Pages)
                {
                    var name = $"{baseName} - page {(page + 1).ToString().PadLeft(digits, '0')}.{extension}";
                    doc.ExportPageImage(page, System.IO.Path.Combine(folder.FolderName, name), options.Dpi, options.Format);
                }
            });
            ShowToast($"Saved {options.Pages.Length} image{(options.Pages.Length == 1 ? "" : "s")} to {System.IO.Path.GetFileName(folder.FolderName)}");
        }
        catch (Exception ex)
        {
            Dialogs.Error(owner, "Couldn't export images", ex.Message);
        }
    }

    public async void CompressCopy()
    {
        Stamps.Commit();
        var owner = OwnerWindow;
        var preset = ToolsDialogs.Compress(owner);
        if (preset == null) return;
        var dialog = new SaveFileDialog
        {
            Filter = PdfFilter,
            DefaultExt = ".pdf",
            AddExtension = true,
            FileName = $"{BaseName} (compressed).pdf",
        };
        if (Document.FilePath != null) dialog.InitialDirectory = System.IO.Path.GetDirectoryName(Document.FilePath);
        if (dialog.ShowDialog(owner) != true) return;

        var doc = Document;
        var path = dialog.FileName;
        var total = Math.Max(1, doc.PageCount);
        try
        {
            Mouse.OverrideCursor = Cursors.AppStarting;
            ShowToast("Compressing…");
            var result = await Task.Run(() => doc.SaveCompressedCopy(path, preset.Dpi, preset.Quality, CancellationToken.None, page =>
            {
                if (page % 10 == 0) Dispatcher.BeginInvoke(() => ToastText.Text = $"Compressing… {page * 100 / total}%");
            }));
            Mouse.OverrideCursor = null;
            if (result.Written)
            {
                var saved = 100 - result.CompressedBytes * 100 / Math.Max(1, result.OriginalBytes);
                ShowToast($"Saved {System.IO.Path.GetFileName(path)}: {FormatBytes(result.OriginalBytes)} → {FormatBytes(result.CompressedBytes)} ({saved}% smaller)");
            }
            else
            {
                Dialogs.Info(owner, "This PDF is already compact",
                    $"PDFPlus couldn't make it smaller than {FormatBytes(result.OriginalBytes)}, so no copy was written. Files without large images usually can't shrink much.");
            }
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            Dialogs.Error(owner, "Couldn't compress the PDF", ex.Message);
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };

    // ---------------------------------------------------------------- fill & sign

    public void ArmStamp(StampKind kind, SavedSignature? signature = null)
    {
        Stamps.Commit();
        Annotations.Tool = AnnotationTool.None;
        _armed = kind;
        _armedSignature = signature;
        View.PlacementMode = true;
        PlacementHintText.Text = kind switch
        {
            StampKind.Signature => "Click where you want your signature",
            StampKind.Text => "Click anywhere on a page to type",
            StampKind.Date => "Click to add today's date",
            StampKind.Check => "Click to add a checkmark",
            _ => "Click to add a cross",
        } + "     Esc to stop";
        PlacementHint.Visibility = Visibility.Visible;
        FocusViewer();
        RaiseStatus();
    }

    public void Disarm()
    {
        if (_armed == null) return;
        _armed = null;
        _armedSignature = null;
        View.PlacementMode = false;
        PlacementHint.Visibility = Visibility.Collapsed;
        RaiseStatus();
    }

    private void OnPlacementRequested(object? sender, PageHit hit)
    {
        if (_armed is not { } kind) return;
        Stamps.Place(kind, hit, _armedSignature);
        if (kind == StampKind.Signature) Disarm();
        RaiseStatus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox or PasswordBox;
        if (e.Key == Key.Delete && !typing && Annotations.Selected != null)
        {
            Annotations.DeleteSelected();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape) return;
        if (Keyboard.FocusedElement is TextBox box && box != SearchBox) return;
        if (Annotations.Selected != null)
        {
            Annotations.ClearSelection();
            e.Handled = true;
            return;
        }
        if (Annotations.Tool != AnnotationTool.None)
        {
            SetAnnotationTool(AnnotationTool.None);
            e.Handled = true;
            return;
        }
        if (Stamps.HasLive)
        {
            Stamps.Commit();
            e.Handled = true;
        }
        else if (_armed != null)
        {
            Disarm();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- page operations

    public void Undo()
    {
        if (Stamps.HasLive)
        {
            Stamps.Cancel();
            RaiseStatus();
            return;
        }
        if (Document.CanUndo) Document.Undo();
    }

    public void Redo()
    {
        Stamps.Commit();
        if (Document.CanRedo) Document.Redo();
    }

    public void RotatePages(int quarterTurns)
    {
        Stamps.Commit();
        var pages = SelectedPages();
        Document.RotatePages(pages, quarterTurns);
        if (pages.Length > 1)
        {
            foreach (var page in pages)
                if (page < _thumbs.Count) Thumbnails.SelectedItems.Add(_thumbs[page]);
        }
    }

    public void DeletePages()
    {
        Stamps.Commit();
        var pages = SelectedPages();
        if (pages.Length >= Document.PageCount)
        {
            Dialogs.Error(OwnerWindow, "Can't delete every page", "A PDF needs at least one page. Select fewer pages and try again.");
            return;
        }
        Document.DeletePages(pages);
        ShowToast(pages.Length == 1 ? $"Deleted page {pages[0] + 1}   ·   Ctrl+Z to undo" : $"Deleted {pages.Length} pages   ·   Ctrl+Z to undo");
    }

    public void InsertBlankPage()
    {
        Stamps.Commit();
        var after = SelectedPages().Max();
        var size = Document.PageSizes[after];
        Document.InsertBlankPage(after + 1, size.Width, size.Height);
        View.GoToPage(after + 1);
    }

    public void InsertPagesFromFile()
    {
        var dialog = new OpenFileDialog { Filter = PdfFilter, Title = "Insert pages from", Multiselect = true };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        InsertFiles(dialog.FileNames, SelectedPages().Max() + 1);
    }

    private void InsertFiles(IEnumerable<string> files, int index)
    {
        Stamps.Commit();
        var owner = OwnerWindow;
        var first = index;
        var total = 0;
        foreach (var file in files)
        {
            if (!Dialogs.TryResolvePassword(owner, file, out var password)) continue;
            try
            {
                var added = Document.InsertPagesFrom(file, password, index);
                index += added;
                total += added;
            }
            catch (Exception ex)
            {
                Dialogs.Error(owner, $"Couldn't insert {System.IO.Path.GetFileName(file)}", ex.Message);
            }
        }
        if (total == 0) return;
        View.GoToPage(first);
        ShowToast($"Inserted {total} page{(total == 1 ? "" : "s")}");
    }

    public void ExtractPages()
    {
        Stamps.Commit();
        var owner = OwnerWindow;
        var input = Dialogs.Input(owner, "Extract pages",
            $"Which pages should go into the new PDF? Use numbers and ranges like 1-3, 5. This document has {Document.PageCount} pages.",
            PageRanges.Format(SelectedPages()));
        if (input == null) return;
        var pages = PageRanges.Parse(input, Document.PageCount);
        if (pages == null)
        {
            Dialogs.Error(owner, "That page range isn't valid", $"Use page numbers between 1 and {Document.PageCount}, like 1-3, 5, 8-10.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = PdfFilter,
            DefaultExt = ".pdf",
            AddExtension = true,
            FileName = $"{BaseName} (pages {PageRanges.Format(pages).Replace(", ", ",")}).pdf",
        };
        if (Document.FilePath != null) dialog.InitialDirectory = System.IO.Path.GetDirectoryName(Document.FilePath);
        if (dialog.ShowDialog(owner) != true) return;

        try
        {
            Document.ExtractPages(pages, dialog.FileName);
            ShowToast($"Saved {pages.Length} page{(pages.Length == 1 ? "" : "s")} to {System.IO.Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            Dialogs.Error(owner, "Couldn't extract pages", ex.Message);
        }
    }

    public void SplitDocument()
    {
        Stamps.Commit();
        var owner = OwnerWindow;
        var input = Dialogs.Input(owner, "Split document", "Start a new PDF every how many pages?", "1");
        if (input == null) return;
        if (!int.TryParse(input.Trim(), out var chunk) || chunk < 1)
        {
            Dialogs.Error(owner, "That isn't a valid number", "Enter a whole number of pages, like 1 or 5.");
            return;
        }

        var folder = new OpenFolderDialog { Title = "Choose where to save the split files" };
        if (Document.FilePath != null) folder.InitialDirectory = System.IO.Path.GetDirectoryName(Document.FilePath);
        if (folder.ShowDialog(owner) != true) return;

        var parts = new List<(int Start, int End, string Path)>();
        for (var start = 0; start < Document.PageCount; start += chunk)
        {
            var end = Math.Min(Document.PageCount, start + chunk);
            var label = end - start == 1 ? $"page {start + 1}" : $"pages {start + 1}-{end}";
            parts.Add((start, end, System.IO.Path.Combine(folder.FolderName, $"{BaseName} ({label}).pdf")));
        }

        var existing = parts.Count(p => File.Exists(p.Path));
        if (existing > 0 && Dialogs.Ask(owner, "Replace existing files?",
                $"{existing} file{(existing == 1 ? "" : "s")} with the same name already exist in that folder.", "Replace", null) != AskResult.Primary)
            return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            foreach (var part in parts) Document.ExtractPages(Enumerable.Range(part.Start, part.End - part.Start), part.Path);
            ShowToast($"Created {parts.Count} file{(parts.Count == 1 ? "" : "s")} in {System.IO.Path.GetFileName(folder.FolderName)}");
        }
        catch (Exception ex)
        {
            Dialogs.Error(owner, "Couldn't split the document", ex.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ---------------------------------------------------------------- search

    public void ShowSearch()
    {
        SearchBar.Visibility = Visibility.Visible;
        var selected = View.HasSelection ? View.SelectedText() : "";
        if (!string.IsNullOrWhiteSpace(selected) && selected.Length < 80 && !selected.Contains('\n')) SearchBox.Text = selected.Trim();
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    public void HideSearch()
    {
        SearchBar.Visibility = Visibility.Collapsed;
        ResetSearch();
        View.Focus();
    }

    private void ResetSearch()
    {
        _searchCts?.Cancel();
        _searchKey = "";
        _hits = new List<SearchHit>();
        _hitIndex = -1;
        View.SetSearchHits([], -1);
        SearchStatus.Text = "";
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideSearch();
            e.Handled = true;
        }
    }

    private void OnSearchOptionChanged(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Trim().Length > 0) FindNext(false);
    }

    private void OnSearchNext(object sender, RoutedEventArgs e) => FindNext(false);
    private void OnSearchPrevious(object sender, RoutedEventArgs e) => FindNext(true);
    private void OnSearchClose(object sender, RoutedEventArgs e) => HideSearch();

    public async void FindNext(bool backwards)
    {
        if (SearchBar.Visibility != Visibility.Visible)
        {
            ShowSearch();
            if (SearchBox.Text.Trim().Length == 0) return;
        }
        var query = SearchBox.Text.Trim();
        if (query.Length == 0 || _searching) return;

        var key = $"{query}{MatchCase.IsChecked}{WholeWord.IsChecked}";
        if (key != _searchKey)
        {
            await RunSearch(query, key);
            if (_hits.Count == 0) return;
            var current = View.CurrentPageIndex;
            _hitIndex = backwards
                ? Math.Max(0, _hits.FindLastIndex(h => h.PageIndex <= current))
                : Math.Max(0, _hits.FindIndex(h => h.PageIndex >= current));
        }
        else
        {
            if (_hits.Count == 0) return;
            _hitIndex = (_hitIndex + (backwards ? -1 : 1) + _hits.Count) % _hits.Count;
        }

        View.SetSearchHits(_hits, _hitIndex);
        View.ShowHit(_hitIndex);
        SearchStatus.Text = $"{_hitIndex + 1} of {_hits.Count}";
    }

    private async Task RunSearch(string query, string key)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        _searchKey = key;
        _hits = new List<SearchHit>();
        _hitIndex = -1;
        View.SetSearchHits([], -1);
        SearchStatus.Text = "Searching…";

        var matchCase = MatchCase.IsChecked == true;
        var wholeWord = WholeWord.IsChecked == true;
        var doc = Document;
        var total = Math.Max(1, doc.PageCount);
        _searching = true;
        try
        {
            var hits = await Task.Run(() => doc.Search(query, matchCase, wholeWord, cts.Token, page =>
            {
                if (page % 20 == 0)
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!cts.IsCancellationRequested) SearchStatus.Text = $"Searching… {page * 100 / total}%";
                    });
            }), cts.Token);
            if (cts.IsCancellationRequested) return;
            _hits = hits;
            SearchStatus.Text = hits.Count == 0 ? "No results" : "";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _searching = false;
        }
    }

    // ---------------------------------------------------------------- misc

    public void ShowToast(string message)
    {
        ToastText.Text = message;
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2800))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3200))));
        Toast.BeginAnimation(OpacityProperty, animation);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _searchCts?.Cancel();
        _thumbTimer.Stop();
        Stamps.Cancel();
        Document.PagesChanged -= OnPagesChanged;
        Document.PageContentChanged -= OnPageContentChanged;
        Document.StateChanged -= OnDocumentStateChanged;
        Document.NavigateRequested -= OnNavigateRequested;
        Document.NamedActionRequested -= OnNamedAction;
        View.Document = null;
        Document.Dispose();
    }

    private sealed class InsertionAdorner(UIElement element) : Adorner(element)
    {
        public double Y { get; set; }

        protected override void OnRender(DrawingContext dc)
        {
            var brush = Application.Current.TryFindResource("Brush.Accent") as Brush ?? Brushes.OrangeRed;
            var pen = new Pen(brush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(18, Y), new Point(AdornedElement.RenderSize.Width - 18, Y));
        }
    }
}
