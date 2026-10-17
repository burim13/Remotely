namespace Remotely.Core.Storage;

/// <summary>The settings search box: every word typed must appear somewhere in the setting's label or help text (any case, any position).</summary>
public static class SettingsSearch
{
    public static bool Matches(string query, params string[] texts)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        var haystack = string.Join(' ', texts);
        return words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }
}
