using Remotely.App.Shell;

namespace Remotely.Tests;

public class CommandLineTests
{
    [Fact]
    public void No_arguments_means_a_plain_launch()
    {
        var o = AppOptions.Parse([]);
        Assert.False(o.HasWork);
        Assert.False(o.IsSelfTest);
        Assert.True(o.UsesSingleInstance);
        Assert.Empty(o.Problems);
    }

    [Fact]
    public void Every_documented_option_is_read()
    {
        var o = AppOptions.Parse(["--invite", "RMT1-abc", "--pair", "ABCD-EFGH", "--connect-token", "K7P2-9XQM", "--minimized", "--show", "SettingsPage"]);
        Assert.Equal("RMT1-abc", o.Invite);
        Assert.Equal("ABCD-EFGH", o.Pair);
        Assert.Equal("K7P2-9XQM", o.ConnectToken);
        Assert.Equal("SettingsPage", o.Show);
        Assert.True(o.Minimized);
        Assert.True(o.HasWork);
        Assert.Empty(o.Problems);
    }

    [Fact]
    public void Selftest_options_keep_their_old_meaning_and_skip_single_instance()
    {
        var lan = AppOptions.Parse(["--selftest", "result.txt"]);
        Assert.Equal("result.txt", lan.SelfTestResultPath);
        Assert.Null(lan.SelfTestServerResultPath);
        Assert.False(lan.UsesSingleInstance);

        var server = AppOptions.Parse(["--selftest-server", "r.txt", "ab12"]);
        Assert.Equal("r.txt", server.SelfTestServerResultPath);
        Assert.Equal("ab12", server.SelfTestServerHostPrefix);
        Assert.False(server.UsesSingleInstance);
    }

    [Fact]
    public void Show_launches_never_join_a_running_instance()
        => Assert.False(AppOptions.Parse(["--show", "HomePage"]).UsesSingleInstance);

    [Fact]
    public void Missing_values_and_unknown_options_are_reported_not_fatal()
    {
        var o = AppOptions.Parse(["--invite", "--minimized", "--bogus", "--selftest-server", "only-one"]);
        Assert.Null(o.Invite);
        Assert.True(o.Minimized);
        Assert.Equal(4, o.Problems.Count);   // --invite value, --bogus, --selftest-server values, and the stray "only-one"
        Assert.False(o.IsSelfTest);
    }

    [Fact]
    public void Options_are_case_insensitive_and_a_trailing_option_is_a_problem()
    {
        var o = AppOptions.Parse(["--INVITE", " RMT1-x ", "--pair"]);
        Assert.Equal("RMT1-x", o.Invite);
        Assert.Single(o.Problems);
    }

    [Fact]
    public void Startup_command_quotes_the_path_and_starts_minimized()
        => Assert.Equal("\"C:\\Program Files\\Remotely\\Remotely.exe\" --minimized", StartupCommand.Build("C:\\Program Files\\Remotely\\Remotely.exe"));
}

public class WindowPlacementTests
{
    private static readonly PixelRect Main = new(0, 0, 1920, 1040);
    private static readonly PixelRect Right = new(1920, 0, 1920, 1040);
    private static readonly PixelRect Left = new(-1280, 0, 1280, 984);

    private static WindowBounds Fit(WindowBounds? saved, params PixelRect[] areas) => WindowPlacement.Fit(saved, areas, 1100, 720, 640, 480);

    [Fact]
    public void First_run_centres_the_default_size_on_the_primary_monitor()
        => Assert.Equal(new WindowBounds(410, 160, 1100, 720, false), Fit(null, Main));

    [Fact]
    public void A_visible_remembered_position_is_kept()
    {
        var saved = new WindowBounds(300, 100, 1000, 700, false);
        Assert.Equal(saved, Fit(saved, Main, Right));
    }

    [Fact]
    public void A_window_on_a_removed_monitor_comes_back_on_the_primary()
    {
        var saved = new WindowBounds(2200, 100, 1000, 700, false);
        var fitted = Fit(saved, Main);
        Assert.Equal(new WindowBounds(460, 170, 1000, 700, false), fitted);
    }

    [Fact]
    public void A_window_on_a_monitor_with_negative_coordinates_is_kept_while_that_monitor_exists()
    {
        var saved = new WindowBounds(-1200, 50, 1000, 700, false);
        Assert.Equal(saved, Fit(saved, Main, Left));
        Assert.NotEqual(saved, Fit(saved, Main));
    }

    [Fact]
    public void A_title_strip_hanging_off_the_bottom_is_not_reachable_so_the_window_is_moved()
    {
        var saved = new WindowBounds(100, 1030, 1000, 700, false);
        var fitted = Fit(saved, Main);
        Assert.True(fitted.Y + WindowPlacement.TitleStripHeight <= Main.Bottom);
    }

    [Fact]
    public void A_window_bigger_than_a_smaller_resolution_is_shrunk_to_fit()
    {
        var small = new PixelRect(0, 0, 1024, 728);
        var fitted = Fit(new WindowBounds(0, 0, 1800, 1000, false), small);
        Assert.Equal(new WindowBounds(0, 0, 1024, 728, false), fitted);
    }

    [Fact]
    public void Absurd_saved_sizes_fall_back_to_the_default_and_the_maximized_flag_survives()
    {
        Assert.Equal(new WindowBounds(410, 160, 1100, 720, false), Fit(new WindowBounds(10, 10, 0, -5, true), Main));
        Assert.True(Fit(new WindowBounds(5000, 5000, 900, 600, true), Main).Maximized);
    }

