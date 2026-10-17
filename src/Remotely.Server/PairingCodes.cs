using System.Security.Cryptography;

namespace Remotely.Server;

/// <summary>An active pairing code as the admin API lists it. <see cref="Id"/> is what revoke takes; <see cref="Code"/> is XXXX-XXXX.</summary>
public sealed record IssuedCode(string Id, string Code, DateTimeOffset ExpiresUtc, int UsesAllowed, int UsesLeft);

/// <summary>Pairing codes (in memory; one use and 15 minutes by default, or a few uses for an invite) plus a per-address lockout against guessing.</summary>
public sealed class PairingCodes(TimeProvider time)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);
    public const int MaxUses = 20;
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(15);

    private sealed class Entry(string id, string code, DateTimeOffset expires, int allowed)
    {
        public string Id { get; } = id;
        public string Code { get; } = code;
        public DateTimeOffset Expires { get; } = expires;
        public int Allowed { get; } = allowed;
        public int UsesLeft { get; set; } = allowed;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _codes = [];
    private readonly Dictionary<string, (int Failures, DateTimeOffset Until)> _failures = [];

    public (string Code, DateTimeOffset ExpiresUtc) Create(int uses = 1, TimeSpan? lifetime = null)
    {
        var issued = Issue(uses, lifetime);
        return (issued.Code, issued.ExpiresUtc);
    }

    public IssuedCode Issue(int uses = 1, TimeSpan? lifetime = null)
    {
        uses = Math.Clamp(uses, 1, MaxUses);
        var life = lifetime is { } l && l > TimeSpan.Zero ? (l > MaxLifetime ? MaxLifetime : l) : Lifetime;
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var code = new string(chars);
        var entry = new Entry(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)), code, time.GetUtcNow() + life, uses);
        lock (_gate)
        {
            Purge();
            _codes[code] = entry;
        }
        return Describe(entry);
    }

    /// <summary>Codes that can still be used.</summary>
    public IReadOnlyList<IssuedCode> Active()
    {
        lock (_gate)
        {
            Purge();
            return [.. _codes.Values.OrderBy(e => e.Expires).Select(Describe)];
        }
    }

    public bool Revoke(string id)
    {
        lock (_gate)
        {
            var match = _codes.Values.FirstOrDefault(e => e.Id == id);
            return match is not null && _codes.Remove(match.Code.ToUpperInvariant());
        }
    }

    private static IssuedCode Describe(Entry e) => new(e.Id, $"{e.Code[..4]}-{e.Code[4..]}", e.Expires, e.Allowed, e.UsesLeft);

    public bool IsLockedOut(string remote)
    {
        lock (_gate)
        {
            return _failures.TryGetValue(remote, out var f) && f.Failures >= MaxFailures && f.Until > time.GetUtcNow();
        }
    }

    /// <summary>True when a valid code exists without consuming it. Lets the caller check other limits before a code is spent.</summary>
    public bool Exists(string code)
    {
        var normalized = Normalize(code);
        lock (_gate)
        {
            Purge();
            return _codes.ContainsKey(normalized);
        }
    }

    /// <summary>Consumes the code if valid. A wrong code counts against <paramref name="remote"/>.</summary>
    public bool TryConsume(string remote, string code)
    {
        var normalized = Normalize(code);
        lock (_gate)
        {
            Purge();
            if (_failures.TryGetValue(remote, out var f) && f.Failures >= MaxFailures && f.Until > time.GetUtcNow())
                return false;
            if (_codes.TryGetValue(normalized, out var entry))
            {
                if (--entry.UsesLeft <= 0) _codes.Remove(normalized);
                _failures.Remove(remote);
                return true;
            }
            var count = _failures.TryGetValue(remote, out var prior) && prior.Until > time.GetUtcNow() ? prior.Failures + 1 : 1;
            _failures[remote] = (count, time.GetUtcNow() + LockoutTime);
            return false;
        }
    }

    private static string Normalize(string code) => new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private void Purge()
    {
        var now = time.GetUtcNow();
        foreach (var k in _codes.Where(c => c.Value.Expires <= now).Select(c => c.Key).ToList()) _codes.Remove(k);
        foreach (var k in _failures.Where(c => c.Value.Until <= now).Select(c => c.Key).ToList()) _failures.Remove(k);
    }
}
