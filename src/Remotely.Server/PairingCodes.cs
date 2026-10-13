using System.Security.Cryptography;

namespace Remotely.Server;

/// <summary>Pairing codes (in memory; one use and 15 minutes by default, or a few uses for an invite) plus a per-address lockout against guessing.</summary>
public sealed class PairingCodes(TimeProvider time)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Expires, int UsesLeft)> _codes = [];
    private readonly Dictionary<string, (int Failures, DateTimeOffset Until)> _failures = [];

    public (string Code, DateTimeOffset ExpiresUtc) Create(int uses = 1, TimeSpan? lifetime = null)
    {
        uses = Math.Clamp(uses, 1, 20);
        var life = lifetime is { } l && l > TimeSpan.Zero ? (l > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : l) : Lifetime;
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var code = new string(chars);
        var expires = time.GetUtcNow() + life;
        lock (_gate)
        {
            Purge();
            _codes[code] = (expires, uses);
        }
        return ($"{code[..4]}-{code[4..]}", expires);
    }

    public bool IsLockedOut(string remote)
    {
        lock (_gate)
        {
            return _failures.TryGetValue(remote, out var f) && f.Failures >= MaxFailures && f.Until > time.GetUtcNow();
        }
    }

    /// <summary>Consumes the code if valid. A wrong code counts against <paramref name="remote"/>.</summary>
    public bool TryConsume(string remote, string code)
    {
        var normalized = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        lock (_gate)
        {
            Purge();
            if (_failures.TryGetValue(remote, out var f) && f.Failures >= MaxFailures && f.Until > time.GetUtcNow())
                return false;
            if (_codes.TryGetValue(normalized, out var entry))
            {
                if (entry.UsesLeft <= 1) _codes.Remove(normalized); else _codes[normalized] = (entry.Expires, entry.UsesLeft - 1);
                _failures.Remove(remote);
                return true;
            }
            var count = _failures.TryGetValue(remote, out var prior) && prior.Until > time.GetUtcNow() ? prior.Failures + 1 : 1;
            _failures[remote] = (count, time.GetUtcNow() + LockoutTime);
            return false;
        }
    }

    private void Purge()
    {
        var now = time.GetUtcNow();
        foreach (var k in _codes.Where(c => c.Value.Expires <= now).Select(c => c.Key).ToList()) _codes.Remove(k);
        foreach (var k in _failures.Where(c => c.Value.Until <= now).Select(c => c.Key).ToList()) _failures.Remove(k);
    }
}
