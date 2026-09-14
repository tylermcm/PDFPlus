using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PDFPlus.Services;

/// <summary>Swaps the shared brush palette at runtime; all UI uses DynamicResource so it updates live.</summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }
    public static event EventHandler? Changed;

    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("Brush.Window", "#E8E9ED", "#1A1B1F"),
        ("Brush.Toolbar", "#FFFFFF", "#26272C"),
        ("Brush.Sidebar", "#F6F6F8", "#202125"),
        ("Brush.Canvas", "#DCDDE3", "#111214"),
        ("Brush.Border", "#D9DBE1", "#35363C"),
        ("Brush.Text", "#1C1D22", "#ECEDF0"),
        ("Brush.TextDim", "#6B6F7A", "#9A9CA6"),
        ("Brush.Accent", "#E0344B", "#F2566B"),
        ("Brush.AccentText", "#FFFFFF", "#FFFFFF"),
        ("Brush.AccentSoft", "#FCE4E7", "#4A2830"),
        ("Brush.Hover", "#0F000000", "#1AFFFFFF"),
        ("Brush.Pressed", "#1F000000", "#2BFFFFFF"),
        ("Brush.TabActive", "#FFFFFF", "#26272C"),
        ("Brush.TabHover", "#F5F6F8", "#232428"),
        ("Brush.Input", "#FFFFFF", "#1C1D21"),
        ("Brush.Popup", "#FFFFFF", "#2C2D33"),
        ("Brush.Scroll", "#55808590", "#55A0A4AE"),
        ("Brush.ScrollHover", "#99808590", "#99A0A4AE"),
        ("Brush.Danger", "#E81123", "#E81123"),
        ("Brush.Selection", "#3D6FE8", "#5B8CFF"),
    ];

    public static void Apply(string mode)
    {
        Apply(mode switch
        {
            "Dark" => true,
            "Light" => false,
            _ => SystemPrefersDark(),
        });
    }

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var resources = Application.Current.Resources;
        foreach (var (key, light, darkHex) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkHex : light));
            brush.Freeze();
            resources[key] = brush;
        }
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Makes the native title bar (dialogs) match the theme.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => ApplyTitleBar(window);
            return;
        }
        var value = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
    }
}
