using System.Xml.Linq;

namespace Remotely.UITests;

/// <summary>The app's own string resources, so the flow tests look for the words the app really shows (and a renamed string breaks the test loudly instead of silently).</summary>
internal sealed class Strings
{
    private readonly Dictionary<string, string> _values;

    private Strings(Dictionary<string, string> values) => _values = values;

    public static Strings Load(string repoRoot, string language, string project = "Remotely.App")
    {
        var path = Path.Combine(repoRoot, "src", project, "Strings", language, "Resources.resw");
        var doc = XDocument.Load(path);
        return new Strings(doc.Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? ""));
    }

    public string Get(string key) => _values.TryGetValue(key, out var v) ? v : throw new KeyNotFoundException($"string resource '{key}' does not exist");

    public string Format(string key, params object[] args) => string.Format(Get(key), args);

    /// <summary>A regular expression that matches the text of a format string such as "Step {0} of {1}".</summary>
    public System.Text.RegularExpressions.Regex Pattern(string key) =>
        new("^" + System.Text.RegularExpressions.Regex.Escape(Get(key)).Replace(@"\{0}", ".+").Replace(@"\{1}", ".+").Replace(@"\{2}", ".+") + "$");

    public static string FindRepoRoot(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remotely.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Remotely.slnx was not found above " + (start ?? AppContext.BaseDirectory) + "; pass --repo");
    }
}
