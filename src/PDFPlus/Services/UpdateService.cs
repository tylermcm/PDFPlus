using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PDFPlus.Services;

/// <summary>A newer release than the one running, and the file to download for it.</summary>
public sealed record UpdateInfo(Version Version, string Title, string Notes, string DownloadUrl, string FileName, long Size, string ReleasePage)
{
    public bool IsInstaller => FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                              || FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>How an update check ended. "Failed" and "up to date" look the same to the user otherwise.</summary>
public enum UpdateCheckStatus { UpToDate, UpdateAvailable, Failed }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update)
{
    public static readonly UpdateCheckResult UpToDate = new(UpdateCheckStatus.UpToDate, null);
    public static readonly UpdateCheckResult Failed = new(UpdateCheckStatus.Failed, null);
}

/// <summary>
/// Looks for a newer PDFPlus on the project's GitHub releases page, downloads it and hands it to Windows to
/// install. Nothing about the user or their files is sent: the check is a plain GET of the public release list,
/// and it can be turned off entirely in the More menu.
/// </summary>
internal static class UpdateService
{
    private const string LatestRelease = "https://api.github.com/repos/tylermcm/PDFPlus/releases/latest";
    private const string ReleasesPage = "https://github.com/tylermcm/PDFPlus/releases";

    /// <summary>Hosts a download may come from. The URL arrives in a web response, so it doesn't get blind trust.</summary>
    private static readonly string[] AllowedHosts =
        ["github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    public static Version CurrentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PDFPlus", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>How long to leave it before looking again, when checking automatically at startup.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    public static string ReleasesUrl => ReleasesPage;

    /// <summary>
    /// Asks GitHub for the latest release. A failed check is reported rather than thrown: the automatic check
    /// at startup ignores it, and only the menu command tells the user about it.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync(LatestRelease, token);
            if (!response.IsSuccessStatusCode) return UpdateCheckResult.Failed;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var release = document.RootElement;
            if (Read(release, "draft") is { ValueKind: JsonValueKind.True }) return UpdateCheckResult.UpToDate;

            var tag = Text(release, "tag_name");
            var title = Text(release, "name");
            var notes = Text(release, "body");
            var page = Text(release, "html_url");
            if (page.Length == 0) page = ReleasesPage;

            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return UpdateCheckResult.UpToDate;

            // The asset filename is the reliable place to read the version from: release tags aren't always
            // version numbers (this project has shipped one simply tagged "msi").
            var best = default(UpdateInfo);
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                var url = Text(asset, "browser_download_url");
                if (name.Length == 0 || !IsAllowed(url)) continue;
                if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                var version = ParseVersion(name) ?? ParseVersion(tag) ?? ParseVersion(title);
                if (version == null) continue;
                var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
                var candidate = new UpdateInfo(version, title.Length > 0 ? title : $"PDFPlus {version}", notes, url, name, size, page);

                // Newest wins; between two files of the same version prefer the .msi, which upgrades in place.
                if (best == null
                    || candidate.Version > best.Version
                    || (candidate.Version == best.Version && IsMsi(candidate.FileName) && !IsMsi(best.FileName)))
                    best = candidate;
            }

            return best != null && best.Version > CurrentVersion
                ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, best)
                : UpdateCheckResult.UpToDate;
        }
        catch
        {
            // Offline, blocked by a proxy, rate limited, or the response wasn't what we expected.
            return UpdateCheckResult.Failed;
        }
    }

    /// <summary>Downloads the release file to a temporary folder and returns its path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken token)
    {
        if (!IsAllowed(update.DownloadUrl)) throw new InvalidOperationException("The download link isn't a GitHub address.");

        var folder = Path.Combine(Path.GetTempPath(), "PDFPlus", "updates");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileName(update.FileName));

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Size;

            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[128 * 1024];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                copied += read;
                if (total > 0) progress?.Report(Math.Min(1, (double)copied / total));
            }
        }

        // A truncated download would fail confusingly halfway through the install.
        var downloaded = new FileInfo(path).Length;
        if (update.Size > 0 && downloaded != update.Size)
        {
            File.Delete(path);
            throw new IOException($"The download was incomplete ({downloaded:N0} of {update.Size:N0} bytes).");
        }
        return path;
    }

    /// <summary>Hands the downloaded file to Windows. The app must already be closing: the MSI replaces its files.</summary>
    public static void Launch(string path)
    {
        var info = path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("msiexec.exe", $"/i \"{path}\"")
            : new ProcessStartInfo(path);
        info.UseShellExecute = true;
        Process.Start(info);
    }

    public static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
        }
        catch
        {
            // No browser registered; nothing useful to do.
        }
    }

    private static bool IsMsi(string fileName) => fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Pulls "1.2.3" out of a name like "PDFPlus-1.2.3-x64.msi" or a tag like "v1.2.3".</summary>
    private static Version? ParseVersion(string text)
    {
        var match = Regex.Match(text, @"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!match.Success) return null;
        int Part(int group) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value) : 0;
        return new Version(Part(1), Part(2), Part(3), Part(4));
    }

    private static JsonElement? Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : null;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
