using Remotely.HostCore;

namespace Remotely.ViewerCore.Mapping;

/// <summary>
/// The monitor picker's data for one live session: "all monitors" plus the displays the host listed (hello extension, updated by the host's monitor
/// list message). Selecting one makes the host stream only that display (<see cref="ViewerSession.SelectMonitorAsync"/>). Dispose to unhook the session.
/// </summary>
public sealed class SessionMonitorSource : IMonitorSource, IDisposable
{
    private readonly ViewerSession _session;

    public SessionMonitorSource(ViewerSession session)
    {
        _session = session;
        session.MonitorsChanged += OnMonitorsChanged;
    }

    public bool CanSelect => _session.SupportsTiledFrames && _session.Monitors.Count > 1;

    public IReadOnlyList<MonitorEntry> Monitors => Build(_session.Monitors);

    public MonitorEntry Selected => Monitors.FirstOrDefault(m => m.Index == _session.SelectedMonitor) ?? MonitorEntry.All;

    /// <summary>The host's whole virtual desktop in pixels (empty when the host did not list its displays).</summary>
    public PixelRect VirtualDesktop => PixelRect.Union(_session.Monitors.Select(Bounds));

    public event Action? Changed;

    public async Task SelectAsync(MonitorEntry monitor, CancellationToken ct = default)
    {
        if (!CanSelect) return;
        await _session.SelectMonitorAsync(monitor.IsAll ? -1 : monitor.Index).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public void Dispose() => _session.MonitorsChanged -= OnMonitorsChanged;

    private void OnMonitorsChanged(IReadOnlyList<MonitorInfo> _) => Changed?.Invoke();

    private static PixelRect Bounds(MonitorInfo m) => new(m.X, m.Y, m.Width, m.Height);

    /// <summary>With one display, "all monitors" and that display are the same picture, so only "all monitors" is listed.</summary>
    public static IReadOnlyList<MonitorEntry> Build(IReadOnlyList<MonitorInfo> monitors) =>
        monitors.Count <= 1 ? [MonitorEntry.All] : [MonitorEntry.All, .. monitors.Select(m => new MonitorEntry(m.Index, m.Name, Bounds(m), m.Primary))];
}
