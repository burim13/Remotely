using System.Security.Cryptography;
using System.Text;

namespace Remotely.Core.Protocol;

/// <summary>
/// Everything a new device needs to join your server, as one string you can paste: the server address, the server key pin and a pairing code.
/// Layout (before base64url): version byte, 32-byte pin (all zero when TLS is off), 8 code characters, the address (UTF-8), then the first
/// 3 bytes of SHA-256 over everything before it, which catches a mistyped or truncated invite. The invite is not a secret that outlives its code:
/// the code inside it expires and can be limited to a few uses.
/// </summary>
public sealed record Invite(string ServerUri, string PinHex, string Code)
{
    public const string Prefix = "RMT1-";
    private const byte Version = 1;
    private const int PinBytes = 32;
    private const int CodeChars = 8;
    private const int ChecksumBytes = 3;

    public string Encode()
    {
        var code = Normalize(Code);
        if (code.Length != CodeChars) throw new ArgumentException("The pairing code must have 8 characters.", nameof(Code));
        var pin = string.IsNullOrEmpty(PinHex) ? new byte[PinBytes] : Convert.FromHexString(PinHex);
        if (pin.Length != PinBytes) throw new ArgumentException("The key pin must be 64 hex characters.", nameof(PinHex));
        var uri = Encoding.UTF8.GetBytes(ServerUri);
        var body = new byte[1 + PinBytes + CodeChars + uri.Length];
        body[0] = Version;
        pin.CopyTo(body, 1);
        Encoding.ASCII.GetBytes(code).CopyTo(body, 1 + PinBytes);
        uri.CopyTo(body, 1 + PinBytes + CodeChars);
        var all = new byte[body.Length + ChecksumBytes];
        body.CopyTo(all, 0);
        SHA256.HashData(body).AsSpan(0, ChecksumBytes).CopyTo(all.AsSpan(body.Length));
        return Prefix + Convert.ToBase64String(all).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads an invite the user pasted. Whitespace and line breaks (from copying out of an e-mail) are ignored.</summary>
    public static bool TryParse(string? text, out Invite? invite, out string error)
    {
        invite = null;
        var compact = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length == 0) { error = "Paste the invite from your server."; return false; }
        if (!compact.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) { error = "That is not a Remotely invite. It should start with RMT1-."; return false; }
        byte[] all;
        try
        {
            var b64 = compact[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            all = Convert.FromBase64String(b64);
        }
        catch (FormatException) { error = "That invite is not complete. Copy all of it."; return false; }
        if (all.Length < 1 + PinBytes + CodeChars + 1 + ChecksumBytes) { error = "That invite is not complete. Copy all of it."; return false; }
        var body = all.AsSpan(0, all.Length - ChecksumBytes);
        if (!SHA256.HashData(body).AsSpan(0, ChecksumBytes).SequenceEqual(all.AsSpan(all.Length - ChecksumBytes)))
        { error = "That invite has a typing mistake or is cut short. Copy it again."; return false; }
        if (body[0] != Version) { error = "This invite was made by a newer Remotely. Update this app."; return false; }
        var pin = body.Slice(1, PinBytes);
        var code = Encoding.ASCII.GetString(body.Slice(1 + PinBytes, CodeChars));
        string uri;
        try { uri = new UTF8Encoding(false, true).GetString(body[(1 + PinBytes + CodeChars)..]); }
        catch (ArgumentException) { error = "That invite is damaged."; return false; }
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("wss" or "ws")) { error = "That invite is damaged."; return false; }
        invite = new Invite(uri, pin.IndexOfAnyExcept((byte)0) < 0 ? "" : Convert.ToHexStringLower(pin), $"{code[..4]}-{code[4..]}");
        error = "";
        return true;
    }

    public static string Normalize(string code) => new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
