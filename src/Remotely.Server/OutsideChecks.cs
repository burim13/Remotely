using System.Net;
using System.Security.Cryptography;

namespace Remotely.Server;

public sealed record OutsideCheckInfo(string Id, string Path, DateTimeOffset ExpiresUtc);

public sealed record OutsideCheckResult(string? Source, DateTimeOffset? TimeUtc, string? Kind, DateTimeOffset ExpiresUtc);

/// <summary>
/// "Test from outside" without any third-party service: the administrator creates a ticket, opens /check/{id} on the public port from a phone on mobile data,
/// and the server remembers the address of the FIRST request. The public page says nothing else and answers 404 for unknown or expired tickets.
/// </summary>
public sealed class OutsideChecks(TimeProvider time)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int MaxActive = 20;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);

    private sealed class Ticket(DateTimeOffset expires)
    {
        public DateTimeOffset Expires = expires;
        public IPAddress? Source;
        public DateTimeOffset? Hit;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Ticket> _tickets = [];

    public OutsideCheckInfo Create(TimeSpan? lifetime)
    {
        var life = lifetime is { } l && l > TimeSpan.Zero ? (l > MaxLifetime ? MaxLifetime : l) : DefaultLifetime;
        var chars = new char[10];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var id = new string(chars);
        var expires = time.GetUtcNow() + life;
        lock (_gate)
        {
            Purge();
            if (_tickets.Count >= MaxActive) _tickets.Remove(_tickets.OrderBy(t => t.Value.Expires).First().Key);
            _tickets[id] = new Ticket(expires);
        }
        return new OutsideCheckInfo(id, "/check/" + id, expires);
    }

    /// <summary>Records the request if the ticket is live and has no hit yet. Returns whether the ticket exists.</summary>
    public bool Visit(string id, IPAddress? source)
    {
        lock (_gate)
        {
            Purge();
            if (!_tickets.TryGetValue(id.ToUpperInvariant(), out var t)) return false;
            if (t.Hit is null && source is not null)
            {
                t.Source = source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source;
                t.Hit = time.GetUtcNow();
            }
            return true;
        }
    }

    public OutsideCheckResult? Get(string id)
    {
        lock (_gate)
        {
            Purge();
            if (!_tickets.TryGetValue(id.ToUpperInvariant(), out var t)) return null;
            var kind = t.Source is null ? null : Remotely.Core.NetworkSetup.AddressClassifier.Classify(t.Source).ToString();
            return new OutsideCheckResult(t.Source?.ToString(), t.Hit, kind, t.Expires);
        }
    }

    private void Purge()
    {
        var now = time.GetUtcNow();
        foreach (var k in _tickets.Where(t => t.Value.Expires <= now).Select(t => t.Key).ToList()) _tickets.Remove(k);
    }
}
