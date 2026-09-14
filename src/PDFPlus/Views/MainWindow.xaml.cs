using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PDFPlus.Controls;
using PDFPlus.Core;
using PDFPlus.Services;
using WpfPath = System.Windows.Shapes.Path;

namespace PDFPlus.Views;

public sealed class DocumentTab : INotifyPropertyChanged
{
    private bool _isActive;

    public DocumentTab(DocumentView view) => View = view;

    public DocumentView View { get; }
    public PdfDocument Document => View.Document;
    public string Title => Document.Title;
    public string? FullPath => Document.FilePath;
    public bool IsDirty => Document.IsDirty;
    public bool IsProtected => Document.IsProtected;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            OnChanged();
        }
    }

    public void Refresh()
    {
        OnChanged(nameof(Title));
        OnChanged(nameof(FullPath));
        OnChanged(nameof(IsDirty));
        OnChanged(nameof(IsProtected));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record RecentItem(string FullPath)
{
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DocumentTab> _tabs = new();
    private DocumentTab? _active;
    private DocumentTab? _tabDragTab;

    public MainWindow()
    {
        InitializeComponent();
        TabStrip.ItemsSource = _tabs;
        LogoImage.Source = AppIcon.Get(32);
        WelcomeLogo.Source = AppIcon.Get(128);
        Icon = AppIcon.Get(256);

        var settings = AppSettings.Current;
        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;

        StateChanged += (_, _) => UpdateWindowStateChrome();
        PreviewKeyDown += OnWindowPreviewKeyDown;
        KeyDown += OnWindowKeyDown;
        DragOver += OnWindowDragOver;
        Drop += OnWindowDrop;
        Closing += OnWindowClosing;

        UpdateWindowStateChrome();
        BuildAnnotationOptions();
        RefreshRecent();
        UpdateChrome();
    }

    private DocumentView? ActiveView => _active?.View;

    // ---------------------------------------------------------------- window chrome

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void UpdateWindowStateChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        var frame = SystemParameters.WindowResizeBorderThickness;
        RootBorder.Margin = maximized ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4) : new Thickness(0);
        MaxButton.Content = maximized ? "" : "";
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseWindowClick(object sender, RoutedEventArgs e) => Close();

    private void UpdateChrome()
    {
        var tab = _active;
        WelcomePanel.Visibility = tab == null ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = tab == null ? Visibility.Collapsed : Visibility.Visible;
        Title = tab == null ? "PDFPlus" : $"{tab.Title} - PDFPlus";
        if (tab == null) return;

        var view = tab.View;
        var viewer = view.Viewer;
        var doc = view.Document;
        if (!PageBox.IsKeyboardFocused) PageBox.Text = doc.PageCount == 0 ? "0" : (viewer.CurrentPageIndex + 1).ToString();
        PageCountText.Text = $"of {doc.PageCount}";
        ZoomButton.Content = $"{Math.Round(viewer.Zoom * 100)}%";
        UndoButton.IsEnabled = doc.CanUndo || view.HasLiveStamp;
        RedoButton.IsEnabled = doc.CanRedo;
        FitWidthButton.IsChecked = viewer.FitMode == FitMode.Width;
        FitPageButton.IsChecked = viewer.FitMode == FitMode.Page;

        var armed = view.ArmedStamp;
        SelectToolButton.IsChecked = armed == null && viewer.Tool == ViewTool.Select;
        HandToolButton.IsChecked = armed == null && viewer.Tool == ViewTool.Hand;
        TextStampButton.IsChecked = armed == StampKind.Text;
        CheckStampButton.IsChecked = armed == StampKind.Check;
        CrossStampButton.IsChecked = armed == StampKind.Cross;
        DateStampButton.IsChecked = armed == StampKind.Date;
        SignButton.IsChecked = armed == StampKind.Signature;

        var annotationTool = view.AnnotationTool;
        if (annotationTool != AnnotationTool.None)
        {
            SelectToolButton.IsChecked = false;
            HandToolButton.IsChecked = false;
        }
        AnnotateModeButton.IsChecked = _toolMode == ToolMode.Annotate;
        FillSignModeButton.IsChecked = _toolMode == ToolMode.FillSign;
        ToolRow.Visibility = _toolMode == ToolMode.None ? Visibility.Collapsed : Visibility.Visible;
        AnnotateRow.Visibility = _toolMode == ToolMode.Annotate ? Visibility.Visible : Visibility.Collapsed;
        FillSignRow.Visibility = _toolMode == ToolMode.FillSign ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in AnnotateRow.Children.OfType<ToggleButton>())
            if (button.Tag is string name && Enum.TryParse<AnnotationTool>(name, out var buttonTool))
                button.IsChecked = buttonTool == annotationTool;
        UpdateAnnotationOptions();
        tab.Refresh();
    }

    // ---------------------------------------------------------------- tabs

    private void AddTab(PdfDocument document)
    {
        var view = new DocumentView(document) { Visibility = Visibility.Collapsed };
        var tab = new DocumentTab(view);
        view.StatusChanged += (_, _) =>
        {
            tab.Refresh();
            if (_active == tab) UpdateChrome();
        };
        view.SaveRequested += (_, _) =>
        {
            if (Save(tab, saveAs: false))
                view.ShowToast(tab.Document.IsProtected ? "Saved with password protection" : "Saved without a password");
        };
        ApplyAnnotationSettings(view);
        DocumentHost.Children.Add(view);
        _tabs.Add(tab);
        SelectTab(tab);
    }

    private void SelectTab(DocumentTab? tab)
    {
        if (_active != tab)
        {
            if (_active != null)
            {
                _active.View.CommitStamps();
                _active.IsActive = false;
                _active.View.Visibility = Visibility.Collapsed;
            }
            _active = tab;
            if (tab != null)
            {
                tab.IsActive = true;
                tab.View.Visibility = Visibility.Visible;
                tab.View.FocusViewer();
                Dispatcher.BeginInvoke(() =>
                {
                    if (TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement container) container.BringIntoView();
                });
            }
        }
        UpdateChrome();
    }

    private void CycleTab(int direction)
    {
        if (_tabs.Count == 0) return;
        var index = _active == null ? 0 : _tabs.IndexOf(_active);
        SelectTab(_tabs[(index + direction + _tabs.Count) % _tabs.Count]);
    }

    private bool ConfirmDiscard(DocumentTab tab)
    {
        tab.View.CommitStamps();
        if (!tab.Document.IsDirty) return true;
        SelectTab(tab);
        var answer = Dialogs.Ask(this, $"Save changes to \"{tab.Title}\"?", "Your changes will be lost if you don't save them.", "Save", "Don't save");
        return answer switch
        {
            AskResult.Primary => Save(tab, saveAs: false),
            AskResult.Secondary => true,
            _ => false,
        };
    }

    private bool CloseTab(DocumentTab tab)
    {
        if (!ConfirmDiscard(tab)) return false;
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        DocumentHost.Children.Remove(tab.View);
        if (_active == tab)
        {
            _active = null;
            SelectTab(_tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        tab.View.Dispose();
        UpdateChrome();
        return true;
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DocumentTab tab) return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            CloseTab(tab);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            SelectTab(tab);
            _tabDragTab = tab;
        }
    }

    private void OnTabMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _tabDragTab = null;
            return;
        }
        if (_tabDragTab == null || (sender as FrameworkElement)?.DataContext is not DocumentTab over || over == _tabDragTab) return;
        _tabs.Move(_tabs.IndexOf(_tabDragTab), _tabs.IndexOf(over));
    }

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DocumentTab tab) CloseTab(tab);
        e.Handled = true;
    }

    private void OnTabScrollerWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- open / save / close

    public async Task OpenFileAsync(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        var existing = _tabs.FirstOrDefault(t => string.Equals(t.Document.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectTab(existing);
            return;
        }

        string? password = null;
        while (true)
        {
            try
            {
                Mouse.OverrideCursor = Cursors.AppStarting;
                var document = await PdfDocument.OpenAsync(fullPath, password);
                Mouse.OverrideCursor = null;
                AddTab(document);
                AppSettings.Current.AddRecent(fullPath);
                RefreshRecent();
                return;
            }
            catch (PdfPasswordRequiredException ex)
            {
                Mouse.OverrideCursor = null;
                password = Dialogs.Password(this, Path.GetFileName(fullPath), ex.WrongPassword);
                if (password == null) return;
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                if (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    AppSettings.Current.RecentFiles.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
                    AppSettings.Current.Save();
                    RefreshRecent();
                }
                Dialogs.Error(this, $"Couldn't open {Path.GetFileName(fullPath)}", ex.Message);
                return;
            }
        }
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e) => await OpenWithDialogAsync();

    private async Task OpenWithDialogAsync()
    {
        var dialog = new OpenFileDialog { Filter = DocumentView.PdfFilter, Multiselect = true, Title = "Open PDF" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) await OpenFileAsync(file);
    }

    private bool Save(DocumentTab tab, bool saveAs)
    {
        tab.View.CommitStamps();
        var document = tab.Document;
        var path = document.FilePath;
        if (saveAs || path == null)
        {
            var dialog = new SaveFileDialog { Filter = DocumentView.PdfFilter, FileName = document.Title, DefaultExt = ".pdf", AddExtension = true };
            if (path != null) dialog.InitialDirectory = Path.GetDirectoryName(path);
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            document.Save(path);
            AppSettings.Current.AddRecent(document.FilePath!);
            RefreshRecent();
            UpdateChrome();
            tab.View.ShowToast("Saved");
            return true;
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            Dialogs.Error(this, "Couldn't save", ex.Message);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        foreach (var tab in _tabs.ToList())
        {
            if (!ConfirmDiscard(tab))
            {
                e.Cancel = true;
                return;
            }
        }

        var settings = AppSettings.Current;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
        }
        settings.Save();
    }

    private void CombineFiles()
    {
        var dialog = new OpenFileDialog { Filter = DocumentView.PdfFilter, Multiselect = true, Title = "Choose PDFs to combine" };
        if (dialog.ShowDialog(this) != true) return;
        var files = dialog.FileNames.OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToList();

        var sources = new List<(string, string?)>();
        foreach (var file in files)
        {
            if (!Dialogs.TryResolvePassword(this, file, out var password)) return;
            sources.Add((file, password));
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var document = PdfDocument.Combine(sources);
            Mouse.OverrideCursor = null;
            AddTab(document);
            ActiveView?.ShowToast($"Combined {files.Count} files in name order   ·   drag thumbnails to reorder");
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            Dialogs.Error(this, "Couldn't combine files", ex.Message);
        }
    }

    private void OnCombineClick(object sender, RoutedEventArgs e) => CombineFiles();

    // ---------------------------------------------------------------- recent files & drag/drop

    private void RefreshRecent()
    {
        var items = AppSettings.Current.RecentFiles.Take(8).Select(p => new RecentItem(p)).ToList();
        RecentList.ItemsSource = items;
        RecentHeader.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _ = OpenFileAsync(path);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        foreach (var file in files.Where(File.Exists)) await OpenFileAsync(file);
    }

    // ---------------------------------------------------------------- keyboard

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        var alt = modifiers.HasFlag(ModifierKeys.Alt);
        var view = ActiveView;
        var handled = true;

        if (ctrl && !alt)
        {
            switch (key)
            {
                case Key.O: _ = OpenWithDialogAsync(); break;
                case Key.S when _active != null: Save(_active, saveAs: shift); break;
                case Key.W or Key.F4 when _active != null: CloseTab(_active); break;
                case Key.Tab when _tabs.Count > 1: CycleTab(shift ? -1 : 1); break;
                case Key.P when view != null: Print(); break;
                case Key.F when view != null: view.ShowSearch(); break;
                case Key.OemPlus or Key.Add when view != null: view.Viewer.ZoomIn(); break;
                case Key.OemMinus or Key.Subtract when view != null: view.Viewer.ZoomOut(); break;
                case Key.D0 or Key.NumPad0 when view != null: view.Viewer.FitMode = FitMode.Page; break;
                case Key.D1 or Key.NumPad1 when view != null: view.Viewer.SetZoom(1); break;
                case Key.D2 or Key.NumPad2 when view != null: view.Viewer.FitMode = FitMode.Width; break;
                default: handled = false; break;
            }
        }
        else if (!alt)
        {
            switch (key)
            {
                case Key.F3 when view != null: view.FindNext(shift); break;
                case Key.F4 when view != null: view.ToggleSidebar(); break;
                default: handled = false; break;
            }
        }
        else
        {
            handled = false;
        }

        if (!handled) return;
        e.Handled = true;
        UpdateChrome();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || ActiveView is not { } view) return;
        if (Keyboard.FocusedElement is TextBox or PasswordBox) return;
        var modifiers = Keyboard.Modifiers;

        if (modifiers == ModifierKeys.Control && e.Key == Key.Z) view.Undo();
        else if ((modifiers == ModifierKeys.Control && e.Key == Key.Y) || (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z)) view.Redo();
        else if (modifiers == ModifierKeys.None && e.Key == Key.V) SetTool(ViewTool.Select);
        else if (modifiers == ModifierKeys.None && e.Key == Key.H) SetTool(ViewTool.Hand);
        else return;

        e.Handled = true;
        UpdateChrome();
    }

    // ---------------------------------------------------------------- toolbar

    private void SetTool(ViewTool tool)
    {
        if (ActiveView is not { } view) return;
        view.CommitStamps();
        view.SetAnnotationTool(AnnotationTool.None);
        view.Viewer.Tool = tool;
        UpdateChrome();
    }

    private void ToggleStamp(StampKind kind)
    {
        if (ActiveView is not { } view) return;
        if (view.ArmedStamp == kind) view.Disarm();
        else view.ArmStamp(kind);
        UpdateChrome();
    }

    private void Print()
    {
        if (ActiveView is not { } view) return;
        view.CommitStamps();
        try
        {
            PrintService.Print(view.Document, view.Viewer.CurrentPageIndex);
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Couldn't print", ex.Message);
        }
    }

    private void OnToggleSidebarClick(object sender, RoutedEventArgs e) => ActiveView?.ToggleSidebar();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_active != null) Save(_active, saveAs: false);
    }

    private void OnPrintClick(object sender, RoutedEventArgs e) => Print();

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        ActiveView?.Undo();
        UpdateChrome();
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        ActiveView?.Redo();
        UpdateChrome();
    }

    private void OnPreviousPageClick(object sender, RoutedEventArgs e) =>
        ActiveView?.Viewer.GoToPage(ActiveView.Viewer.CurrentPageIndex - 1);

    private void OnNextPageClick(object sender, RoutedEventArgs e) =>
        ActiveView?.Viewer.GoToPage(ActiveView.Viewer.CurrentPageIndex + 1);

    private void OnPageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (ActiveView is not { } view) return;
        if (e.Key == Key.Enter)
        {
            if (int.TryParse(PageBox.Text.Trim(), out var page)) view.Viewer.GoToPage(page - 1);
            view.FocusViewer();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            view.FocusViewer();
            e.Handled = true;
        }
    }

    private void OnPageBoxFocus(object sender, KeyboardFocusChangedEventArgs e) => Dispatcher.BeginInvoke(PageBox.SelectAll);

    private void OnPageBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateChrome();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ActiveView?.Viewer.ZoomIn();
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ActiveView?.Viewer.ZoomOut();

    private void OnFitWidthClick(object sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view) view.Viewer.FitMode = FitMode.Width;
        UpdateChrome();
    }

    private void OnFitPageClick(object sender, RoutedEventArgs e)
    {
        if (ActiveView is { } view) view.Viewer.FitMode = FitMode.Page;
        UpdateChrome();
    }

    private void OnZoomMenuClick(object sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view) return;
        var viewer = view.Viewer;
        var menu = new ContextMenu { PlacementTarget = ZoomButton, Placement = PlacementMode.Bottom };
        foreach (var zoom in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 })
            menu.Items.Add(DocumentView.MenuItemFor($"{zoom * 100:0}%", null, () => viewer.SetZoom(zoom), isChecked: Math.Abs(viewer.Zoom - zoom) < 0.005 && viewer.FitMode == FitMode.None));
        menu.Items.Add(new Separator());
        menu.Items.Add(DocumentView.MenuItemFor("Fit width", "", () => viewer.FitMode = FitMode.Width, "Ctrl+2"));
        menu.Items.Add(DocumentView.MenuItemFor("Fit page", "", () => viewer.FitMode = FitMode.Page, "Ctrl+0"));
        menu.Items.Add(DocumentView.MenuItemFor("Actual size", null, () => viewer.SetZoom(1), "Ctrl+1"));
        menu.Closed += (_, _) => UpdateChrome();
        menu.IsOpen = true;
    }

    private void OnRotateLeftClick(object sender, RoutedEventArgs e) => ActiveView?.RotatePages(-1);
    private void OnRotateRightClick(object sender, RoutedEventArgs e) => ActiveView?.RotatePages(1);
    private void OnFindClick(object sender, RoutedEventArgs e) => ActiveView?.ShowSearch();
    private void OnSelectToolClick(object sender, RoutedEventArgs e) => SetTool(ViewTool.Select);
    private void OnHandToolClick(object sender, RoutedEventArgs e) => SetTool(ViewTool.Hand);
    private void OnTextStampClick(object sender, RoutedEventArgs e) => ToggleStamp(StampKind.Text);
    private void OnCheckStampClick(object sender, RoutedEventArgs e) => ToggleStamp(StampKind.Check);
    private void OnCrossStampClick(object sender, RoutedEventArgs e) => ToggleStamp(StampKind.Cross);
    private void OnDateStampClick(object sender, RoutedEventArgs e) => ToggleStamp(StampKind.Date);

    private void OnSignClick(object sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view) return;
        if (view.ArmedStamp == StampKind.Signature)
        {
            view.Disarm();
            UpdateChrome();
            return;
        }

        var saved = AppSettings.Current.Signatures;
        if (saved.Count == 0)
        {
            CreateSignatureAndArm(view);
            UpdateChrome();
            return;
        }

        var menu = new ContextMenu { PlacementTarget = SignButton, Placement = PlacementMode.Bottom };
        foreach (var signature in saved.ToList())
        {
            var preview = new WpfPath
            {
                Data = Geometry.Parse(signature.Data),
                Stretch = Stretch.Uniform,
                Height = 36,
                MaxWidth = 190,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 2),
            };
            preview.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Text");
            var item = new MenuItem { Header = preview };
            item.Click += (_, _) =>
            {
                view.ArmStamp(StampKind.Signature, signature);
                UpdateChrome();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(DocumentView.MenuItemFor("Create new signature…", "", () => CreateSignatureAndArm(view)));
        menu.Items.Add(DocumentView.MenuItemFor("Forget saved signatures", "", () =>
        {
            AppSettings.Current.Signatures.Clear();
            AppSettings.Current.Save();
        }));
        menu.Closed += (_, _) => UpdateChrome();
        menu.IsOpen = true;
    }

    private void CreateSignatureAndArm(DocumentView view)
    {
        var dialog = new SignatureDialog(this);
        if (dialog.ShowDialog() != true || dialog.Result == null) return;
        var list = AppSettings.Current.Signatures;
        list.Insert(0, dialog.Result);
        if (list.Count > 6) list.RemoveRange(6, list.Count - 6);
        AppSettings.Current.Save();
        view.ArmStamp(StampKind.Signature, dialog.Result);
        UpdateChrome();
    }

    // ---------------------------------------------------------------- annotate / fill & sign modes

    internal enum ToolMode { None, Annotate, FillSign }

    private static readonly string[] AnnotationPalette = ["#FFD400", "#3DDC84", "#2F80ED", "#FF4FA3", "#E53935", "#1C1D22"];
    private ToolMode _toolMode;

    private void OnAnnotateModeClick(object sender, RoutedEventArgs e) =>
        SetToolMode(_toolMode == ToolMode.Annotate ? ToolMode.None : ToolMode.Annotate);

    private void OnFillSignModeClick(object sender, RoutedEventArgs e) =>
        SetToolMode(_toolMode == ToolMode.FillSign ? ToolMode.None : ToolMode.FillSign);

    internal void SetToolMode(ToolMode mode)
    {
        _toolMode = mode;
        if (ActiveView is { } view)
        {
            if (mode != ToolMode.Annotate && view.AnnotationTool != AnnotationTool.None) view.SetAnnotationTool(AnnotationTool.None);
            if (mode != ToolMode.FillSign)
            {
                view.CommitStamps();
                view.Disarm();
            }
        }
        UpdateChrome();
    }

    private void OnAnnotationToolClick(object sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view || (sender as FrameworkElement)?.Tag is not string name ||
            !Enum.TryParse<AnnotationTool>(name, out var tool))
            return;
        ApplyAnnotationSettings(view);
        view.SetAnnotationTool(view.AnnotationTool == tool ? AnnotationTool.None : tool);
        UpdateChrome();
    }

    private void BuildAnnotationOptions()
    {
        foreach (var hex in AnnotationPalette)
        {
            var swatch = new System.Windows.Shapes.Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                StrokeThickness = 2,
            };
            var button = new Button { Content = swatch, Tag = hex, ToolTip = "Annotation color", MinWidth = 28, Padding = new Thickness(4, 0, 4, 0) };
            button.Click += (_, _) =>
            {
                AppSettings.Current.AnnotationColor = hex;
                AppSettings.Current.Save();
                if (ActiveView is { } view) ApplyAnnotationSettings(view);
                UpdateAnnotationOptions();
            };
            ColorSwatches.Children.Add(button);
        }

        foreach (var (label, width) in new[] { ("Thin", 1.0), ("Medium", 2.0), ("Thick", 4.0) })
        {
            var line = new System.Windows.Shapes.Rectangle { Width = 18, Height = width + 0.5, RadiusX = 1, RadiusY = 1 };
            line.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Text");
            var button = new ToggleButton { Content = line, Tag = width, ToolTip = $"{label} lines", MinWidth = 30 };
            button.Click += (_, _) =>
            {
                AppSettings.Current.AnnotationWidth = width;
                AppSettings.Current.Save();
                if (ActiveView is { } view) ApplyAnnotationSettings(view);
                UpdateAnnotationOptions();
            };
            WidthChoices.Children.Add(button);
        }
    }

    private void UpdateAnnotationOptions()
    {
        var settings = AppSettings.Current;
        foreach (var button in ColorSwatches.Children.OfType<Button>())
        {
            if (button.Content is not System.Windows.Shapes.Ellipse swatch) continue;
            if (string.Equals(button.Tag as string, settings.AnnotationColor, StringComparison.OrdinalIgnoreCase))
                swatch.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.Text");
            else
                swatch.Stroke = null;
        }
        foreach (var toggle in WidthChoices.Children.OfType<ToggleButton>())
            toggle.IsChecked = toggle.Tag is double width && Math.Abs(width - settings.AnnotationWidth) < 0.01;
    }

    private static void ApplyAnnotationSettings(DocumentView view)
    {
        try
        {
            view.AnnotationColor = (Color)ColorConverter.ConvertFromString(AppSettings.Current.AnnotationColor);
        }
        catch
        {
            view.AnnotationColor = Color.FromRgb(0xFF, 0xD4, 0x00);
        }
        view.AnnotationWidth = Math.Clamp(AppSettings.Current.AnnotationWidth, 0.5, 12);
    }

    private void OnPagesMenuClick(object sender, RoutedEventArgs e)
    {
        if (ActiveView is not { } view) return;
        var menu = new ContextMenu { PlacementTarget = PagesButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(DocumentView.MenuItemFor("Insert blank page", "", view.InsertBlankPage));
        menu.Items.Add(DocumentView.MenuItemFor("Insert pages from file…", "", view.InsertPagesFromFile));
        menu.Items.Add(new Separator());
        menu.Items.Add(DocumentView.MenuItemFor("Extract pages…", "", view.ExtractPages));
        menu.Items.Add(DocumentView.MenuItemFor("Split document…", "", view.SplitDocument));
        menu.Items.Add(DocumentView.MenuItemFor("Combine files…", "", CombineFiles));
        menu.Items.Add(new Separator());
        menu.Items.Add(DocumentView.MenuItemFor("Rotate left", "", () => view.RotatePages(-1)));
        menu.Items.Add(DocumentView.MenuItemFor("Rotate right", "", () => view.RotatePages(1)));
        menu.Items.Add(DocumentView.MenuItemFor("Delete selected pages", "", view.DeletePages, "Del"));
        menu.Closed += (_, _) => UpdateChrome();
        menu.IsOpen = true;
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        var dark = !ThemeManager.IsDark;
        AppSettings.Current.Theme = dark ? "Dark" : "Light";
        AppSettings.Current.Save();
        ThemeManager.Apply(dark);
    }

    private void SetTheme(string mode)
    {
        AppSettings.Current.Theme = mode;
        AppSettings.Current.Save();
        ThemeManager.Apply(mode);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(DocumentView.MenuItemFor("Open…", "", () => _ = OpenWithDialogAsync(), "Ctrl+O"));
        if (_active != null)
        {
            var tab = _active;
            menu.Items.Add(DocumentView.MenuItemFor("Save as…", "", () => Save(tab, saveAs: true), "Ctrl+Shift+S"));
            menu.Items.Add(DocumentView.MenuItemFor("Print…", "", Print, "Ctrl+P"));
            menu.Items.Add(DocumentView.MenuItemFor("Close tab", "", () => CloseTab(tab), "Ctrl+W"));
            menu.Items.Add(new Separator());
            var view = tab.View;
            var isProtected = view.Document.IsProtected;
            menu.Items.Add(DocumentView.MenuItemFor(isProtected ? "Change password…" : "Password protect…", "", view.ProtectWithPassword));
            if (isProtected) menu.Items.Add(DocumentView.MenuItemFor("Remove password…", "", view.RemovePassword));
            menu.Items.Add(DocumentView.MenuItemFor("Export pages as images…", "", view.ExportImages));
            menu.Items.Add(DocumentView.MenuItemFor("Save compressed copy…", "", view.CompressCopy));
        }
        menu.Items.Add(new Separator());

        var theme = new MenuItem { Header = "Theme", Icon = "" };
        var current = AppSettings.Current.Theme;
        theme.Items.Add(DocumentView.MenuItemFor("Match Windows", null, () => SetTheme("System"), isChecked: current == "System"));
        theme.Items.Add(DocumentView.MenuItemFor("Light", null, () => SetTheme("Light"), isChecked: current == "Light"));
        theme.Items.Add(DocumentView.MenuItemFor("Dark", null, () => SetTheme("Dark"), isChecked: current == "Dark"));
        menu.Items.Add(theme);

        menu.Items.Add(DocumentView.MenuItemFor("Keyboard shortcuts", "", ShowShortcuts));
        menu.Items.Add(DocumentView.MenuItemFor("About PDFPlus", "", ShowAbout));
        menu.Closed += (_, _) => UpdateChrome();
        menu.IsOpen = true;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) => ShowAbout();

    private void ShowAbout() => Dialogs.Info(this, "PDFPlus 1.0",
        "A fast, free PDF viewer and editor.\n\n" +
        "No accounts, no subscriptions, no telemetry. Your files never leave this computer.\n\n" +
        "PDF rendering by PDFium, the engine behind Chrome's PDF viewer (BSD-3-Clause license).",
        "About PDFPlus");

    private void ShowShortcuts() => Dialogs.Info(this, "Keyboard shortcuts",
        "Ctrl+O  Open        Ctrl+S  Save        Ctrl+Shift+S  Save as\n" +
        "Ctrl+W  Close tab   Ctrl+Tab  Next tab  Ctrl+P  Print\n" +
        "Ctrl+F  Find        F3 / Shift+F3  Next / previous match\n" +
        "Ctrl+Z  Undo        Ctrl+Y  Redo\n" +
        "Ctrl + / Ctrl -  Zoom      Ctrl+wheel  Zoom at cursor\n" +
        "Ctrl+0  Fit page    Ctrl+1  Actual size    Ctrl+2  Fit width\n" +
        "F4  Sidebar         V  Select tool         H  Hand tool\n" +
        "Home / End  First / last page    Space  Page down\n" +
        "Del (in sidebar)  Delete pages   Drag thumbnails to reorder\n" +
        "Esc  Finish placing text or signature",
        "Keyboard shortcuts");
}
