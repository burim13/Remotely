using Xunit.Abstractions;

namespace Remotely.Tests;

/// <summary>
/// Static accessibility checks over the XAML and code of both apps and the shared UI project. What they prove is limited to what the source shows; what Narrator really
/// announces is checked at run time by the app's --uia-scan option (Shell/UiaScan.cs). Justified exceptions are in tools/loc/allow-a11y.txt.
/// </summary>
public class AccessibilityTests(ITestOutputHelper output)
{
    private const string AllowFile = "tools/loc/allow-a11y.txt";

    private static List<A11yFinding> All()
    {
        var all = new List<A11yFinding>();
        foreach (var xaml in LocRepo.SourceFiles(".xaml").Where(f => !LocRepo.Relative(f).Contains("/Themes/"))) all.AddRange(AccessibilityAnalyzer.AnalyzeXaml(xaml));
        foreach (var code in LocRepo.SourceFiles(".cs")) all.AddRange(AccessibilityAnalyzer.AnalyzeCode(code));
        return all;
    }

    private static List<A11yFinding> Unlisted(IEnumerable<A11yFinding> findings, string rule)
    {
        var allow = LocRepo.ReadAllowList(AllowFile);
        var result = new List<A11yFinding>();
        foreach (var f in findings.Where(f => f.Rule == rule))
            if (!allow.Any(a => a.Matches(f.File, f.Text))) result.Add(f);
        return result;
    }

    private void Expect(string rule, string advice)
    {
        var bad = Unlisted(All(), rule);
        foreach (var f in bad) output.WriteLine(f.ToString());
        Assert.True(bad.Count == 0, $"{advice} (or justify it in {AllowFile}):\n" + string.Join("\n", bad));
    }

    [Fact] public void Every_interactive_control_has_an_accessible_name() => Expect("unnamed", "Give the control AutomationProperties.Name, visible text, a Header or LabeledBy");
    [Fact] public void No_control_removes_its_focus_visuals_or_leaves_the_tab_order() => Expect("focus", "Focus must stay visible and reachable");
    [Fact] public void Tab_order_follows_the_layout_no_positive_TabIndex() => Expect("tabindex", "Reorder the XAML instead of using TabIndex");
    [Fact] public void Nothing_is_mouse_only() => Expect("mouse-only", "Use a Button or give the element focus and a key handler");
    [Fact] public void Colours_come_from_theme_resources_so_high_contrast_applies() => Expect("colour", "Use a ThemeResource brush");
    [Fact] public void Text_can_scale_to_200_percent() => Expect("textscale", "Let the text size its container");
    [Fact] public void Animations_go_through_Motion_so_reduced_motion_is_respected() => Expect("motion", "Use Remotely.UI.Motion");

    [Fact]
    public void The_allow_list_has_no_stale_entries()
    {
        var findings = All();
        var stale = LocRepo.ReadAllowList(AllowFile).Where(a => !findings.Any(f => a.Matches(f.File, f.Text))).Select(a => $"{AllowFile}:{a.Line} {a.Path} | {a.Text}").ToList();
        Assert.True(stale.Count == 0, "Entries that match nothing (delete them):\n" + string.Join("\n", stale));
    }

    [Theory]
    [InlineData("src/Remotely.App/MainWindow.xaml")]
    [InlineData("src/Remotely.ServerManager/MainWindow.xaml")]
    public void The_main_window_declares_navigation_and_main_landmarks(string file)
    {
        var text = File.ReadAllText(Path.Combine(LocRepo.Root, file));
        Assert.Contains("AutomationProperties.LandmarkType=\"Navigation\"", text);
        Assert.Contains("AutomationProperties.LandmarkType=\"Main\"", text);
    }

    [Fact]
    public void The_theme_defines_high_contrast_resources_and_Motion_checks_the_windows_setting()
    {
        var themes = File.ReadAllText(Path.Combine(LocRepo.Src, "Remotely.UI", "Themes", "Portico.xaml")) + File.ReadAllText(Path.Combine(LocRepo.Src, "Remotely.UI", "Themes", "Tokens.xaml"));
        Assert.Contains("HighContrast", themes);
        Assert.Contains("SystemParametersInfo", File.ReadAllText(Path.Combine(LocRepo.Src, "Remotely.UI", "Motion.cs")));
    }

