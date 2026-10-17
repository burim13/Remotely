using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Update;
using Remotely.UI.Controls;

namespace Remotely.ServerManager.Update;

/// <summary>
/// A banner that appears at the top of the shell only while an update is available, downloading or ready (never while update checks are off). One button: Download, then
/// Open the installer. It is collapsed again by its close button until the state changes. Host it above the page content, for example
/// <c>&lt;update:UpdateBanner /&gt;</c> with <c>xmlns:update="using:Remotely.ServerManager.Update"</c>; it needs no other wiring.
/// </summary>
public sealed class UpdateBanner : UserControl
{
    private readonly UpdateController _controller = ManagerUpdates.Controller;
    private readonly InlineBanner _banner = new() { Kind = StatusKind.Info, IsDismissible = true };
    private readonly Button _action = new();
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private UpdatePhase _dismissedAt = UpdatePhase.Off;

    public UpdateBanner()
    {
        _banner.ActionContent = _action;
        _banner.Dismissed += (_, _) => { _dismissedAt = _controller.State.Phase; Refresh(); };
        _action.Click += async (_, _) =>
        {
            if (_controller.State.Phase == UpdatePhase.Available) await _controller.DownloadAsync();
            else if (_controller.State.Phase == UpdatePhase.Ready && !await ManagerUpdates.OpenInstallerAsync())
                _banner.Message = ManagerUpdates.Text("Update_Changed_Warning");
        };
        Content = _banner;
        Visibility = Visibility.Collapsed;
        Loaded += (_, _) => { _controller.Changed += OnChanged; Refresh(); };
        Unloaded += (_, _) => _controller.Changed -= OnChanged;
    }

    private void OnChanged() => _ui.TryEnqueue(Refresh);

    private void Refresh()
    {
        var state = _controller.State;
        if (state.Phase != _dismissedAt) _dismissedAt = UpdatePhase.Off;
        var show = UpdatePresentation.ShowBanner(state) && _dismissedAt == UpdatePhase.Off;
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var version = state.Offer?.Version.ToString() ?? "";
        _banner.Title = UpdatePresentation.Format(ManagerUpdates.Text("Update_Banner_Title"), [version]);
        _banner.Message = state.Phase == UpdatePhase.Ready
            ? UpdatePresentation.Format(ManagerUpdates.Text("Update_Banner_Ready"), [version])
            : ManagerUpdates.Text("Update_Banner_Message");
        _action.Content = ManagerUpdates.Text(state.Phase == UpdatePhase.Ready ? "Update_OpenInstaller" : "Update_Download");
        _action.IsEnabled = state.Phase != UpdatePhase.Downloading;
    }
}
