using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.NetworkSetup;
using Remotely.UI.Localization;
using Windows.ApplicationModel.DataTransfer;

namespace Remotely.ServerManager.Setup;

/// <summary>Small builders the setup steps share. Every visible string goes through <see cref="Loc"/> (keys beginning Setup_ in Strings/*/Resources.resw).</summary>
internal static class SetupUi
{
    public static string T(string key) => Loc.Get(key);

    public static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Loc.Get(key), args);

    public static string F(GuideStep step) => string.Format(CultureInfo.CurrentCulture, Loc.Get(step.Key), step.Args.Cast<object>().ToArray());

    public static TextBlock Body(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    public static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    public static InfoBar Bar() => new() { IsOpen = false, IsClosable = false };

    public static void Show(InfoBar bar, InfoBarSeverity severity, string title, string? message = null)
    {
        bar.Severity = severity;
        bar.Title = title;
        bar.Message = message ?? "";
        bar.IsOpen = true;
    }

    public static StackPanel Column(double spacing = 12) => new() { Spacing = spacing };

    public static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    public static Button ActionButton(string text, Func<Task> work, InfoBar? errorBar = null)
    {
        var button = new Button { Content = text };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await work(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (errorBar is not null) Show(errorBar, InfoBarSeverity.Error, T("Setup_Err_Unexpected"), ex.Message);
            }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    public static Button CopyButton(Func<string> text)
    {
        var button = new Button { Content = T("Setup_Copy") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, T("Setup_Copy"));
        button.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(text());
            Clipboard.SetContent(package);
        };
        return button;
    }

    /// <summary>A "label: value [Copy]" line, for the values the user types into the router.</summary>
    public static UIElement ValueLine(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = Label(label);
        var v = new TextBlock { Text = value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        Grid.SetColumn(v, 1);
        var copy = CopyButton(() => value);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(l);
        grid.Children.Add(v);
        grid.Children.Add(copy);
        return grid;
    }
}
