using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PDFPlus.Core;
using PDFPlus.Services;

namespace PDFPlus.Views;

public enum AskResult { Primary, Secondary, Cancel }

/// <summary>Small themed dialogs built in code so they match the app in light and dark mode.</summary>
public static class Dialogs
{
    internal static Window CreateWindow(Window? owner, string title)
    {
        var window = new Window
        {
            Title = title,
            Style = (Style)Application.Current.FindResource("DialogWindow"),
        };
        // WindowStartupLocation isn't a dependency property, so it can't live in the DialogWindow style.
        if (owner is { IsLoaded: true })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        ThemeManager.ApplyTitleBar(window);
        return window;
    }

    internal static StackPanel Body(string heading, string? message)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20), MaxWidth = 460, MinWidth = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        if (!string.IsNullOrWhiteSpace(message))
        {
            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
            panel.Children.Add(text);
        }
        return panel;
    }

    internal static Button MakeButton(string text, bool accent)
    {
        return new Button
        {
            Content = text,
            Style = (Style)Application.Current.FindResource(accent ? "AccentButton" : "SecondaryButton"),
            Margin = new Thickness(8, 0, 0, 0),
        };
    }

    public static void Error(Window? owner, string heading, string message) => Info(owner, heading, message, "PDFPlus");

    public static void Info(Window? owner, string heading, string message, string title = "PDFPlus")
    {
        var window = CreateWindow(owner, title);
        var body = Body(heading, message);
        var ok = MakeButton("OK", true);
        ok.IsDefault = true;
        ok.IsCancel = true;
        ok.Click += (_, _) => window.Close();
        body.Children.Add(Row(ok));
        window.Content = body;
        window.ShowDialog();
    }

    public static AskResult Ask(Window? owner, string heading, string message, string primary, string? secondary, string? cancel = "Cancel")
    {
        var result = AskResult.Cancel;
        var window = CreateWindow(owner, "PDFPlus");
        var body = Body(heading, message);
        var buttons = new List<Button>();

        var primaryButton = MakeButton(primary, true);
        primaryButton.IsDefault = true;
        primaryButton.Click += (_, _) => { result = AskResult.Primary; window.Close(); };
        buttons.Add(primaryButton);

        if (secondary != null)
        {
            var secondaryButton = MakeButton(secondary, false);
            secondaryButton.Click += (_, _) => { result = AskResult.Secondary; window.Close(); };
            buttons.Add(secondaryButton);
        }
        if (cancel != null)
        {
            var cancelButton = MakeButton(cancel, false);
            cancelButton.IsCancel = true;
            cancelButton.Click += (_, _) => window.Close();
            buttons.Add(cancelButton);
        }

        body.Children.Add(Row(buttons.ToArray()));
        window.Content = body;
        window.ShowDialog();
        return result;
    }

    public static string? Input(Window? owner, string heading, string message, string initial)
    {
        string? result = null;
        var window = CreateWindow(owner, "PDFPlus");
        var body = Body(heading, message);
        var box = new TextBox { Text = initial, Height = 32, Margin = new Thickness(0, 10, 0, 0) };
        body.Children.Add(box);

        var ok = MakeButton("OK", true);
        ok.IsDefault = true;
        ok.Click += (_, _) => { result = box.Text; window.Close(); };
        var cancel = MakeButton("Cancel", false);
        cancel.IsCancel = true;
        body.Children.Add(Row(ok, cancel));

        window.Content = body;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        window.ShowDialog();
        return result;
    }

    public static string? Password(Window? owner, string fileName, bool wrongPassword)
    {
        string? result = null;
        var window = CreateWindow(owner, "Password required");
        var body = Body(wrongPassword ? "That password didn't work" : "This PDF is password protected",
            $"Enter the password to open \"{fileName}\".");
        var box = new PasswordBox { Height = 32, Margin = new Thickness(0, 10, 0, 0) };
        body.Children.Add(box);

        var ok = MakeButton("Open", true);
        ok.IsDefault = true;
        ok.Click += (_, _) => { result = box.Password; window.Close(); };
        var cancel = MakeButton("Cancel", false);
        cancel.IsCancel = true;
        body.Children.Add(Row(ok, cancel));

        window.Content = body;
        window.Loaded += (_, _) => box.Focus();
        window.ShowDialog();
        return result;
    }

    /// <summary>Checks a file can be opened, prompting for a password if needed.</summary>
    internal static bool TryResolvePassword(Window? owner, string path, out string? password)
    {
        password = null;
        while (true)
        {
            try
            {
                using var file = PdfFile.Load(path, password);
                return true;
            }
            catch (PdfPasswordRequiredException ex)
            {
                password = Password(owner, System.IO.Path.GetFileName(path), ex.WrongPassword);
                if (password == null) return false;
            }
            catch (Exception ex)
            {
                Error(owner, $"Couldn't open {System.IO.Path.GetFileName(path)}", ex.Message);
                return false;
            }
        }
    }

    internal static StackPanel Row(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }
}
