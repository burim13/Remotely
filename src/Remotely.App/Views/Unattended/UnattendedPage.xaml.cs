using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.App.Shell;
using Remotely.UI.Localization;
using Remotely.Unattended;
using Windows.ApplicationModel.DataTransfer;

namespace Remotely.App.Views.Unattended;

/// <summary>
/// Unattended access: the state of the Remotely service with install, start, stop and remove (each one administrator prompt, never repeated), a plain-language explanation of the lock
/// screen and administrator prompts with a live status line, same-network sharing through the service, the Ctrl+Alt+Del policy and the "stay awake" request. What it does and how the
/// pieces talk is written down in docs/unattended.md. The page only shows what <see cref="UnattendedController"/> found out; it keeps no state of its own about the service.
/// </summary>
[NavigationPage("unattended", "Unattended_Nav", Order = 40, Glyph = 0xE72E)]
public sealed partial class UnattendedPage : Page
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly UnattendedController _controller = UnattendedController.Instance;
    private bool _syncing;
    private bool _busy;
    private DateTimeOffset _copiedUntil;

    public UnattendedPage()
    {
        InitializeComponent();
        ApplyText();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static string Fmt(string key, params string[] args) => string.Format(Loc.Get(key), args);

    private void ApplyText()
    {
        TitleText.Text = Loc.Get("Unattended_Title");
        IntroText.Text = Loc.Get("Unattended_Intro");
        LiveBar.Title = Loc.Get("Unattended_Live_Title");
        ServiceTitle.Text = Loc.Get("Unattended_Service_Title");
        ServiceHelp.Text = Loc.Get("Unattended_Service_Help");
        InstallButton.Content = Loc.Get("Unattended_Install");
        StartButton.Content = Loc.Get("Unattended_Start");
        StopButton.Content = Loc.Get("Unattended_Stop");
        UninstallButton.Content = Loc.Get("Unattended_Uninstall");
        RecoveryText.Text = Loc.Get("Unattended_Recovery");
        LockTitle.Text = Loc.Get("Unattended_Lock_Title");
        LockBody1.Text = Loc.Get("Unattended_Lock_Body1");
        LockBody2.Text = Loc.Get("Unattended_Lock_Body2");
        LockBody3.Text = Loc.Get("Unattended_Lock_Body3");
        LanTitle.Text = Loc.Get("Unattended_Lan_Title");
        LanHelp.Text = Loc.Get("Unattended_Lan_Help");
        LanNeedsService.Text = Loc.Get("Unattended_Lan_NeedsService");
        LanAllowedSwitch.Header = Loc.Get("Unattended_Lan_Allow_Label");
        LanAllowedSwitch.OnContent = Loc.Get("Unattended_Switch_On");
        LanAllowedSwitch.OffContent = Loc.Get("Unattended_Switch_Off");
        LanAllowedHelp.Text = Loc.Get("Unattended_Lan_Allow_Help");
        LanShareSwitch.Header = Loc.Get("Unattended_Lan_Share_Label");
        LanShareSwitch.OnContent = Loc.Get("Unattended_Lan_On");
        LanShareSwitch.OffContent = Loc.Get("Unattended_Lan_Off");
        TokenLabel.Text = Loc.Get("Unattended_Lan_Token");
        CopyButton.Content = Loc.Get("Unattended_Copy");
        NewTokenButton.Content = Loc.Get("Unattended_NewToken");
        DisconnectButton.Content = Loc.Get("Unattended_Lan_Disconnect");
        AskSwitch.Header = Loc.Get("Unattended_Ask_Label");
        AskSwitch.OnContent = Loc.Get("Unattended_Switch_On");
        AskSwitch.OffContent = Loc.Get("Unattended_Switch_Off");
        AskHelp.Text = Loc.Get("Unattended_Ask_Help");
        SasTitle.Text = Loc.Get("Unattended_Sas_Title");
        SasHelp.Text = Loc.Get("Unattended_Sas_Help");
        SasSwitch.Header = Loc.Get("Unattended_Sas_Switch");
        SasSwitch.OnContent = Loc.Get("Unattended_Switch_On");
        SasSwitch.OffContent = Loc.Get("Unattended_Switch_Off");
        AwakeTitle.Text = Loc.Get("Unattended_Awake_Title");
        AwakeHelp.Text = Loc.Get("Unattended_Awake_Help");
        AwakeSwitch.Header = Loc.Get("Unattended_Awake_Switch");
        AwakeSwitch.OnContent = Loc.Get("Unattended_Switch_On");
        AwakeSwitch.OffContent = Loc.Get("Unattended_Switch_Off");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TokenText, Loc.Get("Unattended_Lan_Token"));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _controller.Changed += OnChanged;
        _controller.Retain(_ui);
        if (ElevatedHost.Unavailable() is { } problem) ShowAction(problem);
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _controller.Changed -= OnChanged;
        _controller.Release();
    }

    private void OnChanged() => _ui.TryEnqueue(Render);

    // ---- drawing ----

    private static InfoBarSeverity SeverityOf(string lineKey) => lineKey switch
    {
        "Unattended_Line_NotInstalled" or "Unattended_Line_Stopped" or "Unattended_Line_Starting" or "Unattended_Line_Stopping" or "Unattended_Line_Paused" or "Unattended_Line_Asking" => InfoBarSeverity.Informational,
        "Unattended_Line_Unknown" or "Unattended_Line_LinkUnreachable" or "Unattended_Line_LinkDenied" or "Unattended_Line_LinkUntrusted" or "Unattended_Line_LinkBad" or "Unattended_Line_NoHelper" => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Success,
    };

    private void Render()
    {
        var view = _controller.View;
        var status = view.Status;
        var linked = view.Service == ServiceState.Running && status is not null;
        var canElevate = ElevatedHost.Unavailable() is null && !_busy;

        var line = StatusLine.Describe(view);
        LiveBar.Message = Fmt(line.Key, line.Args);
        LiveBar.Severity = SeverityOf(line.Key);

        ServiceStateText.Text = Loc.Get("Unattended_State_" + view.Service);
        InstallButton.Visibility = view.Service == ServiceState.NotInstalled ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = view.Service is ServiceState.Stopped or ServiceState.Paused ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = view.Service == ServiceState.Running ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.Visibility = view.Service is ServiceState.Stopped or ServiceState.Running or ServiceState.Paused ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = StartButton.IsEnabled = StopButton.IsEnabled = UninstallButton.IsEnabled = canElevate;

        _syncing = true;
        try
        {
            LanNeedsService.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;
            LanAllowedSwitch.IsOn = status?.LanAllowed ?? false;
            LanAllowedSwitch.IsEnabled = linked && canElevate;
            LanShareSwitch.IsOn = status?.Listening ?? false;
            LanShareSwitch.IsEnabled = linked && status!.LanAllowed;
            AskSwitch.IsOn = status?.AskBeforeConnect ?? true;
            AskSwitch.IsEnabled = linked && canElevate;
            AwakeSwitch.IsOn = status?.KeepAwake ?? true;
            AwakeSwitch.IsEnabled = linked && canElevate;
            SasSwitch.IsOn = view.SasAllowsServices ?? false;
            SasSwitch.IsEnabled = view.SasAllowsServices is not null && canElevate;
        }
        finally { _syncing = false; }

        var showToken = linked && status!.Listening && status.Token is not null;
        TokenPanel.Visibility = showToken ? Visibility.Visible : Visibility.Collapsed;
        if (showToken)
        {
            TokenText.Text = status!.Token!;
            var left = StatusLine.Remaining(status.TokenExpiresUtc, DateTimeOffset.UtcNow);
            TokenInfo.Text = Fmt("Unattended_Lan_Expires", $"{(int)left.TotalMinutes}:{left.Seconds:00}");
            if (CopyButton.Content is string && DateTimeOffset.UtcNow >= _copiedUntil) CopyButton.Content = Loc.Get("Unattended_Copy");
        }
        var inSession = linked && status!.Controller is not null;
        SessionPanel.Visibility = inSession ? Visibility.Visible : Visibility.Collapsed;
        if (inSession) SessionText.Text = Fmt("Unattended_Lan_Connected", status!.Controller!, status.ControllerAddress ?? "");

        SasState.Text = Loc.Get(view.SasAllowsServices switch { true => "Unattended_Sas_State_On", false => "Unattended_Sas_State_Off", _ => "Unattended_Sas_State_Unknown" });
        AwakeState.Text = linked ? Loc.Get(status!.AwakeHeld ? "Unattended_Awake_Held" : "Unattended_Awake_Idle") : "";
    }

    private void ShowAction(ElevatedOutcome outcome)
    {
        ActionBar.Severity = outcome switch
        {
            ElevatedOutcome.Done => InfoBarSeverity.Success,
            ElevatedOutcome.Cancelled or ElevatedOutcome.Unknown or ElevatedOutcome.Busy => InfoBarSeverity.Informational,
            ElevatedOutcome.NotFound or ElevatedOutcome.NotTrusted => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Error,
        };
        ActionBar.Message = Loc.Get("Unattended_Action_" + outcome);
        ActionBar.IsOpen = true;
    }

    private void ShowLan(string? errorCode)
    {
        if (errorCode is null) { LanBar.IsOpen = false; return; }
        LanBar.Severity = InfoBarSeverity.Error;
        LanBar.Message = Loc.Get(errorCode switch
        {
            PipeErrors.NotAllowed => "Unattended_Err_NotAllowed",
            PipeErrors.ListenFailed => "Unattended_Err_ListenFailed",
            PipeErrors.NotListening => "Unattended_Err_NotListening",
            "unreachable" => "Unattended_Err_Unreachable",
            _ => "Unattended_Err_Internal",
        });
        LanBar.IsOpen = true;
    }

    // ---- actions ----

    /// <summary>Handlers are async void by necessity; nothing that goes wrong inside may escape and end the app.</summary>
    private async void Guarded(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            App.LogCrash(ex);
            LanBar.Severity = InfoBarSeverity.Error;
            LanBar.Message = Loc.Get("Unattended_Err_Internal");
            LanBar.IsOpen = true;
            _busy = false;
            Busy.IsActive = false;
            Render();
        }
    }

    private async Task RunElevatedAsync(ElevatedAction action)
    {
        if (_busy) return;
        _busy = true;
        Busy.IsActive = true;
        ActionBar.IsOpen = false;
        Render();
        ElevatedOutcome outcome;
        try { outcome = await ElevatedHost.RunAsync(action); }
        finally
        {
            _busy = false;
            Busy.IsActive = false;
        }
        ShowAction(outcome);
        await _controller.RefreshAsync();
        Render();
    }

    private void OnInstall(object sender, RoutedEventArgs e) => Guarded(() => RunElevatedAsync(ElevatedAction.InstallService));
    private void OnStart(object sender, RoutedEventArgs e) => Guarded(() => RunElevatedAsync(ElevatedAction.StartService));
    private void OnStop(object sender, RoutedEventArgs e) => Guarded(() => RunElevatedAsync(ElevatedAction.StopService));

    private void OnUninstall(object sender, RoutedEventArgs e) => Guarded(async () =>
    {
        if (!await App.Services.ConfirmAsync(Loc.Get("Unattended_Uninstall_Title"), Loc.Get("Unattended_Uninstall_Body"), Loc.Get("Unattended_Uninstall_Accept"), Loc.Get("Unattended_Uninstall_Cancel"))) return;
        await RunElevatedAsync(ElevatedAction.UninstallService);
    });

    private void OnLanAllowedToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var on = LanAllowedSwitch.IsOn;
        Guarded(() => RunElevatedAsync(on ? ElevatedAction.AllowLan : ElevatedAction.DisallowLan));
    }

    private void OnAskToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var on = AskSwitch.IsOn;
        Guarded(() => RunElevatedAsync(on ? ElevatedAction.AskOn : ElevatedAction.AskOff));
    }

    private void OnAwakeToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var on = AwakeSwitch.IsOn;
        Guarded(() => RunElevatedAsync(on ? ElevatedAction.KeepAwakeOn : ElevatedAction.KeepAwakeOff));
    }

    private void OnSasToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var on = SasSwitch.IsOn;
        Guarded(() => RunElevatedAsync(on ? ElevatedAction.SasOn : ElevatedAction.SasOff));
    }

    private void OnLanShareToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var on = LanShareSwitch.IsOn;
        Guarded(async () =>
        {
            if (on)
            {
                // Two listeners on the same ports would confuse the other PC's search, so sharing from the app alone stops while the service shares.
                if (App.Share.IsSharing) await App.Share.StopAsync();
                ShowLan(await _controller.StartListeningAsync());
            }
            else ShowLan(await _controller.StopListeningAsync());
            Render();
        });
    }

    private void OnNewToken(object sender, RoutedEventArgs e) => Guarded(async () => ShowLan(await _controller.NewTokenAsync()));

    private void OnDisconnect(object sender, RoutedEventArgs e) => Guarded(async () => ShowLan(await _controller.EndSessionAsync()));

    private void OnCopyToken(object sender, RoutedEventArgs e)
    {
        if (_controller.View.Status?.Token is not { } token) return;
        var data = new DataPackage();
        data.SetText(token);
        Clipboard.SetContent(data);
        CopyButton.Content = Loc.Get("Unattended_Copied");
        _copiedUntil = DateTimeOffset.UtcNow.AddSeconds(2);
    }
}
