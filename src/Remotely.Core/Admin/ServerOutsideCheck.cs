using System.Net;
using Remotely.Core.NetworkSetup;

namespace Remotely.Core.Admin;

/// <summary><see cref="IOutsideCheck"/> backed by the server's admin API: the server serves /check/{id} on its public port and remembers the first requester.</summary>
public sealed class ServerOutsideCheck(ServerAdminClient admin, TimeSpan? lifetime = null) : IOutsideCheck
{
    public async Task<OutsideCheckTicket> StartAsync(CancellationToken ct = default)
    {
        var t = await admin.StartOutsideCheckAsync(lifetime, ct).ConfigureAwait(false);
        return new OutsideCheckTicket(t.Id, t.Path);
    }

    public async Task<OutsideCheckHit?> GetHitAsync(string ticketId, CancellationToken ct = default)
    {
        var s = await admin.GetOutsideCheckAsync(ticketId, ct).ConfigureAwait(false);
        return s.Source is not null && s.TimeUtc is { } at && IPAddress.TryParse(s.Source, out var ip) ? new OutsideCheckHit(ip, at) : null;
    }
}
