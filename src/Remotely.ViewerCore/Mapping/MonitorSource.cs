namespace Remotely.ViewerCore.Mapping;

/// <summary>One screen of the host. Index -1 with empty bounds is the "all monitors" entry (the whole captured picture).</summary>
public sealed record MonitorEntry(int Index, string Name, PixelRect Bounds, bool IsPrimary = false)
{
    public static MonitorEntry All { get; } = new(-1, "", default);
    public bool IsAll => Index < 0;
}

/// <summary>
/// Seam between the session window and the host's monitor list. The list and per-monitor capture belong to the protocol work (agent A6); that
/// work implements this interface over the monitor list in the host's hello message. Until then <see cref="AllMonitorsSource"/> is used.
/// </summary>
public interface IMonitorSource
{
    IReadOnlyList<MonitorEntry> Monitors { get; }
    MonitorEntry Selected { get; }
    /// <summary>False when the host cannot switch monitors (then only <see cref="Selected"/> is shown).</summary>
    bool CanSelect { get; }
    /// <summary>Raised (on any thread) when the list or the selection changed.</summary>
    event Action? Changed;
    Task SelectAsync(MonitorEntry monitor, CancellationToken ct = default);
}

/// <summary>The only entry is "all monitors": what every host offers today.</summary>
public sealed class AllMonitorsSource : IMonitorSource
{
    private static readonly MonitorEntry[] Only = [MonitorEntry.All];
    public IReadOnlyList<MonitorEntry> Monitors => Only;
    public MonitorEntry Selected => MonitorEntry.All;
    public bool CanSelect => false;
    public event Action? Changed { add { } remove { } }
    public Task SelectAsync(MonitorEntry monitor, CancellationToken ct = default) => Task.CompletedTask;
}
