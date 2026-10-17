using System.Net;

namespace Remotely.Server;

public enum RateResult
{
    Allowed,
    Denied,

    /// <summary>Denied, and this is the first denial since the key last had room: the moment to log it once.</summary>
    DeniedFirst,
}

/// <summary>Token bucket per key: <c>perMinute</c> attempts in a burst, refilled evenly over a minute. 0 or less means no limit.</summary>
public sealed class RateLimiter(int perMinute, TimeProvider time)
{
    private const int PurgeAbove = 4096;
    private const int HardCap = 50_000;

    private sealed class Bucket(double tokens, long stamp)
    {
        public double Tokens = tokens;
        public long Stamp = stamp;
        public bool Warned;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = [];
    private long _lastPurge;

    public int PerMinute => perMinute;

    public RateResult Hit(string key)
    {
        if (perMinute <= 0) return RateResult.Allowed;
        lock (_gate)
        {
            var now = time.GetTimestamp();
            if (!_buckets.TryGetValue(key, out var b))
            {
                if (_buckets.Count >= PurgeAbove) Purge(now);   // at most every few seconds, so a flood of new sources cannot make every attempt scan the table
                b = new Bucket(perMinute, now);
                _buckets[key] = b;
            }
            else
            {
                b.Tokens = Math.Min(perMinute, b.Tokens + time.GetElapsedTime(b.Stamp, now).TotalSeconds * perMinute / 60.0);
                b.Stamp = now;
            }
            if (b.Tokens >= 1)
            {
                b.Tokens -= 1;
                b.Warned = false;
                return RateResult.Allowed;
            }
            var first = !b.Warned;
            b.Warned = true;
            return first ? RateResult.DeniedFirst : RateResult.Denied;
        }
    }

    private void Purge(long now)
    {
        if (_lastPurge != 0 && time.GetElapsedTime(_lastPurge, now) < TimeSpan.FromSeconds(10) && _buckets.Count < HardCap) return;
        _lastPurge = now;
        // A bucket idle for two minutes is full again, so forgetting it changes nothing. If an attacker still fills the table, start over rather than grow without bound.
        foreach (var k in _buckets.Where(p => time.GetElapsedTime(p.Value.Stamp, now) > TimeSpan.FromMinutes(2)).Select(p => p.Key).ToList())
            _buckets.Remove(k);
        if (_buckets.Count >= HardCap) _buckets.Clear();
    }
}

/// <summary>The limiters the server applies, one per kind of attempt. Limits come from <see cref="LimitOptions"/>.</summary>
public sealed class RateLimits(LimitOptions options, TimeProvider time)
{
    /// <summary>New device connections per source address.</summary>
    public RateLimiter Connect { get; } = new(options.ConnectAttemptsPerMinute, time);

    /// <summary>Pairing attempts (right or wrong) per source address.</summary>
    public RateLimiter Pair { get; } = new(options.PairAttemptsPerMinute, time);

    /// <summary>Requests to open a session, per device.</summary>
    public RateLimiter Session { get; } = new(options.SessionRequestsPerMinute, time);

    /// <summary>Refused admin calls: used to keep the audit log from filling with them, never to refuse a valid token.</summary>
    public RateLimiter AdminFailures { get; } = new(10, time);

    /// <summary>The key for a source: IPv4-mapped IPv6 becomes IPv4, and an IPv6 address counts as its /64 (a household or host gets a whole /64, so one address per attempt would defeat the limit).</summary>
    public static string SourceKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();
        return Convert.ToHexStringLower(address.GetAddressBytes().AsSpan(0, 8)) + "::/64";
    }
}
