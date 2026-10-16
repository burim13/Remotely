namespace Remotely.UI.Controls;

/// <summary>The text rules behind <see cref="TokenBox"/>: no WinUI types, so they are unit-tested on any machine.</summary>
public static class TokenFormat
{
    /// <summary>Upper-cases and keeps only the characters a token can contain. With an empty <paramref name="alphabet"/> every letter and digit is kept. Case, dashes, spaces and anything else typed or pasted are ignored.</summary>
    public static string Normalize(string? text, string alphabet = "")
    {
        if (string.IsNullOrEmpty(text)) return "";
        var chars = new List<char>(text.Length);
        foreach (var raw in text)
        {
            if (!char.IsLetterOrDigit(raw)) continue;
            var c = char.ToUpperInvariant(raw);
            if (alphabet.Length == 0 || alphabet.Contains(c)) chars.Add(c);
        }
        return new string(chars.ToArray());
    }

    /// <summary>Cuts a normalised value into <paramref name="groupCount"/> groups; groups past the end of the value are empty.</summary>
    public static string[] Split(string normalized, int groupLength, int groupCount)
    {
        var groups = new string[groupCount];
        for (var i = 0; i < groupCount; i++)
        {
            var start = i * groupLength;
            groups[i] = start >= normalized.Length ? "" : normalized.Substring(start, Math.Min(groupLength, normalized.Length - start));
        }
        return groups;
    }

    /// <summary>Joins the filled groups with <paramref name="separator"/>, like <c>K7P4-4NX2-QMH9</c>. Trailing empty groups are left out.</summary>
    public static string Display(IReadOnlyList<string> groups, string separator = "-")
    {
        var last = groups.Count - 1;
        while (last >= 0 && groups[last].Length == 0) last--;
        return string.Join(separator, groups.Take(last + 1));
    }

    /// <summary>
    /// Applies new text for one group. The group keeps what fits; the rest flows into the following groups (so pasting a whole token into the first box fills every box).
    /// Returns the new groups and the index of the group that should have the caret: the next one after a full group, otherwise the same one.
    /// </summary>
    public static (string[] Groups, int Focus) Apply(IReadOnlyList<string> current, int index, string? typed, int groupLength, string alphabet = "")
    {
        var groups = current.ToArray();
        var text = Normalize(typed, alphabet);
        if (text.Length <= groupLength)
        {
            groups[index] = text;
            return (groups, text.Length == groupLength && index < groups.Length - 1 ? index + 1 : index);
        }
        var remaining = text;
        var focus = index;
        for (var i = index; remaining.Length > 0 && i < groups.Length; i++)
        {
            var take = Math.Min(groupLength, remaining.Length);
            groups[i] = remaining[..take];
            remaining = remaining[take..];
            focus = take == groupLength && i < groups.Length - 1 ? i + 1 : i;
        }
        return (groups, focus);
    }
}
