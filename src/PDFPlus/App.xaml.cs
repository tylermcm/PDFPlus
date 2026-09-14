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
        if (e.Args.Length >= 3 && e.Args[0] is "--selftest" or "--uitest")
        {
            // Development test modes; they never touch the user's saved settings.
            base.OnStartup(e);
            AppSettings.Persist = false;
            ThemeManager.Apply("Light");
            RenderService.Start(Dispatcher);
            var uiTest = e.Args[0] == "--uitest";
            Dispatcher.BeginInvoke(async () => Shutdown(uiTest
                ? await UiTest.RunAsync(e.Args[1], e.Args[2])
                : await SelfTest.RunAsync(e.Args[1], e.Args[2])));
            return;
        }

        var files =e.Args.Where(a => !a.StartsWith('-') && File.Exists(a)).ToArray();

        if (!SingleInstance.TryClaim() && SingleInstance.SendToPrimary(files))
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        AppSettings.Load();
        ThemeManager.Apply(AppSettings.Current.Theme);
        PdfLibrary.EnsureInitialized();
        RenderService.Start(Dispatcher);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        foreach (var file in files) _ = window.OpenFileAsync(file);

        SingleInstance.Listen(paths => Dispatcher.BeginInvoke(() =>
        {
            window.BringToFront();
            foreach (var path in paths) _ = window.OpenFileAsync(path);
        }));
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Dialogs.Error(MainWindow, "Something went wrong", e.Exception.Message);
    }
}
