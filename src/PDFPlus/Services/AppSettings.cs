using System.IO;
using System.Text.Json;

namespace PDFPlus.Services;

public sealed class SavedSignature
{
    /// <summary>WPF path markup of the signature outline, normalized to start at (0,0).</summary>
    public string Data { get; set; } = "";
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>
/// Settings live in %APPDATA%\PDFPlus by default. For a fully portable setup, put an (even empty)
/// PDFPlus.settings.json next to the exe and settings will be kept there instead.
/// </summary>
public sealed class AppSettings
{
    private const string FileName = "PDFPlus.settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string _path = "";

    public static AppSettings Current { get; private set; } = new();

    public string Theme { get; set; } = "System";
    public bool SidebarVisible { get; set; } = true;
    public List<string> RecentFiles { get; set; } = new();
    public List<SavedSignature> Signatures { get; set; } = new();
    public double TextSize { get; set; } = 11;
    public string InkColor { get; set; } = "#000000";
    public string AnnotationColor { get; set; } = "#FFD400";
    public double AnnotationWidth { get; set; } = 2;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }

    public static void Load()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, FileName);
        _path = File.Exists(portable)
            ? portable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDFPlus", FileName);
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 0)
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    /// <summary>False in test modes so automated runs never overwrite real settings.</summary>
    public static bool Persist { get; set; } = true;

    public void Save()
    {
        if (!Persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Settings are a convenience; never fail the app over them.
        }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 15) RecentFiles.RemoveRange(15, RecentFiles.Count - 15);
        Save();
    }
}
