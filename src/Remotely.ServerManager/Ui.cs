using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Remotely.Core.ServerManager;
using Remotely.UI.Localization;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Remotely.ServerManager;

/// <summary>Small helpers the pages share: localised text, themed looks, error banners, clipboard, dialogs and file pickers.</summary>
internal static class Ui
{
    public static string T(string key) => Loc.Get(key);

    public static string T(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Loc.Get(key), args);

    public static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    public static TextBlock Text(string text, string? styleKey = null, double size = 0, bool semiBold = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (styleKey is not null) block.Style = StyleOf(styleKey);
        if (size > 0) block.FontSize = size;
        if (semiBold) block.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return block;
    }

    public static TextBlock Secondary(string text) => Text(text, "SecondaryTextStyle");

    public static Border Card(UIElement child)
    {
        var card = new Border { Style = StyleOf("CardStyle") };
        card.Child = child;
        return card;
    }

    public static Ellipse Dot(string styleKey) => new() { Style = StyleOf(styleKey) };

    public static string When(DateTimeOffset utc) => utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string LastSeenText(LastSeenInfo info) => info.Kind switch
    {
        LastSeenKind.Online => T("Seen_Online"),
        LastSeenKind.Never => T("Seen_Never"),
        LastSeenKind.JustNow => T("Seen_JustNow"),
        LastSeenKind.Minutes => T("Seen_Minutes", info.Count),
        LastSeenKind.Hours => T("Seen_Hours", info.Count),
        LastSeenKind.Days => T("Seen_Days", info.Count),
        _ => T("Seen_Date", info.Date is { } d ? When(d) : ""),
    };

    /// <summary>The server decides what kinds and results exist; known ones get a translated label and anything else is shown as the server wrote it.</summary>
    public static string Label(string prefix, string value)
    {
        var key = prefix + value.ToLowerInvariant();
        var text = Loc.Get(key);
        return text == key ? value : text;
    }

    public static void Show(InfoBar bar, InfoBarSeverity severity, string message)
    {
        bar.Severity = severity;
        bar.Message = message;
        bar.IsOpen = true;
    }

    public static string Describe(Exception ex) => ex is ServerUnavailableException ? ex.Message : ex.GetType().Name + ": " + ex.Message;

    /// <summary>Runs an admin call and shows any failure in the page's banner. Returns true when it worked.</summary>
    public static async Task<bool> GuardAsync(Func<Task> work, InfoBar bar)
    {
        try
        {
            await work();
            bar.IsOpen = false;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Show(bar, InfoBarSeverity.Error, Describe(ex));
            return false;
        }
    }

    public static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        try { Clipboard.Flush(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }   // keeping the text after the app exits is a courtesy
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string body, string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = primaryText,
            CloseButtonText = T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(App.Main);

    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public static async Task<string?> PickSaveFileAsync(string suggestedName, string typeLabel, string extension)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add(typeLabel, new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public static async Task<string?> PickOpenFileAsync(string extension)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static Button SmallButton(string text, RoutedEventHandler click, bool accent = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 5, 12, 6) };
        if (accent) button.Style = StyleOf("AccentButtonStyle");
        button.Click += click;
        return button;
    }
}
