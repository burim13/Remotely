using System.Text.RegularExpressions;

namespace Remotely.Core.Diagnostics;

/// <summary>
/// Removes secrets from text before it is written to a log or put into a diagnostics file. It works by shape and by registration, so it is a safety net and not a proof:
/// <list type="bullet">
/// <item>invites (<c>RMT1-...</c>), same-network tokens (<c>K7P4-4NX2-QMH9</c>, any case), bearer tokens, and long key-like runs of 32 or more letters, digits, <c>-</c>, <c>_</c>, <c>+</c>, <c>/</c> that contain a letter and a digit (keys, hashes, admin tokens; GUIDs are kept);</item>
/// <item>the value after words such as token, secret, password, pin, private key, admin token, invite, authorization and "pairing code" (after <c>=</c>, <c>:</c> or "is");</item>
/// <item>any exact value registered with <see cref="AddSecret"/> (the current token, pairing code or admin token the program knows), in the plain form and with dashes and spaces removed.</item>
/// </list>
/// A secret that has none of these shapes and was never registered (for example a bare 8-character pairing code in the middle of a sentence) is not recognised.
/// </summary>
public sealed partial class Redactor
{
    public const string Mask = "[redacted]";

    private readonly object _gate = new();
    private readonly List<string> _secrets = [];

    /// <summary>The shared instance every logger and the diagnostics export use unless told otherwise.</summary>
    public static Redactor Shared { get; } = new();

    /// <summary>Registers a value that must never be written. Values shorter than 4 characters are ignored (they would damage ordinary text).</summary>
    public void AddSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        lock (_gate)
        {
            foreach (var form in Forms(value))
                if (form.Length >= 4 && !_secrets.Contains(form, StringComparer.Ordinal)) _secrets.Add(form);
            _secrets.Sort((a, b) => b.Length.CompareTo(a.Length));
        }
    }

    public void Clear()
    {
        lock (_gate) _secrets.Clear();
    }

    private static IEnumerable<string> Forms(string value)
    {
        var trimmed = value.Trim();
        yield return trimmed;
        var compact = new string(trimmed.Where(c => c is not ('-' or ' ')).ToArray());
        if (compact.Length >= 4) yield return compact;
    }

    public string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = text;
        string[] secrets;
        lock (_gate) secrets = [.. _secrets];
        foreach (var secret in secrets)
            result = result.Replace(secret, Mask, StringComparison.OrdinalIgnoreCase);

        result = InvitePattern().Replace(result, "RMT1-" + Mask);
        result = TokenPattern().Replace(result, m => IsShortDeviceId(m.Value) ? m.Value : Mask);
        result = BearerPattern().Replace(result, m => m.Groups[1].Value + Mask);
        result = PairingCodePattern().Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        result = NamedValuePattern().Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        result = LongRunPattern().Replace(result, m => IsKeyLike(m.Value) ? Mask : m.Value);
        return result;
    }

    /// <summary>
    /// <c>DeviceIds.Short</c> writes a device id as 12 lowercase hex characters in groups of four, the same shape as a token. It is public and logs rely on it, so an
    /// all-lowercase-hex group of that shape is kept. The price: a token typed in lowercase whose 12 characters all happen to be hex digits (about 1 in 20000) is not
    /// recognised by shape; it is still removed when it was registered with <see cref="AddSecret"/>.
    /// </summary>
    private static bool IsShortDeviceId(string text) => text.All(c => c == '-' || char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static bool IsKeyLike(string run)
    {
        if (run.Length < 32) return false;
        if (GuidPattern().IsMatch(run)) return false;
        return run.Any(char.IsDigit) && run.Any(char.IsLetter);
    }

    [GeneratedRegex(@"RMT1-[A-Za-z0-9_\-+/=]+", RegexOptions.IgnoreCase)]
    private static partial Regex InvitePattern();

    // The alphabet has no 0, 1, I or O (LanToken.Alphabet). Dashes only: a space-separated spelling is covered by AddSecret.
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-HJ-NP-Za-hj-np-z2-9]{4}-[A-HJ-NP-Za-hj-np-z2-9]{4}-[A-HJ-NP-Za-hj-np-z2-9]{4}(?![A-Za-z0-9])")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"(\bBearer\s+)[A-Za-z0-9_\-+/=.~]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(\b(?:pair(?:ing)?[ _\-]?code|invite[ _\-]?code))(\s*(?:is|was|=|:)?\s*[""']?)[A-Za-z0-9\-]{4,}", RegexOptions.IgnoreCase)]
    private static partial Regex PairingCodePattern();

    [GeneratedRegex(@"(\b(?:admin[ _\-]?token|access[ _\-]?token|token|secret|password|passwd|pin|private[ _\-]?key|api[ _\-]?key|key|invite|authorization|credential)s?)([""']?\s*(?:=|:)\s*[""']?)(?!\[redacted\])[^\s""',;&}\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex NamedValuePattern();

    [GeneratedRegex(@"[A-Za-z0-9_+/=\-]{32,}")]
    private static partial Regex LongRunPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$")]
    private static partial Regex GuidPattern();
}
