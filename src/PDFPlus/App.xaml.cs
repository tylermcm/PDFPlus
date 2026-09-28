using System.IO;
using System.Windows;
using System.Windows.Threading;
using PDFPlus.Core;
using PDFPlus.Services;
using PDFPlus.Views;

namespace PDFPlus;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 3 && e.Args[0] is "--selftest" or "--uitest" or "--textcheck")
        {
            // Development test modes; they never touch the user's saved settings.
            base.OnStartup(e);
            AppSettings.Persist = false;
            ThemeManager.Apply("Light");
            RenderService.Start(Dispatcher);
            var mode = e.Args[0];
            Dispatcher.BeginInvoke(async () => Shutdown(mode switch
            {
                "--uitest" => await UiTest.RunAsync(e.Args[1], e.Args[2]),
                "--textcheck" => await TextCheck.RunAsync(e.Args[1], e.Args[2]),
                _ => await SelfTest.RunAsync(e.Args[1], e.Args[2]),
            }));
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0] == "--updatecheck")
        {
            // Development check of the release plumbing; writes %TEMP%\PDFPlus\updatecheck.log.
            base.OnStartup(e);
            AppSettings.Persist = false;
            Dispatcher.BeginInvoke(async () => Shutdown(await UpdateCheckReport.RunAsync()));
            return;
        }

        Timeline.Mark("app start");
        var files =e.Args.Where(a => !a.StartsWith('-') && File.Exists(a)).ToArray();

        if (!SingleInstance.TryClaim() && SingleInstance.SendToPrimary(files))
        {
            Shutdown();
            return;
        }
        Timeline.Mark("single instance claimed");

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        Timeline.Measure("settings loaded", AppSettings.Load);
        Timeline.Measure("theme applied", () => ThemeManager.Apply(AppSettings.Current.Theme));
        Timeline.Measure("pdfium initialised", PdfLibrary.EnsureInitialized);
        Timeline.Measure("render thread started", () => RenderService.Start(Dispatcher));

        var window = Timeline.Measure("main window built", () => new MainWindow(deferHome: files.Length > 0));
        MainWindow = window;
        Timeline.Measure("window shown", window.Show);

        // The first frame is what the user actually waits for; everything after it is the app settling.
        // Posting these rather than using ContentRendered keeps it reliable whatever the window does.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Timeline.Mark("first frame on screen"));
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            Timeline.Mark("idle");
            Timeline.Write();
        });

        if (files.Length > 0)
        {
            // Home stays unbuilt until the documents are in. If every one of them fails to open, the finally
            // brings Home back, so the window is never left empty.
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    foreach (var file in files) await window.OpenFileAsync(file);
                }
                finally
                {
                    window.DeferHome = false;
                    window.RefreshChrome();
                }
            });
        }

        SingleInstance.Listen(paths => Dispatcher.BeginInvoke(() =>
        {
            window.BringToFront();
            foreach (var path in paths) _ = window.OpenFileAsync(path);
        }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Safety net: a start that never went idle is exactly the one worth having a log for.
        Timeline.Write();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Dialogs.Error(MainWindow, "Something went wrong", e.Exception.Message);
    }
}
