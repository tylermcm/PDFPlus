using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PDFPlus.Services;

namespace PDFPlus.Views;

/// <summary>What the user decided about an available update, and what came of it.</summary>
internal sealed record UpdateOutcome(bool Skip, string? InstallerPath, string? Error = null);

/// <summary>
/// Offers an available update, then downloads it in place with a progress bar. Built in code like the other
/// dialogs so it matches the app in light and dark mode.
/// </summary>
internal static class UpdateDialog
{
    /// <summary>Release notes come from the internet, so they're shown as plain text and kept to a sensible length.</summary>
    private const int MaxNotes = 1200;

    public static UpdateOutcome Show(Window? owner, UpdateInfo update)
    {
        var outcome = new UpdateOutcome(false, null);
        var closing = false;
        var window = Dialogs.CreateWindow(owner, "Update PDFPlus");

        var body = Dialogs.Body($"{update.Title} is available",
            $"You have version {UpdateService.CurrentVersion.ToString(3)}. " +
            $"The download is {update.Size / 1024.0 / 1024.0:N0} MB and comes from the PDFPlus releases page on GitHub.");

        if (update.Notes.Trim() is { Length: > 0 } notes)
        {
            var notesBox = new TextBox
            {
                Text = notes.Length > MaxNotes ? notes[..MaxNotes].TrimEnd() + "…" : notes,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 160,
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(8),
                BorderThickness = new Thickness(1),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            notesBox.SetResourceReference(Control.BorderBrushProperty, "Brush.Border");
            notesBox.SetResourceReference(Control.BackgroundProperty, "Brush.Sidebar");
            body.Children.Add(notesBox);
        }

        // Progress, hidden until the download starts.
        var status = new TextBlock { Margin = new Thickness(0, 14, 0, 6), Visibility = Visibility.Collapsed };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Visibility = Visibility.Collapsed };
        track.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        var fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        fill.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        track.Child = fill;
        body.Children.Add(status);
        body.Children.Add(track);

        var install = Dialogs.MakeButton("Download and install", true);
        install.IsDefault = true;
        var skip = Dialogs.MakeButton("Skip this version", false);
        var later = Dialogs.MakeButton("Later", false);
        later.IsCancel = true;
        var buttons = Dialogs.Row(install, skip, later);
        body.Children.Add(buttons);

        var cancellation = new CancellationTokenSource();

        void Finish(UpdateOutcome result)
        {
            if (closing) return;
            closing = true;
            outcome = result;
            window.Close();
        }

        skip.Click += (_, _) => Finish(new UpdateOutcome(true, null));
        later.Click += (_, _) => Finish(outcome);

        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            skip.Visibility = Visibility.Collapsed;
            later.Content = "Cancel";
            status.Visibility = Visibility.Visible;
            track.Visibility = Visibility.Visible;
            status.Text = "Starting download…";

            var progress = new Progress<double>(fraction =>
            {
                status.Text = $"Downloading… {fraction * 100:N0}%";
                fill.Width = track.ActualWidth * fraction;
            });

            try
            {
                var path = await UpdateService.DownloadAsync(update, progress, cancellation.Token);
                Finish(new UpdateOutcome(false, path));
            }
            catch (OperationCanceledException)
            {
                Finish(new UpdateOutcome(false, null));
            }
            catch (Exception ex)
            {
                // Reported by the caller, once this dialog is out of the way.
                Finish(new UpdateOutcome(false, null, ex.Message));
            }
        };

        // Covers the window being closed from its title bar as well as from the buttons.
        window.Closed += (_, _) =>
        {
            closing = true;
            cancellation.Cancel();
        };
        window.Content = body;
        window.ShowDialog();
        return outcome;
    }

    /// <summary>Shown by "Check for updates" when there is nothing new.</summary>
    public static void ShowUpToDate(Window? owner) => Dialogs.Info(owner, "PDFPlus is up to date",
        $"You're running version {UpdateService.CurrentVersion.ToString(3)}, which is the latest release.", "Check for updates");

    /// <summary>Shown by "Check for updates" when the check itself couldn't be completed.</summary>
    public static void ShowCheckFailed(Window? owner) => Dialogs.Info(owner, "Couldn't check for updates",
        "PDFPlus couldn't reach the GitHub releases page. Check your internet connection and try again.", "Check for updates");
}
