using Microsoft.UI.Dispatching;
using Remotely.App.Views.Share;
using Remotely.Core.Crypto;
using Remotely.Core.Lan;
using Remotely.Core.Lan.Consent;
using Remotely.Host.Helper;
using Remotely.HostCore;
using Remotely.UI.Localization;

namespace Remotely.App;

/// <summary>
/// "Share this PC" on the local network: runs the token listener and, for the PC that is let in, captures this PC's screen and injects its input.
/// The app runs in the signed-in user's session, so the lock screen and Ctrl+Alt+Del are not available in this mode.
/// Who may connect is decided by <see cref="Coordinator"/> (asking the person, remembered PCs); what the person sees is drawn by <see cref="ShareUiHost"/>
/// once <see cref="AttachUi"/> was called. Without a screen to ask on, an unknown PC is refused, except under <c>--selftest</c> (a headless check run by CI and installers).
/// </summary>
public sealed class ShareService : IAsyncDisposable
{
    private readonly DesktopThread _desktop = new();
    private readonly string _dataDirectory;
    private DeviceIdentity? _identity;
    private LanHost? _host;
    private ShareUiHost? _ui;
    private DesktopCapturer? _capturer;
    private static int _instances;

    public ShareService() : this(null) { }

    /// <param name="dataDirectory">Where remembered PCs, history and share settings are kept. Null uses the app's data folder.</param>
    public ShareService(string? dataDirectory)
    {
        _dataDirectory = dataDirectory ?? ViewerSettings.Dir;
        Coordinator = new ShareCoordinator(_dataDirectory, PromptAsync);
        Interlocked.Increment(ref _instances);
        // The tray menu and other shell parts listen to this to refresh their share state.
        Coordinator.State.Changed += _ => { try { App.Services.RaiseShareStateChanged(); } catch (Exception ex) when (ex is not OutOfMemoryException) { App.LogCrash(ex); } };
    }

    public ShareCoordinator Coordinator { get; }
    public ShareStateMachine State => Coordinator.State;
    public LanHost? Host => _host;
    public bool IsSharing => _host is not null;
    public ShareUiHost? Ui => _ui;

    /// <summary>Diagnostics only: how many ShareService objects this process has made (the self-test makes more than one).</summary>
    public static int InstancesCreated => Volatile.Read(ref _instances);

    /// <summary>Diagnostics only: which capture object this service uses and whether it is the current one.</summary>
    public string CaptureSummary => _capturer is null ? "none" : $"{nameof(DesktopCapturer)}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_capturer)} on desktop thread #{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_desktop)} (service #{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)})";

    /// <summary>Replaces the question shown to the person (self-tests answer it without a screen). Null means the normal question window.</summary>
    public Func<ConsentRequest, CancellationToken, Task<ConsentDecision>>? PromptOverride { get; set; }

    /// <summary>Shows a Windows notification. Defaults to <see cref="ShareToasts.Show"/>; the shell can replace it with its own toast service.</summary>
    public Action<string, string> ToastSink { get; set; } = ShareToasts.Show;

    /// <summary>This PC's long-term device key (the one the other PC saves when it is remembered). Created on first use.</summary>
    public DeviceIdentity Identity => _identity ??= LoadIdentity();

    private static DeviceIdentity LoadIdentity()
    {
        try { return ViewerSettings.LoadOrCreateIdentity(); }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        {
            App.LogCrash(ex);
            return DeviceIdentity.Generate();   // the key file is unreadable: remembered PCs will not recognise this PC after a restart
        }
    }

    /// <summary>Lets the share screens, the banner, the question window and toasts work. Call once from the UI thread (the Share card does).</summary>
    public void AttachUi(DispatcherQueue ui)
    {
        _ui ??= new ShareUiHost(this, ui);
    }

    private static bool IsHeadlessSelfTest => Environment.GetCommandLineArgs().Contains("--selftest");

    private Task<ConsentDecision> PromptAsync(ConsentRequest request, CancellationToken ct)
    {
        if (PromptOverride is { } over) return over(request, ct);
        if (IsHeadlessSelfTest) return Task.FromResult(ConsentDecision.AllowOnce);   // nobody to ask
        if (_ui is not null) return _ui.AskAsync(request, ct);
        return Task.FromResult(ConsentDecision.Deny);
    }

    /// <summary>Starts sharing. Returns an error sentence for the person, or null when sharing.</summary>
    public string? Start(Action<string> log)
    {
        if (_host is not null) return null;
        // Started from a screen (the UI thread): make sure the question window, banner and toasts exist, so an unknown PC can be asked about.
        if (_ui is null && !IsHeadlessSelfTest && DispatcherQueue.GetForCurrentThread() is { } queue) AttachUi(queue);
        var received = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Remotely");
        _capturer?.Dispose();
        _capturer = new DesktopCapturer(_desktop, log);
        log($"Capture: {CaptureSummary}.");
        var services = new HostServices(_capturer)
        {
            Input = new InputInjector(_desktop),
            Clipboard = new ClipboardAccess(),
            ReceiveDirectory = received,
            IsTrusted = _ => true,           // the token (and the consent answer) already decided who is connecting
            CanControl = _ => true,
        };
        var agent = new HostAgent(Identity, services, log);
        var host = new LanHost(new LanHostOptions { Identity = Identity, Consent = Coordinator.Gate },
            (channel, name, ct) => agent.ServeChannelAsync(channel, name, control: true, ct), log);
        try { host.Start(); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _ = host.DisposeAsync();
            var message = string.Format(Loc.Get("Share_Error_Listen"), ex.Message);
            Coordinator.State.Fail(message);
            return message;
        }
        _host = host;
        Coordinator.Attach(host);
        return null;
    }

    public async Task StopAsync()
    {
        var host = _host;
        _host = null;
        if (host is null) return;
        Coordinator.Detach();
        await host.DisposeAsync();
        _capturer?.Dispose();   // releases screen-duplication resources so a later service in this process starts clean
        _capturer = null;
    }

    /// <summary>The banner's "Stop sharing": disconnects the other PC and turns sharing off.</summary>
    public async Task StopSharingAsync()
    {
        Coordinator.EndSession();
        await StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _ui?.Dispose();
        _desktop.Dispose();
        _identity?.Dispose();
    }
}
