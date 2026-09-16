using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFPlus.Core;
using PDFPlus.Services;

namespace PDFPlus.Views;

public enum HomeTool { Edit, FillSign, Annotate, Organize, Combine, ImagesToPdf, Compress, Protect }

/// <summary>A tool picked on Home, optionally for a specific recent file.</summary>
public sealed record HomeToolRequest(HomeTool Tool, string? Path);

public sealed class RecentFileItem : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private int _pageCount;
    private bool _isProtected;
    private RecentInfo? _info;

    public RecentFileItem(string path)
    {
        FullPath = path;
        Name = Path.GetFileName(path);
        Folder = Path.GetDirectoryName(path) ?? "";
        RefreshFile();
    }

    public string FullPath { get; }
    public string Name { get; }
    public string Folder { get; }
    public bool Exists { get; private set; }
    public long Size { get; private set; }
    public DateTime Modified { get; private set; }
    internal string? PreviewKey { get; set; }

    public bool IsPinned => _info?.Pinned == true;
    public DateTime? Opened => _info?.Opened is { } t && t > DateTime.MinValue ? t : null;
    public int LastPage => _info?.Page ?? 0;
    public BitmapSource? Thumbnail => _thumbnail;
    public bool HasThumbnail => _thumbnail != null;
    public bool ShowPlaceholder => _thumbnail == null;
    public int PageCount => _pageCount;
    public double Opacity => Exists ? 1 : 0.6;

    public string PlaceholderGlyph => !Exists ? "\uE7BA" : _isProtected ? "\uE72E" : "\uE7C3";
    public string PinGlyph => IsPinned ? "\uE77A" : "\uE718";
    public string PinTip => IsPinned ? "Unpin" : "Pin to the top";
    public string PagesText => _pageCount > 0 ? (_pageCount == 1 ? "1 page" : $"{_pageCount} pages") : _isProtected ? "Password protected" : "";
    public string OpenedText => Opened is { } t ? Relative(t) : "";
    public string SizeText => Exists ? FormatBytes(Size) : "";
    public string FolderOrStatus => Exists ? Folder : "File not found (moved, renamed or on a drive that isn't connected)";
    public string Detail => !Exists ? "File not found" : string.Join("  ·  ", new[] { PagesText, OpenedText }.Where(s => s.Length > 0));

    public string ResumeText => LastPage > 0 && _pageCount > 0 ? $"You were on page {LastPage + 1} of {_pageCount}"
        : LastPage > 0 ? $"You were on page {LastPage + 1}"
        : PagesText;

    public void RefreshFile()
    {
        try
        {
            var info = new FileInfo(FullPath);
            Exists = info.Exists;
            Size = Exists ? info.Length : 0;
            Modified = Exists ? info.LastWriteTimeUtc : default;
        }
        catch
        {
            Exists = false;
        }
        Changed(string.Empty);
    }

    public void Update(RecentInfo? info)
    {
        _info = info;
        Changed(string.Empty);
    }

    internal void SetPreview(PdfPreview preview)
    {
        _thumbnail = preview.Image;
        _pageCount = preview.PageCount;
        _isProtected = preview.IsProtected;
        Changed(string.Empty);
    }

    internal static string Relative(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (time.Date == DateTime.Today) return $"{(int)span.TotalHours} h ago";
        if (time.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        if (span.TotalDays < 7) return time.ToString("dddd");
        return time.ToString(time.Year == DateTime.Now.Year ? "MMM d" : "MMM d, yyyy");
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The start screen: open or drop files, resume the last document, launch a tool, and browse recent files with previews.
/// Shown when no document tab is active (the Home button in the tab strip brings it back).
/// </summary>
public partial class HomeView : UserControl
{
    internal static readonly (HomeTool Tool, string Glyph, string Title, string Description, string Color)[] Tools =
    [
        (HomeTool.Edit, "\uE8AC", "Edit text & images", "Retype text on the page, move or resize pictures", "#E0344B"),
        (HomeTool.FillSign, "\uE70F", "Fill & sign", "Fill in forms, type on flat forms and add your signature", "#7C5CFF"),
        (HomeTool.Annotate, "\uE76D", "Annotate", "Highlight, draw, add shapes and sticky notes", "#E19A00"),
        (HomeTool.Organize, "\uE8A9", "Organize pages", "Reorder, rotate, delete, insert and extract pages", "#2F9E6E"),
        (HomeTool.Combine, "\uE8C8", "Combine files", "Merge several PDFs into one document", "#2F80ED"),
        (HomeTool.ImagesToPdf, "\uEB9F", "Images to PDF", "Turn photos and scans into a single PDF", "#D946A0"),
        (HomeTool.Compress, "\uE73F", "Compress", "Save a smaller copy by shrinking large images", "#0E9F9E"),
        (HomeTool.Protect, "\uE72E", "Protect", "Lock a PDF with a password and choose what's allowed", "#64748B"),
    ];

    private static readonly (string Glyph, string Title, string Text)[] Tips =
    [
        ("\uE8AC", "Fix a typo in any PDF", "Press E, click the line, and retype it. PDFPlus keeps the font, size and color."),
        ("\uEB9F", "Pictures in, PDF out", "Drop photos or scans onto this screen to make them into one PDF, a page per picture."),
        ("\uE8A3", "Zoom where you're looking", "Hold Ctrl and scroll to zoom in exactly where your mouse is."),
        ("\uE8A9", "Rearrange pages by dragging", "Drag thumbnails in the sidebar to reorder pages, or drop other PDFs between them."),
        ("\uE70F", "Sign in one click", "Your signature is saved on this computer, so next time you just click where it goes."),
        ("\uE8B3", "Right-click selected text", "Highlight it, underline it, or search the document for it."),
    ];

    private static readonly Dictionary<string, PdfPreview> Previews = new();

    private readonly Dictionary<string, RecentFileItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private List<RecentFileItem> _all = new();
    private RecentFileItem? _continue;
    private bool _pinnedOnly;
    private int _tip;

    public event EventHandler<string?>? OpenRequested;
    public event EventHandler<HomeToolRequest>? ToolRequested;
    public event EventHandler<IReadOnlyList<string>>? ImagesToPdfRequested;
    public event EventHandler? CombineRequested;
    public event EventHandler? AboutRequested;
    public event EventHandler? ShortcutsRequested;

    public HomeView()
    {
        Services.Timeline.Measure("    home xaml parsed", InitializeComponent);
        Logo.Source = AppIcon.Get(128);
        foreach (var tool in Tools) ToolGrid.Children.Add(MakeToolCard(tool));
        Services.Timeline.Mark("    home tool cards built");
        AllFilter.IsChecked = true;
        ShowTip(0);
        SizeChanged += (_, _) => UpdateForWidth();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Refresh();
        };
    }

    // ---------------------------------------------------------------- content

    public void Refresh()
    {
        var settings = AppSettings.Current;
        Greeting.Text = DateTime.Now.Hour switch
        {
            < 5 => "Working late?",
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening",
        };

        var ordered = new List<RecentFileItem>();
        foreach (var path in settings.RecentFiles)
        {
            if (!_items.TryGetValue(path, out var item)) _items[path] = item = new RecentFileItem(path);
            else item.RefreshFile();
            item.Update(settings.DetailsFor(path));
            ordered.Add(item);
        }
        foreach (var stale in _items.Keys.Where(k => !settings.RecentFiles.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            _items.Remove(stale);
        _all = ordered;

        Subtitle.Text = ordered.Count > 0
            ? "Jump back into a recent file, or pick a tool to start something new."
            : "Open a PDF to read, edit, fill, sign or reorganize it. Everything stays on this computer.";
        var list = settings.HomeLayout == "List";
        GridToggle.IsChecked = !list;
        ListToggle.IsChecked = list;
        ApplyFilter();
        UpdateContinue();
        LoadPreviews(ordered);
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var visible = _all
            .Where(i => !_pinnedOnly || i.IsPinned)
            .Where(i => query.Length == 0 || i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        i.Folder.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(i => i.IsPinned) // stable: keeps most-recent-first within each group
            .ToList();
        RecentGrid.ItemsSource = visible;
        RecentRows.ItemsSource = visible;

        var empty = _all.Count == 0;
        var list = AppSettings.Current.HomeLayout == "List";
        RecentCount.Text = _all.Count.ToString();
        RecentCountBadge.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        RecentTools.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyRecent.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RecentGrid.Visibility = visible.Count > 0 && !list ? Visibility.Visible : Visibility.Collapsed;
        RecentListCard.Visibility = visible.Count > 0 && list ? Visibility.Visible : Visibility.Collapsed;
        NoMatches.Visibility = !empty && visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatches.Text = query.Length > 0 ? $"No recent files match “{query}”." : "No pinned files yet. Pin one from its ⋯ menu to keep it at the top.";
        ClearButton.IsEnabled = _all.Any(i => !i.IsPinned);
    }

    private void UpdateContinue()
    {
        _continue = _all.Where(i => i.Exists).OrderByDescending(i => i.Opened ?? DateTime.MinValue).FirstOrDefault();
        ContinuePanel.Visibility = _continue != null ? Visibility.Visible : Visibility.Collapsed;
        TipPanel.Visibility = _continue == null ? Visibility.Visible : Visibility.Collapsed;
        ContinuePanel.DataContext = _continue;
        if (_continue != null) ContinueButton.Content = _continue.LastPage > 0 ? "Continue reading" : "Open";
    }

    private async void LoadPreviews(List<RecentFileItem> items)
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int width = (int)(150 * scale), height = (int)(200 * scale);
        foreach (var item in items)
        {
            if (!item.Exists) continue;
            var key = $"{item.FullPath}|{item.Modified.Ticks}";
            if (item.PreviewKey == key) continue;
            item.PreviewKey = key;
            if (!Previews.TryGetValue(key, out var preview))
            {
                var path = item.FullPath;
                preview = await Task.Run(() =>
                {
                    try
                    {
                        return PdfThumbnail.Render(path, width, height);
                    }
                    catch
                    {
                        return new PdfPreview(null, 0, false);
                    }
                });
                Previews[key] = preview;
            }
            item.SetPreview(preview);
            if (item == _continue) UpdateContinue();
        }
    }

    private Button MakeToolCard((HomeTool Tool, string Glyph, string Title, string Description, string Color) tool)
    {
        var color = (Color)ColorConverter.ConvertFromString(tool.Color);
        var glyph = new TextBlock
        {
            Text = tool.Glyph,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 19,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var tile = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = glyph,
        };
        var title = new TextBlock { Text = tool.Title, FontSize = 14, FontWeight = FontWeights.SemiBold };
        var description = new TextBlock { Text = tool.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        description.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(title);
        text.Children.Add(description);

        var dock = new DockPanel();
        DockPanel.SetDock(tile, Dock.Left);
        dock.Children.Add(tile);
        dock.Children.Add(text);

        var button = new Button
        {
            Content = dock,
            Style = (Style)FindResource("CardButton"),
            Padding = new Thickness(16, 15, 14, 15),
            Margin = new Thickness(0, 0, 14, 14),
        };
        button.Click += (_, _) => ToolRequested?.Invoke(this, new HomeToolRequest(tool.Tool, null));
        return button;
    }

    private void UpdateForWidth()
    {
        var width = ActualWidth;
        ToolGrid.Columns = width >= 1080 ? 4 : width >= 640 ? 2 : 1;
        var wide = width >= 940;
        ContinueColumn.Width = new GridLength(wide ? 400 : 0);
        ContinueCard.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTip(int index)
    {
        _tip = (index % Tips.Length + Tips.Length) % Tips.Length;
        var (glyph, title, text) = Tips[_tip];
        TipGlyph.Text = glyph;
        TipTitle.Text = title;
        TipText.Text = text;
    }

    public void SetLayout(bool list)
    {
        AppSettings.Current.HomeLayout = list ? "List" : "Grid";
        AppSettings.Current.Save();
        GridToggle.IsChecked = !list;
        ListToggle.IsChecked = list;
        ApplyFilter();
    }

    // ---------------------------------------------------------------- recent file actions

    private static RecentFileItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as RecentFileItem;

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) OpenRequested?.Invoke(this, item.FullPath);
    }

    private void OnRecentMoreClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button button && ItemOf(sender) is { } item) ShowItemMenu(item, button, atMouse: false);
    }

    private void OnRecentRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is UIElement element && ItemOf(sender) is { } item) ShowItemMenu(item, element, atMouse: true);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is { } item) TogglePin(item);
    }

    private void TogglePin(RecentFileItem item)
    {
        AppSettings.Current.SetPinned(item.FullPath, !item.IsPinned);
        Refresh();
    }

    private void ShowItemMenu(RecentFileItem item, UIElement target, bool atMouse)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = atMouse ? PlacementMode.MousePoint : PlacementMode.Bottom };
        if (item.Exists)
        {
            menu.Items.Add(DocumentView.MenuItemFor("Open", "\uE8E5", () => OpenRequested?.Invoke(this, item.FullPath)));
            var openWith = new MenuItem { Header = "Open with", Icon = "\uE8A7" };
            foreach (var tool in Tools.Where(t => t.Tool is not (HomeTool.Combine or HomeTool.ImagesToPdf)))
                openWith.Items.Add(DocumentView.MenuItemFor(tool.Title, tool.Glyph, () => ToolRequested?.Invoke(this, new HomeToolRequest(tool.Tool, item.FullPath))));
            menu.Items.Add(openWith);
            menu.Items.Add(new Separator());
            menu.Items.Add(DocumentView.MenuItemFor(item.IsPinned ? "Unpin" : "Pin to the top", item.IsPinned ? "\uE77A" : "\uE718", () => TogglePin(item)));
            menu.Items.Add(DocumentView.MenuItemFor("Show in folder", "\uE8B7", () => ShowInFolder(item.FullPath)));
            menu.Items.Add(DocumentView.MenuItemFor("Copy file path", "\uE8C8", () => CopyPath(item.FullPath)));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(DocumentView.MenuItemFor("Remove from recent", "\uE711", () =>
        {
            AppSettings.Current.RemoveRecent(item.FullPath);
            Refresh();
        }));
        menu.IsOpen = true;
    }

    private void ShowInFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialogs.Error(Window.GetWindow(this), "Couldn't open the folder", ex.Message);
        }
    }

    private static void CopyPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
        }
        catch
        {
            // Clipboard can be locked by another app; ignore.
        }
    }

    // ---------------------------------------------------------------- toolbar & buttons

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        _pinnedOnly = sender == PinnedFilter;
        AllFilter.IsChecked = !_pinnedOnly;
        PinnedFilter.IsChecked = _pinnedOnly;
        ApplyFilter();
    }

    private void OnLayoutClick(object sender, RoutedEventArgs e) => SetLayout(sender == ListToggle);

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (Dialogs.Ask(Window.GetWindow(this), "Clear recent files?",
                "Pinned files stay on the list. Nothing is deleted from your computer.", "Clear", null) != AskResult.Primary)
            return;
        AppSettings.Current.ClearRecent();
        Refresh();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, null);
    private void OnCombineClick(object sender, RoutedEventArgs e) => CombineRequested?.Invoke(this, EventArgs.Empty);
    private void OnImagesClick(object sender, RoutedEventArgs e) => ImagesToPdfRequested?.Invoke(this, []);
    private void OnNextTipClick(object sender, RoutedEventArgs e) => ShowTip(_tip + 1);
    private void OnAboutClick(object sender, RoutedEventArgs e) => AboutRequested?.Invoke(this, EventArgs.Empty);
    private void OnShortcutsClick(object sender, RoutedEventArgs e) => ShortcutsRequested?.Invoke(this, EventArgs.Empty);

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        if (_continue != null) OpenRequested?.Invoke(this, _continue.FullPath);
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        var dark = !ThemeManager.IsDark;
        AppSettings.Current.Theme = dark ? "Dark" : "Light";
        AppSettings.Current.Save();
        ThemeManager.Apply(dark);
    }

    // ---------------------------------------------------------------- drag & drop

    private static bool IsImage(string path) => DocumentView.ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private void SetDropHighlight(bool on)
    {
        DropOutline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, on ? "Brush.Accent" : "Brush.Border");
        DropOutline.StrokeThickness = on ? 2.5 : 1.5;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        SetDropHighlight(true);
        if (!files.Any(IsImage)) return; // PDFs are opened by the window
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    private void OnDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var images = files.Where(f => IsImage(f) && File.Exists(f)).ToList();
        if (images.Count == 0) return;
        e.Handled = true;
        ImagesToPdfRequested?.Invoke(this, images);
        foreach (var pdf in files.Where(f => string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(f)))
            OpenRequested?.Invoke(this, pdf);
    }
}
