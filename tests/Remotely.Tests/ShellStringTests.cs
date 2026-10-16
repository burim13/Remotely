using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Remotely.Tests;

public class ShellStringTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remotely.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static Dictionary<string, string> Values(string language)
    {
        var path = Path.Combine(RepoRoot(), "src", "Remotely.App", "Strings", language, "Resources.resw");
        return XDocument.Load(path).Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
    }

    [Fact]
    public void English_and_Albanian_have_the_same_keys()
    {
        var en = Values("en-US").Keys.ToHashSet();
        var sq = Values("sq-AL").Keys.ToHashSet();
        Assert.Empty(en.Except(sq));
        Assert.Empty(sq.Except(en));
    }

    [Fact]
    public void Every_string_key_the_shell_and_onboarding_code_asks_for_exists()
    {
        var root = Path.Combine(RepoRoot(), "src", "Remotely.App");
        var files = new[] { "MainWindow.xaml.cs", "App.xaml.cs" }.Select(f => Path.Combine(root, f))
            .Concat(Directory.GetFiles(Path.Combine(root, "Shell"), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(Path.Combine(root, "Services"), "*.cs"))
            .Concat(Directory.GetFiles(Path.Combine(root, "Views", "Onboarding"), "*.cs"));
        var used = files
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "(?:Loc\\.Get|Text)\\(\"([A-Za-z_]+)\"\\)|Loc\\.Get\\([^\"\\n]*\"([A-Za-z_]+)\"|NavEntry\\([^\"]*\"[a-z]+\", \"([A-Za-z_]+)\"")
                .Select(m => m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value))
            .ToHashSet();
        Assert.Contains("Tray_Open", used);
        Assert.Contains("Onboarding_Finish", used);
        Assert.Contains("Nav_Home", used);
        Assert.Empty(used.Except(Values("en-US").Keys));
    }

    [Fact]
    public void Format_strings_keep_their_placeholders_in_both_languages()
    {
        foreach (var language in new[] { "en-US", "sq-AL" })
        {
            var values = Values(language);
            Assert.Contains("{0}", values["Onboarding_StepOf"]);
            Assert.Contains("{1}", values["Onboarding_StepOf"]);
            foreach (var key in new[] { "Onboarding_Server_Connected", "Onboarding_Done_Name", "Onboarding_Done_Server" })
                Assert.Contains("{0}", values[key]);
        }
    }
}
