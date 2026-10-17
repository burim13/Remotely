using System.Diagnostics;
using Remotely.Core.Admin;

namespace Remotely.Server;

/// <summary>Counters since start. Local only: read through the admin API, never served on the public port.</summary>
public sealed class ServerMetrics(TimeProvider time)
{
    private readonly DateTimeOffset _started = time.GetUtcNow();
    private long _connectionsAccepted, _connectionsRateLimited, _pairingsSucceeded, _pairingsFailed, _pairingsRateLimited;
    private long _sessionsOpened, _sessionsRefused, _framesRelayed, _bytesRelayed, _adminUnauthorized;

    public DateTimeOffset StartedUtc => _started;

    public void ConnectionAccepted() => Interlocked.Increment(ref _connectionsAccepted);
    public void ConnectionRateLimited() => Interlocked.Increment(ref _connectionsRateLimited);
    public void PairingSucceeded() => Interlocked.Increment(ref _pairingsSucceeded);
    public void PairingFailed() => Interlocked.Increment(ref _pairingsFailed);
    public void PairingRateLimited() => Interlocked.Increment(ref _pairingsRateLimited);
    public void SessionOpened() => Interlocked.Increment(ref _sessionsOpened);
    public void SessionRefused() => Interlocked.Increment(ref _sessionsRefused);
    public void AdminUnauthorized() => Interlocked.Increment(ref _adminUnauthorized);

    public void FrameRelayed(int bytes)
    {
        Interlocked.Increment(ref _framesRelayed);
        Interlocked.Add(ref _bytesRelayed, bytes);
    }

    public ServerMetricsSnapshot Snapshot(int devicesTotal, int devicesOnline, int sessionsActive)
    {
        using var process = Process.GetCurrentProcess();
        return new ServerMetricsSnapshot(
            (time.GetUtcNow() - _started).TotalSeconds, devicesTotal, devicesOnline, sessionsActive,
            Interlocked.Read(ref _connectionsAccepted), Interlocked.Read(ref _connectionsRateLimited),
            Interlocked.Read(ref _pairingsSucceeded), Interlocked.Read(ref _pairingsFailed), Interlocked.Read(ref _pairingsRateLimited),
            Interlocked.Read(ref _sessionsOpened), Interlocked.Read(ref _sessionsRefused),
            Interlocked.Read(ref _framesRelayed), Interlocked.Read(ref _bytesRelayed), Interlocked.Read(ref _adminUnauthorized),
            process.WorkingSet64, GC.GetTotalMemory(false), process.Threads.Count);
    }
}
