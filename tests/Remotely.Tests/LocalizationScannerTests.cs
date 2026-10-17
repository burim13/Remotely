using Xunit.Abstractions;

namespace Remotely.Tests;

/// <summary>
/// No user-visible text in the XAML or the code: it all comes from Strings/*/Resources.resw. The few justified literals (debug Gallery page, technical examples)
/// are listed with a reason in tools/loc/allow-literals.txt; an entry that no longer matches anything fails the test, so the list cannot rot.
/// </summary>
public class LocalizationScannerTests(ITestOutputHelper output)
{
    private const string AllowFile = "tools/loc/allow-literals.txt";

    private static List<LiteralFinding> ScanEverything()
    {
        var all = new List<LiteralFinding>();
        foreach (var xaml in LocRepo.SourceFiles(".xaml").Where(f => !LocRepo.Relative(f).Contains("/Themes/"))) all.AddRange(LocalizationScanner.ScanXaml(xaml));
        foreach (var code in LocRepo.SourceFiles(".cs")) all.AddRange(LocalizationScanner.ScanCode(code));
        return all;
    }

    [Fact]
    public void Every_user_visible_literal_is_a_resource_key_or_on_the_allow_list()
    {
        var allow = LocRepo.ReadAllowList(AllowFile);
        var unlisted = new List<LiteralFinding>();
        foreach (var finding in ScanEverything())
        {
            var entry = allow.FirstOrDefault(a => a.Matches(finding.File, finding.Text));
            if (entry is null) unlisted.Add(finding); else entry.Used++;
        }
        foreach (var f in unlisted) output.WriteLine(f.ToString());
        Assert.True(unlisted.Count == 0, "Hard-coded user-visible text (move it to Resources.resw, or justify it in " + AllowFile + "):\n" + string.Join("\n", unlisted));
    }

    [Fact]
    public void The_allow_list_has_no_stale_entries()
    {
        var allow = LocRepo.ReadAllowList(AllowFile);
        var findings = ScanEverything();
        var stale = allow.Where(a => !findings.Any(f => a.Matches(f.File, f.Text))).Select(a => $"{AllowFile}:{a.Line}  {a.Path} | {a.Text}").ToList();
        Assert.True(stale.Count == 0, "Allow-list entries that match no finding (delete them):\n" + string.Join("\n", stale));
    }

    // ---- the scanner itself, on small inputs ----

    private static List<LiteralFinding> CodeOf(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), "scan-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(path, source);
        try { return LocalizationScanner.ScanCode(path); }
        finally { File.Delete(path); }
    }

    private static List<LiteralFinding> XamlOf(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), "scan-" + Guid.NewGuid().ToString("N") + ".xaml");
        File.WriteAllText(path, source);
        try { return LocalizationScanner.ScanXaml(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Scanner_finds_literals_in_xaml_attributes_and_text_nodes()
    {
        var found = XamlOf("""
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel>
                <TextBlock Text="Hello there" />
                <Button Content="Save" ToolTipService.ToolTip="Saves it" AutomationProperties.Name="Save button" />
                <TextBox Header="Name" PlaceholderText="Type here" />
                <Button>Go</Button>
              </StackPanel>
            </Page>
            """);
        var texts = found.Select(f => f.Text).OrderBy(t => t).ToList();
        Assert.Equal(["Go", "Hello there", "Name", "Save", "Save button", "Saves it", "Type here"], texts);
    }

    [Fact]
    public void Scanner_ignores_bindings_resource_keys_and_non_text()
    {
        var found = XamlOf("""
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel>
                <TextBlock Text="{x:Bind Title}" />
                <TextBlock Text="12:30" />
                <Button Content="Setup_Copy" />
                <TextBlock Text="" />
                <TextBlock Text="–" />
              </StackPanel>
            </Page>
            """);
        Assert.Empty(found);
    }

    [Fact]
    public void Scanner_finds_literals_in_code_property_assignments_and_sink_calls()
    {
        var found = CodeOf("""
            class P {
              void M() {
                var a = new TextBlock { Text = "Connect", FontWeight = x };
                b.Content = "Save " + count;
                c.Text = $"{theme} theme";
                SetName(button, "Close the window");
                Toast("Title here", Loc.Get("Body_Key"));
                Show("Connecting…");
              }
            }
            """);
        Assert.Equal([" theme", "Close the window", "Connect", "Connecting…", "Save ", "Title here"], found.Select(f => f.Text).OrderBy(t => t, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Scanner_skips_lookups_comments_and_text_free_assignments()
    {
        var found = CodeOf("""
            class P {
              // Text = "In a comment"
              /* Content = "In a block comment" */
              void M() {
                a.Text = Loc.Get("Nav_Home");
                b.Text = string.Format(Loc.Get("Onboarding_StepOf"), 1, 2);
                c.Text = "";
                d.Text = "–";
                e.Text = $"{x}/{y}";
                f.Content = text;
                SetName(g, Ui.T("Invite_Copy"));
                var s = new Style("SubtitleTextBlockStyle");
                Ui.Text("", "SubtitleTextBlockStyle");
              }
            }
            """);
        Assert.Empty(found);
    }
}
