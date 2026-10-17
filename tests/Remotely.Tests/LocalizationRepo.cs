using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Remotely.Tests;

/// <summary>Where the localisation and accessibility tests find things. Everything is plain file and XML reading, so the tests run on Linux, where the WinUI apps cannot be built.</summary>
internal static class LocRepo
{
    public static readonly string[] Apps = ["Remotely.App", "Remotely.ServerManager"];

    /// <summary>Languages that every app must carry besides the English default. Albanian is the second language (docs/agent-specs.md); qps-ploc is the generated pseudo-locale.</summary>
    public const string English = "en-US", Albanian = "sq-AL", Pseudo = "qps-ploc";

    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remotely.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Remotely.slnx not found above " + AppContext.BaseDirectory);
        }
    }

    public static string Src => Path.Combine(Root, "src");

    public static string ResourcePath(string app, string language) => Path.Combine(Src, app, "Strings", language, "Resources.resw");

    /// <summary>The key to value table of one resource file, in file order.</summary>
    public static List<KeyValuePair<string, string>> Load(string app, string language) =>
        XDocument.Load(ResourcePath(app, language)).Root!.Elements("data")
            .Select(e => new KeyValuePair<string, string>(e.Attribute("name")!.Value, e.Element("value")?.Value ?? ""))
            .ToList();

    public static Dictionary<string, string> Table(string app, string language) => Load(app, language).ToDictionary(p => p.Key, p => p.Value);

    /// <summary>The placeholder indexes of a format string ({0}, {1:N0}), sorted, so two translations can be compared however they order the words.</summary>
    public static string Placeholders(string value) =>
        string.Join(",", Regex.Matches(value.Replace("{{", "").Replace("}}", ""), @"\{(\d+)[^{}]*\}").Select(m => int.Parse(m.Groups[1].Value)).OrderBy(i => i));

    public static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    /// <summary>Source files of the app projects and the shared UI project (no obj/bin output).</summary>
    public static IEnumerable<string> SourceFiles(string extension, params string[] projects)
    {
        foreach (var project in projects.Length == 0 ? [.. Apps, "Remotely.UI"] : projects)
        {
            var dir = Path.Combine(Src, project);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*" + extension, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var rel = Relative(file);
                if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
                yield return file;
            }
        }
    }

    /// <summary>Lines of an allow-list file: <c>path | text | reason</c>, # comments and blank lines ignored.</summary>
    public static List<AllowEntry> ReadAllowList(string relativePath)
    {
        var list = new List<AllowEntry>();
        var lines = File.ReadAllLines(Path.Combine(Root, relativePath));
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(" | ", 3);
            if (parts.Length != 3 || parts.Any(p => p.Trim().Length == 0))
                throw new InvalidDataException($"{relativePath}:{i + 1}: expected 'path | text | reason' (all three non-empty), got: {line}");
            list.Add(new AllowEntry(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), i + 1));
        }
        return list;
    }
}

internal sealed record AllowEntry(string Path, string Text, string Reason, int Line)
{
    public int Used { get; set; }

    /// <summary>A finding matches when the file is the same and the text is the same (or the entry says <c>*</c>, the whole file).</summary>
    public bool Matches(string path, string text) => Path == path && (Text == "*" || Text == text);
}
