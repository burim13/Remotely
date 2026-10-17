using System.Text.RegularExpressions;
using System.Xml.Linq;
using Remotely.Unattended;

namespace Remotely.Tests;

/// <summary>
/// The parts of the Unattended access page that can be checked without Windows (agent A13): the sentences, the strings behind them, the rules for asking for administrator rights, and
/// the files being wired up. Whether the page draws, and what it looks like, is not tested here: only the Windows build and a person can show that.
/// </summary>
public class UnattendedPageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remotely.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string PageFolder => Path.Combine(RepoRoot(), "src", "Remotely.App", "Views", "Unattended");

    private static Dictionary<string, string> Strings(string language) =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", "Remotely.App", "Strings", language, "Resources.resw")).Root!
            .Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    private static string[] Placeholders(string text) => [.. Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order()];

    // ---- strings ----

    private static HashSet<string> KeysUsedByThePage()
    {
        var keys = Directory.EnumerateFiles(PageFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"(Unattended_[A-Za-z0-9_]+)\"").Select(m => m.Groups[1].Value))
            .Where(k => !k.EndsWith('_'))   // a prefix the page completes from an enum name
            .ToHashSet();
        // keys the page builds from an enum name
        foreach (var name in Enum.GetNames<ServiceState>()) keys.Add("Unattended_State_" + name);
        foreach (var name in new[] { "Done", "Failed", "Cancelled", "NotFound", "NotTrusted", "Busy", "Unknown" }) keys.Add("Unattended_Action_" + name);
        foreach (var key in StatusLine.AllKeys) keys.Add(key);
        return keys;
    }

    [Fact]
    public void Every_string_the_page_asks_for_exists_in_English_and_Albanian_with_the_same_placeholders()
    {
        var en = Strings("en-US");
        var sq = Strings("sq-AL");
        var used = KeysUsedByThePage();
        Assert.True(used.Count > 60);
        Assert.Contains("Unattended_Nav", used);
        foreach (var key in used)
        {
            Assert.True(en.ContainsKey(key), "missing in en-US: " + key);
            Assert.True(sq.ContainsKey(key), "missing in sq-AL: " + key);
            Assert.Equal(Placeholders(en[key]), Placeholders(sq[key]));
            Assert.False(string.IsNullOrWhiteSpace(en[key]), key);
        }
    }

    [Fact]
    public void Every_Unattended_string_in_the_table_is_used_so_none_is_left_over()
    {
        var used = KeysUsedByThePage();
        var unused = Strings("en-US").Keys.Where(k => k.StartsWith("Unattended_") && !used.Contains(k)).ToList();
        Assert.Empty(unused);
    }

    [Fact]
    public void The_Albanian_block_is_marked_as_machine_written()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Remotely.App", "Strings", "sq-AL", "Resources.resw"));
        var at = text.IndexOf("A13: Unattended access page", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Contains("MACHINE-WRITTEN", text[at..(at + 300)]);
    }

    [Fact]
    public void The_page_draws_no_literal_text_all_of_it_comes_from_the_string_table()
    {
        var xaml = XDocument.Load(Path.Combine(PageFolder, "UnattendedPage.xaml"));   // also proves the file is well-formed XML
        var literal = xaml.Descendants().SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName is "Text" or "Content" or "Header" or "OnContent" or "OffContent" or "Title" or "Message" or "PlaceholderText").ToList();
        Assert.Empty(literal);
    }

    [Fact]
    public void Every_handler_named_in_the_XAML_exists_in_the_code_behind_and_every_named_element_is_used()
    {
        var xaml = File.ReadAllText(Path.Combine(PageFolder, "UnattendedPage.xaml"));
        var code = File.ReadAllText(Path.Combine(PageFolder, "UnattendedPage.xaml.cs"));
        var handlers = Regex.Matches(xaml, @"(?:Click|Toggled)=""(\w+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(handlers);
        foreach (var handler in handlers) Assert.Matches($@"void {handler}\(object sender, RoutedEventArgs e\)", code);
        foreach (var name in Regex.Matches(xaml, @"x:Name=""(\w+)""").Select(m => m.Groups[1].Value)) Assert.Contains(name + ".", code);
    }

    [Fact]
    public void The_page_is_in_the_navigation_and_in_the_list_that_CI_opens()
    {
        var code = File.ReadAllText(Path.Combine(PageFolder, "UnattendedPage.xaml.cs"));
        Assert.Contains("[NavigationPage(\"unattended\", \"Unattended_Nav\"", code);
        var pages = File.ReadAllLines(Path.Combine(RepoRoot(), "tools", "ci", "pages.txt")).Select(l => l.Trim());
        Assert.Contains("UnattendedPage", pages);
        Assert.Contains("x:Class=\"Remotely.App.Views.Unattended.UnattendedPage\"", File.ReadAllText(Path.Combine(PageFolder, "UnattendedPage.xaml")));
    }

    // ---- the status line ----

    private static UnattendedView Running(Action<UnattendedStatus>? shape = null)
    {
        var status = new UnattendedStatus { HelperAttached = true };
        shape?.Invoke(status);
        return new UnattendedView(ServiceState.Running, null, status, false);
    }

    [Theory]
    [InlineData(ServiceState.NotInstalled, "Unattended_Line_NotInstalled")]
    [InlineData(ServiceState.Stopped, "Unattended_Line_Stopped")]
    [InlineData(ServiceState.Starting, "Unattended_Line_Starting")]
    [InlineData(ServiceState.Stopping, "Unattended_Line_Stopping")]
    [InlineData(ServiceState.Paused, "Unattended_Line_Paused")]
    [InlineData(ServiceState.Unknown, "Unattended_Line_Unknown")]
    public void A_service_that_is_not_running_is_described_by_its_state_whatever_else_is_known(ServiceState state, string key)
    {
        Assert.Equal(key, StatusLine.Describe(new UnattendedView(state, null, new UnattendedStatus { Listening = true, Controller = "x" }, true)).Key);
    }

    [Theory]
    [InlineData(PipeProblem.Unreachable, "Unattended_Line_LinkUnreachable")]
    [InlineData(PipeProblem.AccessDenied, "Unattended_Line_LinkDenied")]
    [InlineData(PipeProblem.UntrustedServer, "Unattended_Line_LinkUntrusted")]
    [InlineData(PipeProblem.BadReply, "Unattended_Line_LinkBad")]
    public void A_running_service_the_app_cannot_trust_or_reach_says_which_it_is(PipeProblem problem, string key)
    {
        Assert.Equal(key, StatusLine.Describe(new UnattendedView(ServiceState.Running, problem, null, null)).Key);
    }

    [Fact]
    public void The_line_follows_what_is_happening_with_the_most_important_thing_first()
    {
        Assert.Equal("Unattended_Line_Ready", StatusLine.Describe(Running()).Key);
        Assert.Equal("Unattended_Line_NoHelper", StatusLine.Describe(Running(s => s.HelperAttached = false)).Key);
        Assert.Equal("Unattended_Line_Listening", StatusLine.Describe(Running(s => s.Listening = true)).Key);
        Assert.Equal("Unattended_Line_ViewingLock", StatusLine.Describe(Running(s => s.CaptureDesktop = "Winlogon")).Key);
        Assert.Equal("Unattended_Line_ViewingDesktop", StatusLine.Describe(Running(s => s.CaptureDesktop = "Default")).Key);

        var lock1 = StatusLine.Describe(Running(s => { s.Controller = "Dad's laptop"; s.CaptureDesktop = "Winlogon"; s.Listening = true; }));
        Assert.Equal("Unattended_Line_SessionLock", lock1.Key);
        Assert.Equal(["Dad's laptop"], lock1.Args);
        Assert.Equal("Unattended_Line_SessionDesktop", StatusLine.Describe(Running(s => { s.Controller = "x"; s.CaptureDesktop = "Default"; })).Key);
        Assert.Equal("Unattended_Line_Session", StatusLine.Describe(Running(s => s.Controller = "x")).Key);

        // a question outranks everything else
        var asking = StatusLine.Describe(Running(s => { s.Controller = "x"; s.CaptureDesktop = "Winlogon"; s.Question = new ConsentQuestion { Id = "q", ControllerName = "Anna" }; }));
        Assert.Equal("Unattended_Line_Asking", asking.Key);
        Assert.Equal(["Anna"], asking.Args);
    }

    [Fact]
    public void Whatever_the_combination_the_line_is_a_known_key_with_as_many_arguments_as_its_text_has_placeholders()
    {
        var en = Strings("en-US");
        var known = StatusLine.AllKeys.ToHashSet();
        foreach (var service in Enum.GetValues<ServiceState>())
        foreach (var problem in new PipeProblem?[] { null, PipeProblem.Unreachable, PipeProblem.AccessDenied, PipeProblem.UntrustedServer, PipeProblem.BadReply })
        foreach (var withStatus in new[] { false, true })
        foreach (var desktop in new[] { null, "Default", "Winlogon", "Other" })
        foreach (var controller in new[] { null, "Anna" })
        foreach (var asking in new[] { false, true })
        foreach (var helper in new[] { false, true })
        foreach (var listening in new[] { false, true })
        {
            var status = withStatus ? new UnattendedStatus
            {
                CaptureDesktop = desktop, Controller = controller, HelperAttached = helper, Listening = listening,
                Question = asking ? new ConsentQuestion { Id = "q", ControllerName = "Bo" } : null,
            } : null;
            var line = StatusLine.Describe(new UnattendedView(service, problem, status, null));
            Assert.Contains(line.Key, known);
            Assert.Equal(Placeholders(en[line.Key]).Length, line.Args.Length);
        }
    }

    [Fact]
    public void The_time_left_on_a_token_is_never_negative()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.FromMinutes(3), StatusLine.Remaining(now.AddMinutes(3), now));
        Assert.Equal(TimeSpan.Zero, StatusLine.Remaining(now.AddMinutes(-3), now));
        Assert.Equal(TimeSpan.Zero, StatusLine.Remaining(null, now));
    }

    // ---- asking for administrator rights ----

    [Fact]
    public void Each_administrator_action_is_a_fixed_list_of_plain_words_and_no_two_are_alike()
    {
        var seen = new HashSet<string>();
        foreach (var action in Enum.GetValues<ElevatedAction>())
        {
            var args = ElevatedActions.Arguments(action);
            Assert.NotEmpty(args);
            Assert.All(args, a => Assert.Matches("^[a-z][a-z-]*$", a));   // no spaces, quotes, slashes or anything that could change how the command line is read
            Assert.True(seen.Add(string.Join(' ', args)), "duplicate arguments for " + action);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ElevatedActions.Arguments((ElevatedAction)999));
    }

    [Fact]
    public void The_arguments_are_commands_the_host_program_really_has()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Remotely.Host", "Program.cs"));
        var unattended = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Remotely.Host", "Commands", "UnattendedCommand.cs"));
        foreach (var action in Enum.GetValues<ElevatedAction>())
        {
            var args = ElevatedActions.Arguments(action);
            Assert.Contains($"case \"{args[0]}\"", program);
            if (args[0] == "unattended")
            {
                Assert.Equal(3, args.Count);
                Assert.Contains($"case \"{args[1]}\" when valid", unattended);
                Assert.Contains(args[2], new[] { "on", "off" });
            }
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\Remotely\Remotely.Host.exe", true)]
    [InlineData(@"c:\program files\remotely\REMOTELY.HOST.EXE", true)]
    [InlineData(@"C:/Program Files/Remotely/Remotely.Host.exe", true)]
    [InlineData(@"C:\Program Files\Some Other Folder\Deeper\Remotely.Host.exe", true)]
    [InlineData(@"C:\Users\Anna\AppData\Local\Programs\Remotely\Remotely.Host.exe", false)]   // a per-user install
    [InlineData(@"C:\Users\Anna\Downloads\Remotely.Host.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Remotely\Remotely.Host.exe", false)]                // another folder that only starts with the same letters
    [InlineData(@"C:\Program FilesX\Remotely\Remotely.Host.exe", false)]
    [InlineData(@"C:\Program Files\Remotely.Host.exe", false)]                               // directly in Program Files: every installer shares that folder
    [InlineData(@"C:\Program Files\Remotely\Other.exe", false)]
    [InlineData(@"C:\Program Files\Remotely\..\..\Users\Anna\Remotely.Host.exe", false)]
    [InlineData(@"C:\Program Files\.\Remotely\Remotely.Host.exe", false)]
    [InlineData(@"\\server\share\Program Files\Remotely\Remotely.Host.exe", false)]
    [InlineData(@"D:\Program Files\Remotely\Remotely.Host.exe", false)]                      // another drive
    [InlineData("", false)]
    public void Administrator_rights_are_asked_only_for_the_host_program_installed_below_Program_Files(string path, bool expected)
    {
        Assert.Equal(expected, ElevatedActions.IsTrustedLocation(path, @"C:\Program Files"));
    }

    [Fact]
    public void Without_a_known_Program_Files_folder_nothing_is_trusted()
    {
        Assert.False(ElevatedActions.IsTrustedLocation(@"C:\Program Files\Remotely\Remotely.Host.exe", ""));
        Assert.False(ElevatedActions.IsTrustedLocation(@"C:\Program Files\Remotely\Remotely.Host.exe", null));
        Assert.False(ElevatedActions.IsTrustedLocation(null, @"C:\Program Files"));
        Assert.False(ElevatedActions.IsTrustedLocation(@"C:\Program Files\Remotely\Remotely.Host.exe", @"C:\Program Files\..\Users"));
    }

    // ---- Ctrl+Alt+Del policy ----

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(7, false)]
    [InlineData(-1, false)]
    public void Services_may_send_Ctrl_Alt_Del_only_when_the_policy_value_says_services(int? value, bool expected)
    {
        Assert.Equal(expected, SasPolicy.ServicesMaySend(value));
    }

    [Fact]
    public void The_policy_location_matches_the_one_the_existing_enable_sas_command_writes()
    {
        var installer = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Remotely.Host", "Commands", "Installer.cs"));
        Assert.Contains(SasPolicy.KeyPath, installer);
        Assert.Contains(SasPolicy.ValueName, installer);
    }
}
