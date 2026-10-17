using System.Text.Json;
using System.Text.RegularExpressions;

namespace Remotely.Core.Update;

/// <summary>Reads the GitHub releases API (<c>GET /repos/{owner}/{repo}/releases</c>) and the <c>SHA256SUMS.txt</c> file that a release publishes. Pure text in, records out.</summary>
public static partial class ReleaseFeed
{
    private const int MaxNotesChars = 4000;

    /// <summary>Accepts the array the list endpoint returns or a single release object. Drafts and entries without a readable tag are left out.</summary>
    public static IReadOnlyList<ReleaseInfo> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var releases = new List<ReleaseInfo>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
                if (ParseOne(item) is { } r) releases.Add(r);
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object && ParseOne(doc.RootElement) is { } single) releases.Add(single);
        return releases;
    }

    private static ReleaseInfo? ParseOne(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (Bool(e, "draft")) return null;
        var tag = Str(e, "tag_name");
        if (!ReleaseVersion.TryParse(tag, out var version)) return null;
        var assets = new List<ReleaseAsset>();
        if (e.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var name = Str(a, "name");
                var url = Str(a, "browser_download_url");
                if (name.Length == 0 || url.Length == 0) continue;
                assets.Add(new ReleaseAsset(name, url, a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0));
            }
        }
        var notes = Str(e, "body");
        if (notes.Length > MaxNotesChars) notes = notes[..MaxNotesChars] + "…";
        return new ReleaseInfo(tag, version, Str(e, "name"), notes, Str(e, "html_url"), Bool(e, "prerelease") || version.IsPreRelease, assets);
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Parses <c>sha256sum</c> output: one line per file, 64 hex characters, white space, an optional <c>*</c> (binary marker) and the file name. Lines that do not fit are
    /// ignored; if a name appears twice with different hashes it is left out entirely (an ambiguous file is not trusted).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseSums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var m = SumLine().Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            var name = m.Groups[2].Value;
            var hash = m.Groups[1].Value.ToLowerInvariant();
            if (map.TryGetValue(name, out var existing) && existing != hash) conflicted.Add(name);
            map[name] = hash;
        }
        foreach (var name in conflicted) map.Remove(name);
        return map;
    }

    [GeneratedRegex(@"^([0-9a-fA-F]{64}) [ *]([^\\/\0]+)$")]
    private static partial Regex SumLine();
}