    [Fact]
    public void Without_any_monitor_information_the_saved_value_is_returned_unchanged()
    {
        var saved = new WindowBounds(5, 5, 800, 600, false);
        Assert.Equal(saved, Fit(saved));
    }
}

[NavigationPage("t-attr", "Nav_Test", Order = 5, Glyph = 0xE001)]
internal sealed class AttributedPage { }

[NavigationPage("home", "Nav_Replacement", Order = 1)]
internal sealed class ReplacementHomePage { }

[NavigationPage("settings", "Nav_Settings")]
internal sealed class NewSettingsPage { }

public class NavigationCatalogTests
{
    private static readonly NavEntry Home = new("home", "Nav_Home", typeof(object), 0xE10F, 10);
    private static readonly NavEntry Computers = new("computers", "Nav_Computers", typeof(string), 0xE8AF, 20);

    [Fact]
    public void Entries_are_ordered_by_order_then_tag()
    {
        var all = NavigationCatalog.Build([Computers, Home], [typeof(AttributedPage)]);
        Assert.Equal(["t-attr", "home", "computers"], all.Select(e => e.Tag));
        Assert.Equal(0xE001, all[0].Glyph);
        Assert.Equal(typeof(AttributedPage), all[0].PageType);
    }

    [Fact]
    public void An_attributed_page_replaces_a_built_in_with_the_same_tag()
    {
        var all = NavigationCatalog.Build([Home, Computers], [typeof(ReplacementHomePage)]);
        Assert.Equal(2, all.Count);
        Assert.Equal(typeof(ReplacementHomePage), all.Single(e => e.Tag == "home").PageType);
    }

    [Fact]
    public void The_settings_entry_replaces_the_page_but_is_not_a_menu_item()
    {
        var all = NavigationCatalog.Build([Home, new NavEntry("settings", "Nav_Settings", typeof(object), 0, 900)], [typeof(NewSettingsPage)]);
        Assert.Equal(typeof(NewSettingsPage), all.Single(e => e.Tag == "settings").PageType);
        Assert.DoesNotContain(NavigationCatalog.Menu(all), e => e.Tag == "settings");
    }

    [Fact]
    public void Entries_without_a_tag_are_ignored()
        => Assert.Empty(NavigationCatalog.Build([new NavEntry(" ", "k", typeof(object), 0, 1)], []));

    [Fact]
    public void Register_replaces_an_earlier_entry_with_the_same_tag()
    {
        NavigationCatalog.Register(new NavEntry("reg-test", "A", typeof(object), 0, 1));
        NavigationCatalog.Register(new NavEntry("reg-test", "B", typeof(string), 0, 1));
        var all = NavigationCatalog.All(typeof(NavigationCatalogTests).Assembly);
        Assert.Equal("B", all.Single(e => e.Tag == "reg-test").TitleKey);
    }
}

public class SingleInstanceTests
{
    private static string UniqueName() => "Remotely.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_second_launch_forwards_its_arguments_and_learns_the_first_ones_process_id()
    {
        var name = UniqueName();
        using var first = SingleInstance.Acquire(name);
        Assert.True(first.IsPrimary);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Listen(a => received.TrySetResult(a));

        using var second = SingleInstance.Acquire(name);
        Assert.False(second.IsPrimary);
        var pid = 0;
        var ok = await second.ForwardAsync(["--invite", "RMT1-xyz"], TimeSpan.FromSeconds(10), p => pid = p);

        Assert.True(ok);
        Assert.Equal(Environment.ProcessId, pid);
        Assert.Equal(["--invite", "RMT1-xyz"], await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Several_launches_in_a_row_are_all_delivered()
    {
        var name = UniqueName();
        using var first = SingleInstance.Acquire(name);
        var got = new System.Collections.Concurrent.ConcurrentBag<string>();
        first.Listen(a => got.Add(a[0]));
        using var other = SingleInstance.Acquire(name);
        for (var i = 0; i < 3; i++) Assert.True(await other.ForwardAsync([$"n{i}"], TimeSpan.FromSeconds(10)));
        for (var i = 0; i < 50 && got.Count < 3; i++) await Task.Delay(100);
        Assert.Equal(["n0", "n1", "n2"], got.Order());
    }

    [Fact]
    public async Task Forwarding_to_nobody_gives_up_after_the_timeout()
    {
        using var primaryThatNeverListens = SingleInstance.Acquire(UniqueName());
        Assert.True(primaryThatNeverListens.IsPrimary);
        Assert.False(await primaryThatNeverListens.ForwardAsync(["x"], TimeSpan.FromMilliseconds(800)));
    }

    [Fact]
    public void The_name_is_free_again_after_the_first_instance_is_disposed()
    {
        var name = UniqueName();
        SingleInstance.Acquire(name).Dispose();
        using var again = SingleInstance.Acquire(name);
        Assert.True(again.IsPrimary);
    }
}

public class CrashReportTests
{
    [Fact]
    public void The_report_has_version_system_time_and_the_full_exception()
    {
        var ex = new InvalidOperationException("boom", new IOException("inner"));
        var text = CrashReport.Format(ex, "1.0.0", "Windows 11", "Arm64", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        Assert.StartsWith("Remotely 1.0.0 on Windows 11 Arm64", text);
        Assert.Contains("2026-01-02 03:04:05Z", text);
        Assert.Contains("boom", text);
        Assert.Contains("inner", text);
    }

    [Fact]
    public void A_missing_exception_still_gives_a_report()
        => Assert.Contains("no exception information", CrashReport.Format(null, "1", "os", "x64", DateTimeOffset.UnixEpoch));
}
