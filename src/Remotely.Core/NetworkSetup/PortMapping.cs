using System.Net;

namespace Remotely.Core.NetworkSetup;

public sealed record PortMappingRequest(IPAddress InternalAddress, int InternalPort, int ExternalPort, string Description, TimeSpan Lease);

/// <summary>A mapping a router really accepted (the router's own answer, not what was asked for): what the UI lists, and what is removed again.</summary>
public sealed record PortMapping(string Mapper, string Protocol, int ExternalPort, int InternalPort, IPAddress InternalAddress, IPAddress? ExternalAddress,
    TimeSpan Lease, DateTimeOffset CreatedUtc, string Description);

public sealed record PortMappingAttempt(PortMapping? Mapping, string? ErrorKey, string? Detail)
{
    public bool Ok => Mapping is not null;
    public static PortMappingAttempt Success(PortMapping mapping) => new(mapping, null, null);
    public static PortMappingAttempt Failure(string errorKey, string? detail = null) => new(null, errorKey, detail);
}

/// <summary>A way of asking a router to forward a TCP port (UPnP IGD, NAT-PMP). Real routers cannot be reached from CI: implementations are tested against fakes only.</summary>
public interface IPortMapper
{
    string Name { get; }
    Task<PortMappingAttempt> CreateAsync(PortMappingRequest request, CancellationToken ct = default);
    Task<bool> RemoveAsync(PortMapping mapping, CancellationToken ct = default);
}

/// <summary>
/// Keeps track of what was asked of the router. Opt-in: <see cref="AutomaticMappingEnabled"/> is false until the user switches it on, and nothing is
/// sent to the router while it is false. The caller must call <see cref="RemoveAllAsync"/> when the server stops and when the Manager closes.
/// </summary>
public sealed class PortMappingSession(IEnumerable<IPortMapper> mappers, TimeProvider? time = null)
{
    private readonly List<IPortMapper> _mappers = mappers.ToList();
    private readonly List<PortMapping> _created = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool AutomaticMappingEnabled { get; set; }

    public IReadOnlyList<PortMapping> Mappings => _created.ToArray();

    public async Task<PortMappingAttempt> CreateAsync(PortMappingRequest request, CancellationToken ct = default)
    {
        if (!AutomaticMappingEnabled) return PortMappingAttempt.Failure("Setup_Upnp_Off");
        PortMappingAttempt last = PortMappingAttempt.Failure("Setup_Upnp_NoRouter");
        foreach (var mapper in _mappers)
        {
            var attempt = await mapper.CreateAsync(request, ct).ConfigureAwait(false);
            if (attempt.Ok)
            {
                _created.Add(attempt.Mapping! with { CreatedUtc = _time.GetUtcNow() });
                return attempt;
            }
            // Keep the most informative failure: one from a router that answered beats "no router found".
            if (attempt.ErrorKey != "Setup_Upnp_NoRouter" || last.ErrorKey == "Setup_Upnp_NoRouter") last = attempt;
        }
        return last;
    }

    /// <summary>Asks the same mapper again for each mapping older than half its lease, so a lease-limited mapping does not lapse while the server runs.</summary>
    public async Task RenewDueAsync(CancellationToken ct = default)
    {
        foreach (var old in _created.ToArray())
        {
            if (old.Lease == TimeSpan.Zero || _time.GetUtcNow() - old.CreatedUtc < old.Lease / 2) continue;
            var mapper = _mappers.FirstOrDefault(m => m.Name == old.Mapper);
            if (mapper is null) continue;
            var attempt = await mapper.CreateAsync(new PortMappingRequest(old.InternalAddress, old.InternalPort, old.ExternalPort, old.Description, old.Lease), ct).ConfigureAwait(false);
            if (attempt.Ok) _created[_created.IndexOf(old)] = attempt.Mapping! with { CreatedUtc = _time.GetUtcNow() };
        }
    }

    /// <summary>Removes every mapping this session created. Returns the ones the router would not remove.</summary>
    public async Task<IReadOnlyList<PortMapping>> RemoveAllAsync(CancellationToken ct = default)
    {
        var failed = new List<PortMapping>();
        foreach (var mapping in _created.ToArray())
        {
            var mapper = _mappers.FirstOrDefault(m => m.Name == mapping.Mapper);
            var removed = mapper is not null && await mapper.RemoveAsync(mapping, ct).ConfigureAwait(false);
            if (removed) _created.Remove(mapping); else failed.Add(mapping);
        }
        return failed;
    }

    /// <summary>The line shown to the user for a mapping.</summary>
    public static GuideStep Describe(PortMapping m) => new("Setup_Upnp_Created", m.Mapper, m.Protocol, m.ExternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        m.InternalAddress.ToString(), m.InternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture), m.ExternalAddress?.ToString() ?? "?");
}
