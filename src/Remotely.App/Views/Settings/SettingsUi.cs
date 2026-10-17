using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Remotely.UI.Controls;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Settings;

/// <summary>Small builders so the settings sections look alike and use the Portico text styles (looked up by name; a missing style falls back to the default text).</summary>
internal static class SettingsUi
{
    private static Style? Find(string key) => Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    public static TextBlock Title(string text) => new() { Text = text, Style = Find("Portico.Text.Title"), TextWrapping = TextWrapping.Wrap };

    public static TextBlock Subtitle(string text) => new() { Text = text, Style = Find("Portico.Text.Subtitle"), TextWrapping = TextWrapping.Wrap };

    public static TextBlock Strong(string text) => new() { Text = text, Style = Find("Portico.Text.BodyStrong"), TextWrapping = TextWrapping.Wrap };

    public static TextBlock Secondary(string text) => new() { Text = text, Style = Find("Portico.Text.Secondary"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false };

    public static TextBlock Mono(string text) => new() { Text = text, Style = Find("Portico.Text.Mono"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    public static Card CardOf(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var child in children) panel.Children.Add(child);
        return new Card { Content = panel };
    }

    public static Button ButtonOf(string textKey, RoutedEventHandler click)
    {
        var button = new Button { Content = Loc.Get(textKey) };
        AutomationProperties.SetName(button, Loc.Get(textKey));
        button.Click += click;
        return button;
    }

    public static InfoBar Bar(InfoBarSeverity severity, string message) => new() { Severity = severity, Message = message, IsOpen = true, IsClosable = false };

    public static string Format(string key, params object[] args) => string.Format(Loc.Get(key), args);
}
