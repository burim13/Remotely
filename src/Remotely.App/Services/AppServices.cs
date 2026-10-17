using Microsoft.UI.Xaml.Controls;
using Remotely.UI.Localization;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Remotely.App.Services;

/// <summary>The one implementation of <see cref="IAppServices"/>: pages call this instead of reaching into <c>App</c> statics. Dialogs run one at a time (WinUI allows only one open ContentDialog per window).</summary>
public sealed class AppServices : IAppServices
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    /// <summary>Raised after the tray menu (or anything else in the shell) started or stopped sharing, so a page showing the share state can refresh.</summary>
    public event Action? ShareStateChanged;

    internal void RaiseShareStateChanged() => ShareStateChanged?.Invoke();

    public void Navigate(string page) => App.Main?.Show(page);

    public async Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            PrimaryButtonText = accept,
            CloseButtonText = cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task ShowErrorAsync(string title, string message, string? details = null)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(details))
        {
            var box = new TextBox
            {
                Text = details,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap,
                MaxHeight = 220,
                MinWidth = 420,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            panel.Children.Add(box);
        }
        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            CloseButtonText = Text("Dialog_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (!string.IsNullOrEmpty(details))
        {
            dialog.PrimaryButtonText = Text("Dialog_CopyDetails");
            dialog.PrimaryButtonClick += (d, args) =>
            {
                args.Cancel = true;   // stay open, so the person can see the text was copied and still read it
                CopyText(details);
                d.PrimaryButtonText = Text("Dialog_Copied");
            };
        }
        await ShowAsync(dialog);
    }

    public void Toast(string title, string message)
    {
        if (App.Main is { } main && main.TryShowToast(title, message)) return;
        App.Tray?.Notify(title, message);
    }

    public Task<string?> PickFileAsync() => PickAsync(async hwnd =>
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return (await picker.PickSingleFileAsync())?.Path;
    });

    public Task<string?> PickFolderAsync() => PickAsync(async hwnd =>
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return (await picker.PickSingleFolderAsync())?.Path;
    });

    public void CopyText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    public string Text(string resourceKey) => Loc.Get(resourceKey);

    private static async Task<string?> PickAsync(Func<IntPtr, Task<string?>> pick)
    {
        if (App.Main is not { } main) return null;
        return await pick(WinRT.Interop.WindowNative.GetWindowHandle(main));
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _dialogGate.WaitAsync();
        try
        {
            if (App.Main?.Content?.XamlRoot is not { } root) return ContentDialogResult.None;
            dialog.XamlRoot = root;
            if (App.Main.Content is Microsoft.UI.Xaml.FrameworkElement fe) dialog.RequestedTheme = fe.RequestedTheme;
            return await dialog.ShowAsync();
        }
        finally { _dialogGate.Release(); }
    }
}
