using System.IO;

namespace PDFPlus.Services;

/// <summary>
/// Reports what the update check finds, without changing anything:
/// PDFPlus.exe --updatecheck  (see %TEMP%\PDFPlus\updatecheck.log)
///
/// Used when cutting a release, to confirm the new release's files are named in a way the app can read.
/// </summary>
internal static class UpdateCheckReport
{
    public static async Task<int> RunAsync()
    {
        var result = await UpdateService.CheckAsync(CancellationToken.None);
        var report = result.Update is not { } update
            ? $"running {UpdateService.CurrentVersion}\ncheck result   {result.Status}"
            : $"running {UpdateService.CurrentVersion}\n" +
              $"check result   {result.Status}\n" +
              $"newer release  {update.Version}\n" +
              $"title          {update.Title}\n" +
              $"file           {update.FileName} ({update.Size:N0} bytes)\n" +
              $"download       {update.DownloadUrl}\n" +
              $"release page   {update.ReleasePage}";

        var folder = Path.Combine(Path.GetTempPath(), "PDFPlus");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "updatecheck.log"), report);
        return 0;
    }
}
