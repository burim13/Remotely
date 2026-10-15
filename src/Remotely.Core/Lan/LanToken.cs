using System.Security.Cryptography;
using System.Text;

namespace Remotely.Core.Lan;

/// <summary>
/// The token a PC shows so another PC on the same network can connect: 12 characters, written like <c>K7P4-4NX2-QMH9</c>.
/// The first 4 are the PC's public label (the other PC uses them to find it on the network); the last 8 are the secret, never sent on the
/// network. A token works for one connection and expires.
/// </summary>
public sealed record LanToken(string Id, string Secret)
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0, 1, I or O: they are too easy to mistake
    public const int IdLength = 4;
    public const int SecretLength = 8;
    private const int Iterations = 300_000;

    public string Display => $"{Id}-{Secret[..4]}-{Secret[4..]}";

    public static LanToken Create() => new(Random(IdLength), Random(SecretLength));

    private static string Random(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Reads what a person typed: case, dashes and spaces do not matter.</summary>
    public static bool TryParse(string? text, out LanToken? token, out string error)
    {
        token = null;
        var compact = new string((text ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (compact.Length == 0) { error = "Type the token shown on the other PC."; return false; }
        if (compact.Length != IdLength + SecretLength) { error = "A token has 12 letters and numbers, like K7P4-4NX2-QMH9."; return false; }
        if (compact.Any(c => !Alphabet.Contains(c))) { error = "A token never contains 0, 1, I or O. Check what you typed."; return false; }
        token = new LanToken(compact[..IdLength], compact[IdLength..]);
        error = "";
        return true;
    }

    /// <summary>Stretches the secret so that guessing it offline costs real time per guess. <paramref name="salt"/> is chosen by the PC that made the token.</summary>
    public byte[] DeriveKey(ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(Secret), salt, Iterations, HashAlgorithmName.SHA256, 32);
}

/// <summary>A token on the PC that shows it, with its salt, the stretched key, its lifetime and how many wrong tries it has had.</summary>
public sealed class LanTokenState
{
    public LanTokenState(LanToken token, DateTimeOffset expiresUtc)
    {
        Token = token;
        ExpiresUtc = expiresUtc;
        Salt = RandomNumberGenerator.GetBytes(16);
        Key = token.DeriveKey(Salt);
    }

    public LanToken Token { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public byte[] Salt { get; }
    public byte[] Key { get; }
    private int _failedAttempts;
    /// <summary>Counts one more wrong try and returns the total.</summary>
    public int AddFailure() => Interlocked.Increment(ref _failedAttempts);
}