    [Fact]
    public void The_uia_scan_option_takes_a_result_file_and_an_optional_page_list()
    {
        var both = Remotely.App.Shell.AppOptions.Parse(["--uia-scan", "out.txt", "pages.txt"]);
        Assert.Equal("out.txt", both.UiaScanResultPath);
        Assert.Equal("pages.txt", both.UiaScanPagesPath);
        Assert.False(both.UsesSingleInstance);
        var one = Remotely.App.Shell.AppOptions.Parse(["--uia-scan", "out.txt", "--minimized"]);
        Assert.Equal("out.txt", one.UiaScanResultPath);
        Assert.Null(one.UiaScanPagesPath);
        Assert.True(one.Minimized);
        Assert.Empty(one.Problems);
        Assert.Single(Remotely.App.Shell.AppOptions.Parse(["--uia-scan"]).Problems);
    }

    // ---- the analyser itself ----

    private static List<A11yFinding> Xaml(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), "a11y-" + Guid.NewGuid().ToString("N") + ".xaml");
        File.WriteAllText(path, $"<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">{body}</Page>");
        try { return AccessibilityAnalyzer.AnalyzeXaml(path); } finally { File.Delete(path); }
    }

    [Fact]
    public void Analyser_flags_an_icon_only_button_and_accepts_named_ones()
    {
        var found = Xaml("""
            <StackPanel>
              <Button x:Name="IconOnly"><SymbolIcon Symbol="Refresh" /></Button>
              <Button x:Name="Named" AutomationProperties.Name="Refresh"><SymbolIcon Symbol="Refresh" /></Button>
              <Button Content="Save" />
              <Button><TextBlock Text="Go" /></Button>
              <TextBox Header="Name" />
              <TextBox x:Name="Bare" PlaceholderText="Type" />
              <ListView SelectionMode="None" />
              <ListView x:Name="Pick" ItemClick="OnPick" IsItemClickEnabled="True" />
            </StackPanel>
            """);
        Assert.Equal(["Button x:Name=IconOnly", "ListView x:Name=Pick", "TextBox x:Name=Bare"], found.Where(f => f.Rule == "unnamed").Select(f => f.Text).Order().ToList());
    }

    [Fact]
    public void Analyser_flags_focus_tabindex_colour_mouse_only_and_motion_problems()
    {
        var found = Xaml("""
            <StackPanel>
              <Button Content="A" UseSystemFocusVisuals="False" />
              <Button Content="B" IsTabStop="False" />
              <Button Content="C" TabIndex="3" />
              <Border Tapped="OnTap" />
              <TextBlock Text="x" Foreground="#FF0000" />
              <TextBlock Text="y" Foreground="{ThemeResource Portico.Text}" IsTextScaleFactorEnabled="False" />
              <Storyboard />
            </StackPanel>
            """);
        Assert.Equal(["colour", "focus", "focus", "motion", "mouse-only", "tabindex", "textscale"], found.Select(f => f.Rule).Order().ToList());
    }

    [Fact]
    public void Code_analyser_flags_a_control_built_without_a_name()
    {
        var path = Path.Combine(Path.GetTempPath(), "a11y-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(path, """
            class P { void M() {
              var a = new Button { Content = text };
              var b = new Button { Tag = 1 };
              var c = new TextBox { MinWidth = 3 };
              AutomationProperties.SetName(c, "x");
              b.Tapped += OnTap;
            } }
            """);
        try
        {
            var found = AccessibilityAnalyzer.AnalyzeCode(path);
            Assert.Equal(1, found.Count(f => f.Rule == "unnamed" && f.Text.StartsWith("new Button")));
            Assert.Equal(1, found.Count(f => f.Rule == "unnamed"));
            Assert.Equal(1, found.Count(f => f.Rule == "mouse-only"));
        }
        finally { File.Delete(path); }
    }
}
