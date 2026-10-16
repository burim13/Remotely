namespace Remotely.Core.Lan;

/// <summary>What to tell a person while they type a token: how far they are, and which typed characters can never be part of a token (0, 1, I, O).</summary>
public sealed record TokenFeedback(bool IsComplete, bool IsValid, int Length, string Message, IReadOnlyList<int> BadPositions)
{
    public const int Expected = LanToken.IdLength + LanToken.SecretLength;
}

public static class TokenHints
{
    /// <summary>Position numbers in <see cref="TokenFeedback.BadPositions"/> count letters and digits only, from 1, ignoring dashes and spaces.</summary>
    public static TokenFeedback Check(string? typed)
    {
        var compact = new string((typed ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var bad = new List<int>();
        for (var i = 0; i < compact.Length; i++)
            if (!LanToken.Alphabet.Contains(compact[i])) bad.Add(i + 1);

        if (bad.Count > 0)
        {
            var parts = bad.Select(position => Describe(position, compact[position - 1]));
            return new TokenFeedback(false, false, compact.Length, string.Join(" ", parts), bad);
        }
        if (compact.Length == 0) return new TokenFeedback(false, true, 0, "", bad);
        if (compact.Length < TokenFeedback.Expected)
            return new TokenFeedback(false, true, compact.Length, $"{compact.Length} of {TokenFeedback.Expected} characters.", bad);

        // Full length (or longer) with only allowed characters: LanToken decides, and its message is shown as it is.
        return LanToken.TryParse(typed, out _, out var error)
            ? new TokenFeedback(true, true, compact.Length, "", bad)
            : new TokenFeedback(false, false, compact.Length, error, bad);
    }

    /// <summary>
    /// The nearby PC whose label the typed text starts with, so typing the rest of the token can connect to it without a search. Null when nothing matches
    /// or when two PCs show the same label (then the normal search decides).
    /// </summary>
    public static LanDiscovery.Nearby? MatchNearby(string? typed, IEnumerable<LanDiscovery.Nearby> nearby)
    {
        var compact = new string((typed ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (compact.Length < LanToken.IdLength) return null;
        var matches = nearby.Where(n => n.Id == compact[..LanToken.IdLength]).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Describe(int position, char c) => c switch
    {
        '0' => $"Character {position} is the number 0. Tokens never contain 0 or the letter O; look at that character again.",
        'O' => $"Character {position} is the letter O. Tokens never contain O or the number 0; look at that character again.",
        '1' => $"Character {position} is the number 1. Tokens never contain 1 or the letter I; look at that character again.",
        'I' => $"Character {position} is the letter I. Tokens never contain I or the number 1; look at that character again.",
        _ => $"Character {position} ({c}) cannot be part of a token.",
    };
}
