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

/// <summary>What the Home screen remembers about a recent file.</summary>
public sealed class RecentInfo
{
    public DateTime Opened { get; set; }
    /// <summary>Zero-based page the file was left on.</summary>
    public int Page { get; set; }
    public bool Pinned { get; set; }
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
    /// <summary>Keyed by lower-case path.</summary>
    public Dictionary<string, RecentInfo> RecentDetails { get; set; } = new();
    /// <summary>"Grid" or "List" layout for recent files on Home.</summary>
    public string HomeLayout { get; set; } = "Grid";
    public List<SavedSignature> Signatures { get; set; } = new();
    public double TextSize { get; set; } = 11;
    public string InkColor { get; set; } = "#000000";
    public string AnnotationColor { get; set; } = "#FFD400";
    public double AnnotationWidth { get; set; } = 2;
    /// <summary>
    /// Look for a newer release on GitHub at startup. This is the only thing PDFPlus ever sends over the
    /// network, it carries nothing about the user or their files, and it can be turned off in the More menu.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>A version the user chose to skip, so the same one isn't offered again.</summary>
    public string SkippedVersion { get; set; } = "";
    public DateTime LastUpdateCheck { get; set; }
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

    private const int MaxRecent = 30;

    private static string Key(string path) => path.ToLowerInvariant();

    public RecentInfo? DetailsFor(string path) => RecentDetails.GetValueOrDefault(Key(path));

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        var info = DetailsFor(path) ?? new RecentInfo();
        info.Opened = DateTime.Now;
        RecentDetails[Key(path)] = info;

        // Pinned files never fall off the list; the rest keep the newest MaxRecent.
        var unpinned = 0;
        for (var i = 0; i < RecentFiles.Count; i++)
        {
            if (DetailsFor(RecentFiles[i])?.Pinned == true || ++unpinned <= MaxRecent) continue;
            RecentDetails.Remove(Key(RecentFiles[i]));
            RecentFiles.RemoveAt(i--);
        }
        Save();
    }

    public void SetPinned(string path, bool pinned)
    {
        if (!RecentFiles.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) RecentFiles.Add(path);
        var info = DetailsFor(path) ?? new RecentInfo();
        info.Pinned = pinned;
        RecentDetails[Key(path)] = info;
        Save();
    }

    public void RemoveRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentDetails.Remove(Key(path));
        Save();
    }

    /// <summary>Forgets every recent file that isn't pinned.</summary>
    public void ClearRecent()
    {
        foreach (var path in RecentFiles.Where(p => DetailsFor(p)?.Pinned != true).ToList()) RemoveRecent(path);
        Save();
    }

    /// <summary>Remembers the page a file was left on; written with the next save.</summary>
    public void RememberPage(string path, int page)
    {
        if (DetailsFor(path) is { } info) info.Page = Math.Max(0, page);
    }
}
