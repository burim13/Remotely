using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Update;

namespace Remotely.App.Update;

/// <summary>
/// The "Updates" block for the Settings page: the opt-in switch (off by default), the current status, and the buttons Check now, Download, Open the installer and Skip.
/// Host it with <c>new Remotely.App.Update.UpdateSettingsSection()</c> or <c>&lt;update:UpdateSettingsSection /&gt;</c> (<c>xmlns:update="using:Remotely.App.Update"</c>).
/// It is built in code so it needs no XAML of its own; all words come from <see cref="AppUpdates.Text(string)"/> (resource keys <c>Update_*</c>, English fallback built in).
/// </summary>
public sealed class UpdateSettingsSection : UserControl
{
    private readonly UpdateController _controller = AppUpdates.Controller;
    private readonly ToggleSwitch _toggle = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _signature = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly Button _check = new();
    private readonly Button _download = new();
    private readonly Button _open = new();
    private readonly Button _skip = new();
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private bool _syncing;

    public UpdateSettingsSection()
    {
        var title = new TextBlock { Text = AppUpdates.Text("Update_Section_Title"), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 12, 0, 0) };
        var description = new TextBlock { Text = AppUpdates.Text("Update_Toggle_Description"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        _toggle.Header = AppUpdates.Text("Update_Toggle_Header");
        _toggle.OnContent = AppUpdates.Text("Update_Toggle_On");
        _toggle.OffContent = AppUpdates.Text("Update_Toggle_Off");
        AutomationProperties.SetName(_toggle, AppUpdates.Text("Update_Toggle_Header"));
        _toggle.Toggled += OnToggled;

        _check.Content = AppUpdates.Text("Update_CheckNow");
        _download.Content = AppUpdates.Text("Update_Download");
        _open.Content = AppUpdates.Text("Update_OpenInstaller");
        _skip.Content = AppUpdates.Text("Update_Skip");
        _check.Click += async (_, _) => await _controller.CheckNowAsync();
        _download.Click += async (_, _) => await _controller.DownloadAsync();
        _skip.Click += (_, _) => _controller.SkipThisVersion();
        _open.Click += async (_, _) =>
        {
            if (!await AppUpdates.OpenInstallerAsync()) _status.Text = AppUpdates.Text("Update_Changed_Warning");
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_check);
        buttons.Children.Add(_download);
        buttons.Children.Add(_open);
        buttons.Children.Add(_skip);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(title);
        panel.Children.Add(description);
        panel.Children.Add(_toggle);
        panel.Children.Add(_status);
        panel.Children.Add(_progress);
        panel.Children.Add(_signature);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _controller.Changed += OnChanged; Refresh(); };
        Unloaded += (_, _) => _controller.Changed -= OnChanged;
    }

    private void OnChanged() => _ui.TryEnqueue(Refresh);

    private void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _controller.SetEnabled(_toggle.IsOn);
        if (_toggle.IsOn) _ = AppUpdates.CheckIfDueSafeAsync();
    }

    private void Refresh()
    {
        var state = _controller.State;
        _syncing = true;
        _toggle.IsOn = _controller.Enabled;
        _syncing = false;
        _status.Text = AppUpdates.Text(UpdatePresentation.Status(state, _controller.Current));
        var sig = UpdatePresentation.Signature(state);
        _signature.Text = sig is null ? "" : AppUpdates.Text(sig);
        _signature.Visibility = sig is null ? Visibility.Collapsed : Visibility.Visible;
        _progress.Visibility = state.Phase == UpdatePhase.Downloading ? Visibility.Visible : Visibility.Collapsed;
        _progress.Value = state.Progress;
        var busy = state.Phase is UpdatePhase.Checking or UpdatePhase.Downloading;
        _check.IsEnabled = !busy;
        _download.Visibility = state.Phase == UpdatePhase.Available ? Visibility.Visible : Visibility.Collapsed;
        _skip.Visibility = state.Phase is UpdatePhase.Available or UpdatePhase.Ready ? Visibility.Visible : Visibility.Collapsed;
        _open.Visibility = state.Phase == UpdatePhase.Ready ? Visibility.Visible : Visibility.Collapsed;
    }
}
