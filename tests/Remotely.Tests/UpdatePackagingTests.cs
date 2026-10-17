using System.Text.Json;
using System.Text.RegularExpressions;
using Remotely.Core.Update;

namespace Remotely.Tests;

/// <summary>Static checks that the installers, the winget and scoop manifests, the build scripts and docs/updates.md agree with each other (none of them can be run on Linux).</summary>
public partial class UpdatePackagingTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remotely.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Remotely.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string DefineVersion(string iss) => DefaultVersion().Match(iss).Groups[1].Value;

    [GeneratedRegex(@"#define Version ""([^""]+)""")]
    private static partial Regex DefaultVersion();

    [GeneratedRegex(@"AppId=\{\{([0-9A-F-]{36})\}")]
    private static partial Regex AppIdText();

    [Fact]
    public void Both_installers_and_the_scripts_default_to_the_same_version()
    {
        var client = DefineVersion(Read("installer", "Remotely.iss"));
        var server = DefineVersion(Read("installer", "Remotely-Server.iss"));
        Assert.Equal(client, server);
        Assert.Contains($"[string]$Version = \"{client}\"", Read("build", "publish.ps1"));
        Assert.True(ReleaseVersion.TryParse(client, out _));
    }

    [Fact]
    public void The_winget_manifests_describe_one_version_and_the_installer_names_the_build_produces()
    {
        var version = DefineVersion(Read("installer", "Remotely.iss"));
        foreach (var (id, product, arches, iss) in new[]
        {
            ("Burim13.Remotely", UpdateProduct.Client, new[] { "x64", "arm64" }, "Remotely.iss"),
            ("Burim13.RemotelyServer", UpdateProduct.Server, new[] { "x64" }, "Remotely-Server.iss"),
        })
        {
            var folder = Path.Combine(Root(), "packaging", "winget", id);
            var files = Directory.GetFiles(folder, "*.yaml");
            Assert.Equal(3, files.Length);   // version, installer, default locale
            foreach (var f in files)
            {
                var text = File.ReadAllText(f);
                Assert.DoesNotContain('\t', text);
                Assert.Contains($"PackageIdentifier: {id}\n", text.Replace("\r\n", "\n"));
                Assert.Contains($"PackageVersion: {version}", text);
            }
            var installer = File.ReadAllText(files.Single(f => f.EndsWith(".installer.yaml")));
            Assert.Contains("InstallerType: inno", installer);
            Assert.Contains("/VERYSILENT", installer);
            Assert.Contains("/SUPPRESSMSGBOXES", installer);
            var appId = AppIdText().Match(Read("installer", iss)).Groups[1].Value;
            Assert.Contains($"ProductCode: '{{{appId}}}_is1'", installer);
            foreach (var arch in arches)
            {
                var name = UpdateAssets.PackageName(product, ReleaseVersion.Parse(version), arch);
                Assert.Contains($"https://github.com/burim13/Remotely/releases/download/v{version}/{name}", installer);
            }
            // Until build/update-manifests.ps1 has filled it in from SHA256SUMS.txt, a hash is an obvious placeholder (never a made-up hash that looks real).
            foreach (Match m in Regex.Matches(installer, @"InstallerSha256: (\S+)"))
                Assert.Matches(@"^(REPLACE_WITH_SHA256_FROM_SHA256SUMS_TXT|[0-9a-f]{64})$", m.Groups[1].Value);
            Assert.Equal(arches.Length, Regex.Matches(installer, "InstallerSha256:").Count);
        }
    }

    [Fact]
    public void The_scoop_manifest_is_valid_json_and_matches_the_installer_names()
    {
        var version = DefineVersion(Read("installer", "Remotely.iss"));
        using var doc = JsonDocument.Parse(Read("packaging", "scoop", "remotely.json"));
        var root = doc.RootElement;
        Assert.Equal(version, root.GetProperty("version").GetString());
        Assert.Equal("MIT", root.GetProperty("license").GetString());
        var arch = root.GetProperty("architecture");
        foreach (var (key, name) in new[] { ("64bit", "x64"), ("arm64", "arm64") })
        {
            var entry = arch.GetProperty(key);
            Assert.Equal($"https://github.com/burim13/Remotely/releases/download/v{version}/{UpdateAssets.PackageName(UpdateProduct.Client, ReleaseVersion.Parse(version), name)}", entry.GetProperty("url").GetString());
            Assert.Matches(@"^(REPLACE_WITH_SHA256_FROM_SHA256SUMS_TXT|[0-9a-f]{64})$", entry.GetProperty("hash").GetString()!);
        }
        Assert.Contains("/CURRENTUSER", root.GetProperty("installer").GetProperty("script").EnumerateArray().Select(e => e.GetString()).Aggregate((a, b) => a + b));
    }

    [Fact]
    public void The_server_installer_carries_the_server_manager_and_the_service()
    {
        var iss = Read("installer", "Remotely-Server.iss");
        Assert.Contains(@"Source: ""{#PublishManager}\*""; DestDir: ""{app}\Manager""", iss);
        Assert.Contains(@"{app}\Manager\Remotely.ServerManager.exe", iss);
        Assert.Contains("'install'", iss);   // the service is installed by Remotely.Server.exe install
        // The Manager looks for the server in %ProgramFiles%\Remotely Server (ServerExeLocator): the Manager folder must sit inside the install folder.
        Assert.Contains(@"DefaultDirName={autopf}\Remotely Server", iss);
        Assert.Contains("Remotely.Server.exe", Read("src", "Remotely.Core", "ServerManager", "ServiceControl.cs"));
        var publish = Read("build", "publish.ps1");
        Assert.Contains(@"-o publish\manager", publish);
        var workflow = Read(".github", "workflows", "build.yml");
        Assert.Contains("name: Remotely.ServerManager-win-x64, path: publish/manager", workflow);
    }

    [Fact]
    public void The_client_installer_has_no_required_questions_and_documents_its_switches()
    {
        var iss = Read("installer", "Remotely.iss");
        Assert.Contains("Flags: unchecked", iss.Split('\n').Single(l => l.StartsWith("Name: \"startup\"")));
        Assert.Contains("Flags: unchecked", iss.Split('\n').Single(l => l.StartsWith("Name: \"connect\"")));
        Assert.Contains("if PageID = wpSelectComponents then Result := True;", iss);
        // The value the installer writes is the one the app's own setting writes (StartupCommand.Build).
        Assert.Contains(@"--minimized", iss);
        Assert.Contains("/v Remotely /t REG_SZ", iss);
        var docs = Read("docs", "updates.md");
        foreach (var switchName in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER", "/ALLUSERS", "/TASKS=startup", "/Invite=", "/COMPONENTS=viewer", "/Address=", "/LOG=", "/SimulateFailure=1" })
            Assert.Contains(switchName, docs);
    }

    [Fact]
    public void Every_resource_key_of_the_update_screens_has_english_text_and_the_apps_use_only_known_keys()
    {
        foreach (var (key, text) in UpdatePresentation.EnglishText) Assert.False(string.IsNullOrWhiteSpace(text), key);
        var known = UpdatePresentation.EnglishText.Keys.ToHashSet();
        foreach (var dir in new[] { Path.Combine(Root(), "src", "Remotely.App", "Update"), Path.Combine(Root(), "src", "Remotely.ServerManager", "Update") })
            foreach (var file in Directory.GetFiles(dir, "*.cs"))
                foreach (Match m in Regex.Matches(File.ReadAllText(file), @"""(Update_[A-Za-z_]+)"""))
                    Assert.Contains(m.Groups[1].Value, known);
        foreach (var line in new[]
        {
            UpdatePresentation.Status(new UpdateState(UpdatePhase.Available, new UpdateOffer(ReleaseVersion.Parse("1.0.0"), "v1.0.0", "", "", "x.exe", "https://x", 1, new string('a', 64))), ReleaseVersion.Parse("0.9.0")),
            UpdatePresentation.Status(new UpdateState(UpdatePhase.Failed, Detail: "boom"), ReleaseVersion.Parse("0.9.0")),
        })
        {
            Assert.Contains(line.Key, known);
            Assert.DoesNotContain("{0}", UpdatePresentation.English(line));
        }
    }

    [Fact]
    public void The_apps_wire_up_the_update_screens_only_through_the_documented_hooks()
    {
        // Update checks must stay opt-in: nothing in the apps may call the network directly except through UpdateController, and the controller only checks on its own when enabled.
        foreach (var dir in new[] { Path.Combine(Root(), "src", "Remotely.App", "Update"), Path.Combine(Root(), "src", "Remotely.ServerManager", "Update") })
            foreach (var file in Directory.GetFiles(dir, "*.cs"))
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("GetStringAsync", text);
                Assert.DoesNotContain("AllowLoopbackHttp", text);
                Assert.DoesNotContain("AutoRun", text);
            }
        Assert.False(new UpdateSettings().Enabled);
    }
}
