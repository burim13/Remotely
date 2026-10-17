using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Remotely.Core.Update;

namespace Remotely.Tests;

/// <summary>
/// The update check (src/Remotely.Core/Update) against a local HTTP test feed that looks like the GitHub releases API: feed parsing, version order, the published
/// SHA-256 file, a good package, a tampered package, a package without a published hash, untrusted download addresses and the opt-in rules.
/// </summary>
public class UpdateTests
{
    // ---- versions ----

    [Theory]
    [InlineData("0.4.0", "0.5.0")]
    [InlineData("0.9.0", "0.10.0")]
    [InlineData("1.0.0-beta.1", "1.0.0")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-1", "1.0.0-alpha")]
    [InlineData("1.0.0-beta", "1.0.0-beta.1")]
    [InlineData("v0.4.9", "v0.5.0")]
    public void A_version_sorts_below_the_next_one(string older, string newer)
    {
        Assert.True(ReleaseVersion.Parse(older) < ReleaseVersion.Parse(newer));
        Assert.True(ReleaseVersion.Parse(newer) > ReleaseVersion.Parse(older));
    }

    [Theory]
    [InlineData("v0.4.0", "0.4.0")]
    [InlineData("0.4.0+build.7", "0.4.0")]
    [InlineData("V1.2.3-rc.1", "1.2.3-rc.1")]
    public void A_version_is_written_without_the_v_and_the_build_part(string input, string expected)
    {
        Assert.Equal(expected, ReleaseVersion.Parse(input).ToString());
        Assert.Equal(ReleaseVersion.Parse(expected), ReleaseVersion.Parse(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("99999999999.0.0")]
    public void A_tag_that_is_not_a_version_is_refused(string text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    // ---- feed and sums text ----

    [Fact]
    public void The_feed_parser_skips_drafts_and_unreadable_tags_and_keeps_the_assets()
    {
        var json = """
        [
          {"tag_name":"v0.5.0","name":"Remotely v0.5.0","body":"notes","html_url":"https://example/r","draft":false,"prerelease":false,
           "assets":[{"name":"a.exe","browser_download_url":"https://x/a.exe","size":12},{"name":"","browser_download_url":"https://x/none"}]},
          {"tag_name":"v0.6.0","draft":true,"assets":[]},
          {"tag_name":"nightly","draft":false,"assets":[]},
          {"tag_name":"v0.5.1-beta.1","draft":false,"prerelease":true,"assets":[]},
          "not an object"
        ]
        """;
        var releases = ReleaseFeed.Parse(json);
        Assert.Equal(["v0.5.0", "v0.5.1-beta.1"], releases.Select(r => r.Tag));
        Assert.False(releases[0].IsPreRelease);
        Assert.True(releases[1].IsPreRelease);
        var asset = Assert.Single(releases[0].Assets);
        Assert.Equal(("a.exe", "https://x/a.exe", 12), (asset.Name, asset.Url, asset.Size));
    }

    [Fact]
    public void The_sums_parser_reads_sha256sum_output_and_ignores_ambiguous_names()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var c = new string('c', 64);
        var text = $"{a}  one.exe\r\n{b} *two.exe\n# comment\nnot a line\n{a}  dup.exe\n{c}  dup.exe\n{a}  ../evil.exe\n{new string('d', 63)}  short.exe\n";
        var sums = ReleaseFeed.ParseSums(text);
        Assert.Equal(a, sums["one.exe"]);
        Assert.Equal(new string('b', 64), sums["two.exe"]);
        Assert.DoesNotContain("dup.exe", sums.Keys);
        Assert.DoesNotContain("../evil.exe", sums.Keys);
        Assert.DoesNotContain("short.exe", sums.Keys);
    }

    [Fact]
    public void Release_notes_are_cut_to_a_sensible_length()
    {
        var json = JsonSerializer.Serialize(new { tag_name = "v1.0.0", body = new string('x', 10000), assets = Array.Empty<object>() });
        Assert.True(ReleaseFeed.Parse("[" + json + "]")[0].Notes.Length <= 4001);
    }

    // ---- checking against a local feed ----

    [Fact]
    public async Task A_newer_release_with_a_published_hash_is_offered()
    {
        using var feed = new TestFeed();
        var package = feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", Package("good"));
        var result = await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64");
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal(Sha(package), result.Offer!.Sha256);
        Assert.Equal("Remotely-Setup-0.5.0-x64.exe", result.Offer.AssetName);
        Assert.Equal(ReleaseVersion.Parse("0.5.0"), result.Offer.Version);
    }

    [Fact]
    public async Task The_newest_release_wins_and_older_or_equal_ones_are_not_offered()
    {
        using var feed = new TestFeed();
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", Package("old"));
        feed.AddRelease("v0.6.0", "Remotely-Setup-0.6.0-x64.exe", Package("new"));
        var checker = Checker(feed);
        Assert.Equal(ReleaseVersion.Parse("0.6.0"), (await checker.CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64")).Offer!.Version);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await checker.CheckAsync(ReleaseVersion.Parse("0.6.0"), UpdateProduct.Client, "x64")).Status);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await checker.CheckAsync(ReleaseVersion.Parse("1.0.0"), UpdateProduct.Client, "x64")).Status);
    }

    [Fact]
    public async Task The_arm64_and_server_installers_are_picked_by_name()
    {
        using var feed = new TestFeed();
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", Package("x64"), extra: [("Remotely-Setup-0.5.0-arm64.exe", Package("arm")), ("Remotely-Server-Setup-0.5.0-x64.exe", Package("srv"))]);
        var checker = Checker(feed);
        var current = ReleaseVersion.Parse("0.4.0");
        Assert.Equal("Remotely-Setup-0.5.0-arm64.exe", (await checker.CheckAsync(current, UpdateProduct.Client, "arm64")).Offer!.AssetName);
        Assert.Equal("Remotely-Server-Setup-0.5.0-x64.exe", (await checker.CheckAsync(current, UpdateProduct.Server, "x64")).Offer!.AssetName);
    }

    [Fact]
    public async Task A_release_without_a_sums_file_or_without_a_line_for_the_package_is_not_offered()
    {
        using var feed = new TestFeed();
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", Package("a"), publishSums: false);
        var none = await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64");
        Assert.Equal(UpdateCheckStatus.Unverifiable, none.Status);
        Assert.Null(none.Offer);

        using var feed2 = new TestFeed();
        feed2.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", Package("a"), sumsText: $"{new string('1', 64)}  something-else.exe\n");
        var other = await Checker(feed2).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64");
        Assert.Equal(UpdateCheckStatus.Unverifiable, other.Status);
        Assert.Null(other.Offer);
    }

    [Fact]
    public async Task A_release_without_an_installer_for_this_kind_of_pc_says_so()
    {
        using var feed = new TestFeed();
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", Package("a"));
        var result = await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "arm64");
        Assert.Equal(UpdateCheckStatus.NoPackage, result.Status);
    }

    [Fact]
    public async Task Pre_releases_are_ignored_unless_asked_for()
    {
        using var feed = new TestFeed();
        feed.AddRelease("v0.5.0-beta.1", "Remotely-Setup-0.5.0-beta.1-x64.exe", Package("beta"), prerelease: true);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64")).Status);
        Assert.Equal(UpdateCheckStatus.Available, (await Checker(feed, new UpdateOptions { IncludePreReleases = true }).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64")).Status);
    }

    [Fact]
    public async Task A_package_address_outside_the_trusted_prefix_is_never_offered()
    {
        using var feed = new TestFeed();
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", Package("a"), packageUrlOverride: "http://127.0.0.1:1/elsewhere/Remotely-Setup-0.5.0-x64.exe");
        var result = await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64");
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Null(result.Offer);
    }

    [Fact]
    public async Task Plain_http_is_refused_with_the_default_options()
    {
        using var feed = new TestFeed();
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", Package("a"));
        var strict = new UpdateOptions { FeedUrl = feed.FeedUrl, TrustedDownloadPrefixes = [feed.Prefix] };   // AllowLoopbackHttp stays false
        var result = await new UpdateChecker(new HttpClient(), strict).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64");
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(0, feed.Requests);
    }

    [Fact]
    public async Task An_unreachable_feed_a_server_error_and_garbage_are_reported_not_thrown()
    {
        using var feed = new TestFeed();
        feed.Raw("/feed", 500, "boom");
        Assert.Equal(UpdateCheckStatus.Failed, (await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64")).Status);
        feed.Raw("/feed", 200, "<html>not json</html>");
        Assert.Equal(UpdateCheckStatus.Failed, (await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64")).Status);
        feed.Raw("/feed", 200, new string(' ', 3 * 1024 * 1024));
        Assert.Equal(UpdateCheckStatus.Failed, (await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64")).Status);

        var dead = new UpdateOptions { FeedUrl = "http://127.0.0.1:1/feed", AllowLoopbackHttp = true, TrustedDownloadPrefixes = ["http://127.0.0.1:1/"] };
        Assert.Equal(UpdateCheckStatus.Failed, (await new UpdateChecker(new HttpClient(), dead).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64")).Status);
    }

    // ---- downloading: good, tampered, oversize ----

    [Fact]
    public async Task A_good_package_is_downloaded_verified_and_left_unstarted()
    {
        using var feed = new TestFeed();
        var package = feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", Package("good", 200_000));
        var offer = (await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64")).Offer!;
        using var dir = new TempDir();
        var steps = new List<DownloadProgress>();
        var verified = await Downloader(feed).DownloadAsync(offer, dir.Path, new SyncProgress(steps.Add));
        Assert.Equal(package, await File.ReadAllBytesAsync(verified.Path));
        Assert.Equal(Sha(package), verified.Sha256);
        Assert.Equal(SignatureStatus.NotSigned, verified.Signature.Status);
        Assert.Equal(dir.Path, Path.GetDirectoryName(verified.Path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.part"));
        Assert.True(steps.Count > 0 && steps[^1].BytesDone == package.Length);
        Assert.True(await verified.StillMatchesAsync());
        await File.AppendAllTextAsync(verified.Path, "x");
        Assert.False(await verified.StillMatchesAsync());   // changed after the download: the app must not run it
    }

    [Fact]
    public async Task A_tampered_package_is_refused_and_leaves_nothing_behind()
    {
        using var feed = new TestFeed();
        var real = Package("real", 50_000);
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", real);
        var offer = (await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64")).Offer!;
        var tampered = (byte[])real.Clone();
        tampered[tampered.Length / 2] ^= 0x01;   // one flipped bit
        feed.ReplacePackage("Remotely-Setup-0.5.0-x64.exe", tampered);
        using var dir = new TempDir();
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => Downloader(feed).DownloadAsync(offer, dir.Path));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task A_package_of_another_length_is_refused()
    {
        using var feed = new TestFeed();
        var real = Package("real", 50_000);
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", real);
        var offer = (await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64")).Offer!;
        feed.ReplacePackage("Remotely-Setup-0.5.0-x64.exe", real[..^1]);
        using var dir = new TempDir();
        await Assert.ThrowsAsync<UpdateVerificationException>(() => Downloader(feed).DownloadAsync(offer, dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task A_package_larger_than_the_limit_is_stopped_while_downloading()
    {
        using var feed = new TestFeed();
        var big = Package("big", 100_000);
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", big);
        var offer = (await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64")).Offer!;
        using var dir = new TempDir();
        var small = new UpdateDownloader(new HttpClient(), Options(feed, new UpdateOptions { MaxPackageBytes = 10_000 }), new FakeSignature(SignatureStatus.NotSigned));
        await Assert.ThrowsAsync<UpdateVerificationException>(() => small.DownloadAsync(offer, dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Theory]
    [InlineData("../evil.exe")]
    [InlineData("a b.exe")]
    [InlineData("setup.msi")]
    [InlineData("C:\\evil.exe")]
    public async Task A_package_name_that_could_escape_the_folder_is_refused(string name)
    {
        using var feed = new TestFeed();
        using var dir = new TempDir();
        var offer = new UpdateOffer(ReleaseVersion.Parse("0.5.0"), "v0.5.0", "", "", name, feed.Prefix + "x.exe", 1, new string('a', 64));
        await Assert.ThrowsAsync<UpdateVerificationException>(() => Downloader(feed).DownloadAsync(offer, dir.Path));
    }

    [Fact]
    public async Task A_download_address_outside_the_trusted_prefix_is_refused_before_any_request()
    {
        using var feed = new TestFeed();
        using var dir = new TempDir();
        var offer = new UpdateOffer(ReleaseVersion.Parse("0.5.0"), "v0.5.0", "", "", "Remotely-Setup-0.5.0-x64.exe", "https://evil.example/Remotely-Setup-0.5.0-x64.exe", 1, new string('a', 64));
        await Assert.ThrowsAsync<UpdateVerificationException>(() => Downloader(feed).DownloadAsync(offer, dir.Path));
        Assert.Equal(0, feed.Requests);
    }

    [Fact]
    public async Task A_signature_that_is_present_but_not_valid_is_refused_and_a_valid_one_is_accepted()
    {
        using var feed = new TestFeed();
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", Package("signed"));
        var offer = (await Checker(feed).CheckAsync(ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64")).Offer!;
        using var dir = new TempDir();
        var bad = new UpdateDownloader(new HttpClient(), Options(feed), new FakeSignature(SignatureStatus.Invalid));
        await Assert.ThrowsAsync<UpdateVerificationException>(() => bad.DownloadAsync(offer, dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
        var good = new UpdateDownloader(new HttpClient(), Options(feed), new FakeSignature(SignatureStatus.Valid));
        Assert.Equal(SignatureStatus.Valid, (await good.DownloadAsync(offer, dir.Path)).Signature.Status);
    }

    [Theory]
    [InlineData("STATUS=Valid\nSIGNER=CN=Remotely\n", 0, SignatureStatus.Valid)]
    [InlineData("STATUS=NotSigned\n", 0, SignatureStatus.NotSigned)]
    [InlineData("STATUS=HashMismatch\n", 0, SignatureStatus.Invalid)]
    [InlineData("STATUS=NotTrusted\nSIGNER=CN=Self\n", 0, SignatureStatus.Invalid)]
    [InlineData("STATUS=UnknownError\n", 0, SignatureStatus.Invalid)]
    [InlineData("", 0, SignatureStatus.Invalid)]
    [InlineData("STATUS=Valid\n", 1, SignatureStatus.Invalid)]
    public void The_powershell_answer_is_read_strictly(string output, int exitCode, SignatureStatus expected)
    {
        Assert.Equal(expected, PowerShellAuthenticodeVerifier.Interpret(output, exitCode, "").Status);
    }

    // ---- the flow the apps bind to ----

    [Fact]
    public async Task Update_checks_are_off_until_the_person_turns_them_on()
    {
        using var feed = new TestFeed();
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", Package("a"));
        using var dir = new TempDir();
        var controller = Controller(feed, dir, new UpdateSettings());
        Assert.Equal(UpdatePhase.Off, controller.State.Phase);
        await controller.CheckIfDueAsync();
        Assert.Equal(0, feed.Requests);   // nothing contacted on its own
        Assert.Equal(UpdatePhase.Off, controller.State.Phase);

        await controller.CheckNowAsync();   // an explicit click is allowed
        Assert.Equal(UpdatePhase.Available, controller.State.Phase);
        Assert.False(controller.Enabled);
    }

    [Fact]
    public async Task The_opt_in_is_saved_and_a_damaged_settings_file_means_off()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "updates.json");
        Assert.False(UpdateSettings.Load(path).Enabled);
        new UpdateSettings { Enabled = true, SkippedVersion = "0.5.0" }.Save(path);
        var loaded = UpdateSettings.Load(path);
        Assert.True(loaded.Enabled);
        Assert.Equal("0.5.0", loaded.SkippedVersion);
        await File.WriteAllTextAsync(path, "{ not json");
        Assert.False(UpdateSettings.Load(path).Enabled);
    }

    [Fact]
    public async Task Automatic_checks_run_at_most_once_a_day_and_skipped_versions_stay_quiet()
    {
        using var feed = new TestFeed();
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", Package("a"));
        using var dir = new TempDir();
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
        var settings = new UpdateSettings { Enabled = true };
        var controller = Controller(feed, dir, settings, clock);

        await controller.CheckIfDueAsync();
        Assert.Equal(UpdatePhase.Available, controller.State.Phase);
        Assert.Equal(1, feed.FeedRequests);

        await controller.CheckIfDueAsync();   // same day: no second request
        Assert.Equal(1, feed.FeedRequests);

        clock.Now += TimeSpan.FromHours(25);
        controller.SkipThisVersion();
        await controller.CheckIfDueAsync();   // due again, but this version was skipped
        Assert.Equal(2, feed.FeedRequests);
        Assert.Equal(UpdatePhase.UpToDate, controller.State.Phase);

        await controller.CheckNowAsync();     // a manual check shows it again
        Assert.Equal(UpdatePhase.Available, controller.State.Phase);
    }

    [Fact]
    public async Task The_controller_downloads_to_ready_and_reports_a_tampered_file_as_failed()
    {
        using var feed = new TestFeed();
        var real = Package("a", 30_000);
        feed.AddRelease("v0.5.0", "Remotely-Setup-0.5.0-x64.exe", real);
        using var dir = new TempDir();
        var controller = Controller(feed, dir, new UpdateSettings { Enabled = true });
        var changes = 0;
        controller.Changed += () => Interlocked.Increment(ref changes);
        await controller.CheckNowAsync();
        await controller.DownloadAsync();
        Assert.Equal(UpdatePhase.Ready, controller.State.Phase);
        Assert.True(File.Exists(controller.State.Package!.Path));
        Assert.True(changes >= 4);

        var tampered = (byte[])real.Clone();
        tampered[10] ^= 0xFF;
        feed.ReplacePackage("Remotely-Setup-0.5.0-x64.exe", tampered);
        await controller.CheckNowAsync();
        await controller.DownloadAsync();
        Assert.Equal(UpdatePhase.Failed, controller.State.Phase);
        Assert.Contains("SHA-256", controller.State.Detail);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "downloads")));
    }

    [Fact]
    public async Task A_failed_check_does_not_count_as_a_check_so_it_is_tried_again_at_next_start()
    {
        using var feed = new TestFeed();
        feed.Raw("/feed", 503, "down");
        using var dir = new TempDir();
        var settings = new UpdateSettings { Enabled = true };
        var controller = Controller(feed, dir, settings);
        await controller.CheckIfDueAsync();
        Assert.Equal(UpdatePhase.Failed, controller.State.Phase);
        Assert.Null(settings.LastCheckUtc);
    }

    [Fact]
    public async Task The_shipped_release_workflow_names_match_what_the_updater_looks_for()
    {
        // installer/*.iss decide the file names; the updater must ask for exactly those.
        var root = RepoRoot();
        var client = await File.ReadAllTextAsync(Path.Combine(root, "installer", "Remotely.iss"));
        var server = await File.ReadAllTextAsync(Path.Combine(root, "installer", "Remotely-Server.iss"));
        Assert.Contains("OutputBaseFilename=Remotely-Setup-{#Version}-{#Arch}", client);
        Assert.Contains("OutputBaseFilename=Remotely-Server-Setup-{#Version}-x64", server);
        var v = ReleaseVersion.Parse("1.2.3");
        Assert.Equal("Remotely-Setup-1.2.3-arm64.exe", UpdateAssets.PackageName(UpdateProduct.Client, v, "arm64"));
        Assert.Equal("Remotely-Server-Setup-1.2.3-x64.exe", UpdateAssets.PackageName(UpdateProduct.Server, v, "x64"));
    }

    // ---- real Authenticode check (Windows only; the CI "installers" job runs this class on Windows) ----

    [Fact]
    public async Task Windows_reports_unsigned_signed_and_tampered_files_correctly()
    {
        if (!OperatingSystem.IsWindows()) return;   // nothing to check on Linux; the Windows job runs it for real
        var verifier = new PowerShellAuthenticodeVerifier();
        using var dir = new TempDir();

        var unsigned = Path.Combine(dir.Path, "plain.exe");
        await File.WriteAllBytesAsync(unsigned, Package("unsigned"));
        Assert.Equal(SignatureStatus.NotSigned, (await verifier.VerifyAsync(unsigned, default)).Status);

        // The test host itself (dotnet.exe) carries an embedded Microsoft signature.
        var signedFile = Environment.ProcessPath!;
        var signed = await verifier.VerifyAsync(signedFile, default);
        Assert.True(signed.Status == SignatureStatus.Valid, $"{signedFile}: {signed.Status} {signed.Detail}");
        Assert.Contains("Microsoft", signed.Signer);

        // A copy with one byte changed in the middle must not pass.
        var copy = Path.Combine(dir.Path, "copy.exe");
        File.Copy(signedFile, copy, true);
        var bytes = await File.ReadAllBytesAsync(copy);
        bytes[bytes.Length / 3] ^= 0xFF;
        await File.WriteAllBytesAsync(copy, bytes);
        var tampered = await verifier.VerifyAsync(copy, default);
        Assert.Equal(SignatureStatus.Invalid, tampered.Status);
    }

    [Fact]
    public async Task The_installers_built_by_CI_pass_the_whole_update_pipeline()
    {
        // Set by the CI "installers" job (Windows) to the folder with the real installers and the SHA256SUMS.txt that build\checksums.ps1 wrote. Skipped elsewhere.
        var dir = Environment.GetEnvironmentVariable("REMOTELY_PUBLISH_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        var version = ReleaseVersion.Parse(Environment.GetEnvironmentVariable("REMOTELY_VERSION") ?? "0.4.0");
        var sumsText = await File.ReadAllTextAsync(Path.Combine(dir, "SHA256SUMS.txt"));
        var sums = ReleaseFeed.ParseSums(sumsText);

        var wanted = new[]
        {
            (UpdateProduct.Client, "x64"),
            (UpdateProduct.Client, "arm64"),
            (UpdateProduct.Server, "x64"),
        };
        var files = wanted.ToDictionary(w => w, w => UpdateAssets.PackageName(w.Item1, version, w.Item2));
        foreach (var name in files.Values)
        {
            var actual = Sha(await File.ReadAllBytesAsync(Path.Combine(dir, name)));
            Assert.Equal(actual, sums[name]);   // the sums file written by the build lists every installer with its real hash
        }

        using var feed = new TestFeed();
        var first = files.Values.First();
        feed.AddRelease("v" + version, first, await File.ReadAllBytesAsync(Path.Combine(dir, first)), sumsText: sumsText,
            extra: files.Values.Skip(1).Select(n => (n, File.ReadAllBytes(Path.Combine(dir, n)))).ToArray());
        var checker = Checker(feed);
        var older = ReleaseVersion.Parse("0.0.1");
        using var work = new TempDir();
        foreach (var ((product, arch), name) in files)
        {
            var offer = (await checker.CheckAsync(older, product, arch)).Offer;
            Assert.NotNull(offer);
            Assert.Equal(name, offer!.AssetName);
            // The real Authenticode check (PowerShell on Windows). The installers are unsigned for now, so NotSigned is expected until a certificate exists.
            var real = new UpdateDownloader(new HttpClient(), Options(feed));
            var package = await real.DownloadAsync(offer, work.Path);
            Assert.Equal(sums[name], package.Sha256);
            Assert.Equal(OperatingSystem.IsWindows() ? SignatureStatus.NotSigned : SignatureStatus.NotChecked, package.Signature.Status);
        }

        // The same installer served with one flipped bit is refused.
        var victim = files.Values.First();
        var bad = await File.ReadAllBytesAsync(Path.Combine(dir, victim));
        bad[bad.Length / 2] ^= 0x01;
        feed.ReplacePackage(victim, bad);
        var offerAgain = (await checker.CheckAsync(older, UpdateProduct.Client, "x64")).Offer!;
        await Assert.ThrowsAsync<UpdateVerificationException>(() => new UpdateDownloader(new HttpClient(), Options(feed)).DownloadAsync(offerAgain, work.Path));
    }

    // ---- helpers ----

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remotely.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Remotely.slnx not found above " + AppContext.BaseDirectory);
    }

    private static byte[] Package(string seed, int size = 4096)
    {
        var bytes = new byte[size];
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        for (var i = 0; i < size; i++) bytes[i] = (byte)(key[i % key.Length] ^ (i * 31));
        return bytes;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static UpdateOptions Options(TestFeed feed, UpdateOptions? template = null) => new()
    {
        FeedUrl = feed.FeedUrl,
        TrustedDownloadPrefixes = [feed.Prefix],
        AllowLoopbackHttp = true,
        IncludePreReleases = template?.IncludePreReleases ?? false,
        MaxPackageBytes = template?.MaxPackageBytes ?? 400L * 1024 * 1024,
    };

    private static UpdateChecker Checker(TestFeed feed, UpdateOptions? template = null) => new(new HttpClient(), Options(feed, template));

    private static UpdateDownloader Downloader(TestFeed feed) => new(new HttpClient(), Options(feed), new FakeSignature(SignatureStatus.NotSigned));

    private static UpdateController Controller(TestFeed feed, TempDir dir, UpdateSettings settings, TimeProvider? clock = null) =>
        new(Checker(feed), Downloader(feed), settings, Path.Combine(dir.Path, "updates.json"), ReleaseVersion.Parse("0.4.0"), UpdateProduct.Client, "x64",
            Path.Combine(dir.Path, "downloads"), clock);

    private sealed class FakeSignature(SignatureStatus status) : IAuthenticodeVerifier
    {
        public Task<SignatureResult> VerifyAsync(string path, CancellationToken ct) =>
            Task.FromResult(new SignatureResult(status, status == SignatureStatus.Valid ? "CN=Test" : null, status == SignatureStatus.Invalid ? "test says invalid" : null));
    }

    private sealed class SyncProgress(Action<DownloadProgress> action) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => action(value);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "remotely-update-test-" + Guid.NewGuid().ToString("N")[..8]);
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A loopback HTTP server shaped like the GitHub releases API: <c>/feed</c> lists releases, <c>/dl/&lt;tag&gt;/&lt;file&gt;</c> serves assets.</summary>
    private sealed class TestFeed : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly List<Dictionary<string, object?>> _releases = [];
        private readonly Dictionary<string, (int Status, byte[] Body)> _routes = new();
        private int _requests;
        private int _feedRequests;

        public TestFeed()
        {
            var port = FreePort();
            Prefix = $"http://127.0.0.1:{port}/dl/";
            FeedUrl = $"http://127.0.0.1:{port}/feed";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public string Prefix { get; }
        public string FeedUrl { get; }
        public int Requests => Volatile.Read(ref _requests);
        public int FeedRequests => Volatile.Read(ref _feedRequests);

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        /// <summary>Adds a release with the package, its sums file and optional extra packages. Returns the package bytes.</summary>
        public byte[] AddRelease(string tag, string packageName, byte[] package, bool publishSums = true, string? sumsText = null, bool prerelease = false,
            string? packageUrlOverride = null, (string Name, byte[] Data)[]? extra = null)
        {
            var all = new List<(string Name, byte[] Data)> { (packageName, package) };
            if (extra is not null) all.AddRange(extra);
            var assets = new List<object>();
            foreach (var (name, data) in all)
            {
                Route($"/dl/{tag}/{name}", 200, data);
                assets.Add(new { name, browser_download_url = name == packageName && packageUrlOverride is not null ? packageUrlOverride : Prefix + $"{tag}/{name}", size = data.Length });
            }
            if (publishSums)
            {
                var text = sumsText ?? string.Concat(all.Select(a => $"{Sha(a.Data)}  {a.Name}\n"));
                Route($"/dl/{tag}/SHA256SUMS.txt", 200, Encoding.UTF8.GetBytes(text));
                assets.Add(new { name = "SHA256SUMS.txt", browser_download_url = Prefix + $"{tag}/SHA256SUMS.txt", size = text.Length });
            }
            lock (_gate)
            {
                _releases.Add(new Dictionary<string, object?>
                {
                    ["tag_name"] = tag, ["name"] = "Remotely " + tag, ["body"] = "Release notes for " + tag, ["html_url"] = "https://example.invalid/" + tag,
                    ["draft"] = false, ["prerelease"] = prerelease, ["assets"] = assets,
                });
                Route("/feed", 200, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_releases)));
            }
            return package;
        }

        /// <summary>Serves different bytes for an asset than the ones whose hash was published: what a tampered download looks like.</summary>
        public void ReplacePackage(string name, byte[] data)
        {
            lock (_gate)
            {
                var key = _routes.Keys.First(k => k.EndsWith("/" + name, StringComparison.Ordinal));
                _routes[key] = (200, data);
            }
        }

        public void Raw(string path, int status, string body) => Route(path, status, Encoding.UTF8.GetBytes(body));

        private void Route(string path, int status, byte[] body)
        {
            lock (_gate) _routes[path] = (status, body);
        }

        private async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                Interlocked.Increment(ref _requests);
                var path = ctx.Request.Url!.AbsolutePath;
                if (path == "/feed") Interlocked.Increment(ref _feedRequests);
                (int Status, byte[] Body) route;
                bool found;
                lock (_gate) found = _routes.TryGetValue(path, out route);
                try
                {
                    if (!found) { ctx.Response.StatusCode = 404; }
                    else
                    {
                        ctx.Response.StatusCode = route.Status;
                        ctx.Response.ContentLength64 = route.Body.Length;
                        await ctx.Response.OutputStream.WriteAsync(route.Body);
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { }
                finally { ctx.Response.Close(); }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }
}
