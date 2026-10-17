namespace Remotely.Core.Update;

public enum UpdatePhase
{
    /// <summary>Update checks are switched off (the default).</summary>
    Off,
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,

    /// <summary>Downloaded and verified. The person decides whether to run it.</summary>
    Ready,

    /// <summary>A newer release exists, but it cannot be checked against a published SHA-256, so it is not offered.</summary>
    Unverifiable,
    NoPackage,
    Failed,
}

public sealed record UpdateState(UpdatePhase Phase, UpdateOffer? Offer = null, VerifiedPackage? Package = null, string? Detail = null, double Progress = 0, DateTimeOffset? LastCheckUtc = null);

/// <summary>
/// The update flow without any screen: opt-in switch, throttled automatic check, manual check, download and verify, skip this version. Both apps bind their
/// "Updates" section and banner to <see cref="State"/>. It never runs an installer; the app does that on the person's click, after
/// <see cref="VerifiedPackage.StillMatchesAsync"/>.
/// </summary>
public sealed class UpdateController(UpdateChecker checker, UpdateDownloader downloader, UpdateSettings settings, string settingsPath, ReleaseVersion current,
    UpdateProduct product, string architecture, string downloadDirectory, TimeProvider? clock = null)
{
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private UpdateState _state = new(settings.Enabled ? UpdatePhase.Idle : UpdatePhase.Off, LastCheckUtc: settings.LastCheckUtc);

    public UpdateState State => _state;
    public bool Enabled => settings.Enabled;
    public ReleaseVersion Current => current;

    /// <summary>Raised on whatever thread made the change; the UI marshals to its own thread.</summary>
    public event Action? Changed;

    public void SetEnabled(bool enabled)
    {
        settings.Enabled = enabled;
        settings.Save(settingsPath);
        if (!enabled) Set(new UpdateState(UpdatePhase.Off, LastCheckUtc: settings.LastCheckUtc));
        else if (_state.Phase == UpdatePhase.Off) Set(new UpdateState(UpdatePhase.Idle, LastCheckUtc: settings.LastCheckUtc));
    }

    /// <summary>For app start-up: does nothing unless checks are switched on and the last check is older than <see cref="AutoCheckInterval"/>.</summary>
    public Task CheckIfDueAsync(CancellationToken ct = default)
    {
        if (!settings.Enabled) return Task.CompletedTask;
        if (settings.LastCheckUtc is { } last && _clock.GetUtcNow() - last < AutoCheckInterval) return Task.CompletedTask;
        return CheckCoreAsync(honourSkip: true, ct);
    }

    /// <summary>The person pressed "Check now". Works even while automatic checks are off: it is their explicit request.</summary>
    public Task CheckNowAsync(CancellationToken ct = default) => CheckCoreAsync(honourSkip: false, ct);

    private async Task CheckCoreAsync(bool honourSkip, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct).ConfigureAwait(false)) return;   // a check or download is already running
        try
        {
            Set(_state with { Phase = UpdatePhase.Checking, Detail = null });
            var result = await checker.CheckAsync(current, product, architecture, ct).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            if (result.Status != UpdateCheckStatus.Failed)
            {
                settings.LastCheckUtc = now;
                settings.Save(settingsPath);
            }
            Set(result.Status switch
            {
                UpdateCheckStatus.UpToDate => new UpdateState(UpdatePhase.UpToDate, LastCheckUtc: now),
                UpdateCheckStatus.Available when honourSkip && result.Offer!.Version.ToString() == settings.SkippedVersion =>
                    new UpdateState(UpdatePhase.UpToDate, LastCheckUtc: now),
                UpdateCheckStatus.Available => new UpdateState(UpdatePhase.Available, result.Offer, LastCheckUtc: now),
                UpdateCheckStatus.Unverifiable => new UpdateState(UpdatePhase.Unverifiable, Detail: result.Detail, LastCheckUtc: now),
                UpdateCheckStatus.NoPackage => new UpdateState(UpdatePhase.NoPackage, Detail: result.Detail, LastCheckUtc: now),
                _ => new UpdateState(UpdatePhase.Failed, Detail: result.Detail, LastCheckUtc: settings.LastCheckUtc),
            });
        }
        catch (OperationCanceledException)
        {
            Set(new UpdateState(settings.Enabled ? UpdatePhase.Idle : UpdatePhase.Off, LastCheckUtc: settings.LastCheckUtc));
        }
        finally { _busy.Release(); }
    }

    public async Task DownloadAsync(CancellationToken ct = default)
    {
        if (_state.Phase != UpdatePhase.Available || _state.Offer is not { } offer) return;
        if (!await _busy.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            Set(_state with { Phase = UpdatePhase.Downloading, Progress = 0, Detail = null });
            var progress = new DirectProgress(p => SetProgress(p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0));
            var package = await downloader.DownloadAsync(offer, downloadDirectory, progress, ct).ConfigureAwait(false);
            Set(new UpdateState(UpdatePhase.Ready, offer, package, LastCheckUtc: settings.LastCheckUtc, Progress: 1));
        }
        catch (OperationCanceledException)
        {
            Set(new UpdateState(UpdatePhase.Available, offer, LastCheckUtc: settings.LastCheckUtc));
        }
        catch (Exception ex) when (ex is UpdateVerificationException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Set(new UpdateState(UpdatePhase.Failed, offer, Detail: ex.Message, LastCheckUtc: settings.LastCheckUtc));
        }
        finally { _busy.Release(); }
    }

    /// <summary>Do not offer this version again (a newer one is still offered).</summary>
    public void SkipThisVersion()
    {
        if (_state.Offer is not { } offer) return;
        settings.SkippedVersion = offer.Version.ToString();
        settings.Save(settingsPath);
        Set(new UpdateState(settings.Enabled ? UpdatePhase.Idle : UpdatePhase.Off, LastCheckUtc: settings.LastCheckUtc));
    }

    private readonly object _gate = new();

    private void Set(UpdateState state)
    {
        lock (_gate) _state = state;
        Changed?.Invoke();
    }

    // Progress arrives while the download runs; a late report must never overwrite the state that followed it.
    private void SetProgress(double value)
    {
        lock (_gate)
        {
            if (_state.Phase != UpdatePhase.Downloading) return;
            _state = _state with { Progress = value };
        }
        Changed?.Invoke();
    }

    private sealed class DirectProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
