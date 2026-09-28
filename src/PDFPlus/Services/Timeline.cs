using System.Diagnostics;
using System.IO;
using System.Text;

namespace PDFPlus.Services;

/// <summary>
/// Records how long each part of starting up took, and writes it to %TEMP%\PDFPlus\startup.log once the app
/// is idle. Collecting a mark is a string and a long, so this is always on: the interesting slow starts are
/// the ones on someone else's machine, and asking them to reproduce with a flag rarely works.
///
/// The clock starts when the process did, not when managed code began, so the time the single-file bundle
/// spends unpacking itself before Main is visible rather than invisible.
/// </summary>
internal static class Timeline
{
    private readonly record struct Entry(string Name, double Milliseconds);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly DateTime ManagedStart = DateTime.UtcNow;
    private static readonly List<Entry> Marks = new();
    private static readonly object Gate = new();
    private static bool _written;

    /// <summary>Time from the process being created to the first line of managed code, measured lazily.</summary>
    private static double BeforeManagedCode()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var elapsed = (ManagedStart - process.StartTime.ToUniversalTime()).TotalMilliseconds;
            return elapsed is > 0 and < 120_000 ? elapsed : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static void Mark(string name)
    {
        lock (Gate) Marks.Add(new Entry(name, Clock.Elapsed.TotalMilliseconds));
    }

    /// <summary>Times one step and records it. Returns whatever the step returned.</summary>
    public static T Measure<T>(string name, Func<T> step)
    {
        var result = step();
        Mark(name);
        return result;
    }

    public static void Measure(string name, Action step)
    {
        step();
        Mark(name);
    }

    /// <summary>Writes the log. Safe to call more than once; only the first call does anything.</summary>
    public static void Write()
    {
        lock (Gate)
        {
            if (_written) return;
            _written = true;
        }

        try
        {
            var before = BeforeManagedCode();
            var report = new StringBuilder();
            report.AppendLine($"PDFPlus startup  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"{"",-42}{"at",8}{"took",9}");
            report.AppendLine(new string('-', 59));

            var previous = 0.0;
            if (before > 0)
            {
                report.AppendLine($"{"runtime start, before Main",-42}{before,8:N0}{before,9:N0}");
                previous = 0;
            }

            foreach (var mark in Marks)
            {
                report.AppendLine($"{mark.Name,-42}{before + mark.Milliseconds,8:N0}{mark.Milliseconds - previous,9:N0}");
                previous = mark.Milliseconds;
            }

            report.AppendLine(new string('-', 59));
            report.AppendLine($"{"total",-42}{before + previous,8:N0}");

            var folder = Path.Combine(Path.GetTempPath(), "PDFPlus");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "startup.log"), report.ToString());
        }
        catch
        {
            // Timing is a diagnostic; never let it affect the app.
        }
    }
}
