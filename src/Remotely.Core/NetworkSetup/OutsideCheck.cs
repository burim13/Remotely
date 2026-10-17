using System.Net;

namespace Remotely.Core.NetworkSetup;

/// <summary>A check page prepared on the server. <see cref="Path"/> is what comes after the address, for example <c>/check/7QK2</c>.</summary>
public sealed record OutsideCheckTicket(string Id, string Path);

/// <summary>The server saw a request for the check page, from <see cref="Source"/>.</summary>
public sealed record OutsideCheckHit(IPAddress Source, DateTimeOffset TimeUtc);

/// <summary>
/// The Manager's side of the "test from outside" step. The server (agent A10, see the integrator requests) serves the check page on the public port and
/// remembers who asked for it; no third-party service is involved. Until the server provides it, <see cref="UnavailableOutsideCheck"/> is used.
/// </summary>
public interface IOutsideCheck
{
    Task<OutsideCheckTicket> StartAsync(CancellationToken ct = default);

    /// <summary>The first request the server received for the ticket's page, or null if none yet.</summary>
    Task<OutsideCheckHit?> GetHitAsync(string ticketId, CancellationToken ct = default);
}

public sealed class UnavailableOutsideCheck : IOutsideCheck
{
    public Task<OutsideCheckTicket> StartAsync(CancellationToken ct = default) => throw new NotSupportedException("This server does not offer a check page yet.");
    public Task<OutsideCheckHit?> GetHitAsync(string ticketId, CancellationToken ct = default) => throw new NotSupportedException();
}

public enum OutsideVerdict
{
    /// <summary>A request arrived from an address outside every private range: the port is reachable from the internet.</summary>
    ReachableFromOutside,
    /// <summary>A request arrived, but from this network (the phone was probably still on Wi-Fi). Says nothing about the outside.</summary>
    SeenFromInside,
    /// <summary>Nothing arrived in time.</summary>
    NothingArrived,
    /// <summary>A request arrived from an address that is not clearly inside or outside (100.64.0.0/10).</summary>
    Unclear,
}

public static class OutsideCheckEvaluator
{
    public static OutsideVerdict Evaluate(OutsideCheckHit? hit) => hit is null ? OutsideVerdict.NothingArrived : AddressClassifier.Classify(hit.Source) switch
    {
        AddressKind.Public => OutsideVerdict.ReachableFromOutside,
        AddressKind.Private or AddressKind.Loopback or AddressKind.LinkLocal => OutsideVerdict.SeenFromInside,
        _ => OutsideVerdict.Unclear,
    };

    /// <summary>
    /// Waits for the phone's request. A request from inside the network does not end the wait early: the person may still switch the phone to mobile data and try again,
    /// so only an outside or unclear request, cancellation, or the time limit ends it.
    /// </summary>
    public static async Task<(OutsideVerdict Verdict, OutsideCheckHit? Hit)> WaitAsync(IOutsideCheck check, OutsideCheckTicket ticket, TimeSpan timeout, TimeSpan pollInterval, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        OutsideCheckHit? inside = null;
        while (true)
        {
            var hit = await check.GetHitAsync(ticket.Id, ct).ConfigureAwait(false);
            var verdict = Evaluate(hit);
            if (verdict is OutsideVerdict.ReachableFromOutside or OutsideVerdict.Unclear) return (verdict, hit);
            if (verdict == OutsideVerdict.SeenFromInside) inside = hit;
            if (DateTime.UtcNow >= deadline) return inside is null ? (OutsideVerdict.NothingArrived, null) : (OutsideVerdict.SeenFromInside, inside);
            await Task.Delay(pollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The address to show: https://name:port/check/XXXX, or no port part when the port is 443.</summary>
    public static string BuildUrl(string host, int port, OutsideCheckTicket ticket)
    {
        var h = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        return $"https://{h}{(port == 443 ? "" : ":" + port)}{ticket.Path}";
    }
}
