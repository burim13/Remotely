using Remotely.Core.Protocol;
using System.Diagnostics;
using Microsoft.Win32;
using Remotely.Host.Service;
using Remotely.HostCore;

namespace Remotely.Host.Commands;

/// <summary>
/// Install, uninstall and configure. Service mode (default) copies this exe to Program Files and registers an auto-start LocalSystem service through
/// sc.exe; user mode (--user) copies it into the user's profile and starts it at sign-in through a Run key. The Inno Setup installers call these same commands.
/// </summary>
internal static class Installer
{
    private const string ServiceName = "Remotely.Host";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static int Install()
    {
        if (!HostPaths.UserMode && !RequireAdmin()) return 1;
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        var target = HostPaths.InstalledExe;
        Directory.CreateDirectory(HostPaths.ProgramDir);

        if (HostPaths.UserMode)
        {
            UserModeRunner.RequestStop();
            Thread.Sleep(800);
            CopyIfDifferent(source, target);
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) key.SetValue(UserModeRunner.RunKeyName, $"\"{target}\" user");
            HostPaths.EnsureDataDir();
            Process.Start(new ProcessStartInfo(target, "user") { UseShellExecute = false, CreateNoWindow = true });
            Console.WriteLine("Installed for this user and started. It starts again each time you sign in. Log: " + HostPaths.LogDir);
            return 0;
        }

        if (ServiceExists()) { Sc("stop", ServiceName); WaitStopped(); }
        CopyIfDifferent(source, target);
        var binPath = $"\"{target}\" service";
        var code = ServiceExists()
            ? Sc("config", ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem")
            : Sc("create", ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "Remotely Host");
        if (code != 0) return code;
        Sc("description", ServiceName, "Lets your other devices view and control this PC through your own Remotely server.");
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000");
        HostPaths.EnsureDataDir();
        code = Sc("start", ServiceName);
        Console.WriteLine(code == 0 ? "Installed and started. Log: " + HostPaths.LogDir : "Installed, but it did not start; check 'sc query Remotely.Host' and the log.");
        return code;
    }

    public static int Uninstall()
    {
        if (HostPaths.UserMode)
        {
            UserModeRunner.RequestStop();
            Thread.Sleep(1000);
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)) key?.DeleteValue(UserModeRunner.RunKeyName, throwOnMissingValue: false);
            TryDeleteProgram();
            Console.WriteLine("User-mode host removed. Settings, key and logs in " + HostPaths.DataDir + " were kept.");
            return 0;
        }
        if (!RequireAdmin()) return 1;
        if (ServiceExists()) { Sc("stop", ServiceName); WaitStopped(); Sc("delete", ServiceName); }
        TryDeleteProgram();
        Console.WriteLine("Service removed. Settings, key and logs in " + HostPaths.DataDir + " were kept.");
        return 0;
    }

    public static int Setup(string[] args)
    {
        if (!HostPaths.UserMode && !RequireAdmin()) return 1;
        HostPaths.EnsureDataDir();
        var config = HostConfig.Load(HostPaths.ConfigFile);
        string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Opt("--invite") is { } inviteText)
        {
            if (!Invite.TryParse(inviteText, out var invite, out var problem)) { Console.WriteLine(problem); return 1; }
            config.ServerUri = invite!.ServerUri;
            config.ServerPin = invite.PinHex;
            config.PairCode = invite.Code;
            config.AllowInsecureTransport = invite.ServerUri.StartsWith("ws://", StringComparison.OrdinalIgnoreCase);
            // Joining with an invite is the family set-up: any device paired with your server may connect. --trusted-only keeps a per-PC list instead.
            config.TrustAllPaired = !args.Contains("--trusted-only");
        }
        config.ServerUri = Opt("--server") ?? config.ServerUri;
        config.ServerPin = Opt("--pin") ?? config.ServerPin;
        config.PairCode = Opt("--code") ?? config.PairCode;
        config.DeviceName = Opt("--name") ?? config.DeviceName;
        if (args.Contains("--insecure")) config.AllowInsecureTransport = true;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--trust" && !config.TrustedViewers.Contains(args[i + 1], StringComparer.OrdinalIgnoreCase)) config.TrustedViewers.Add(args[i + 1]);
            if (args[i] == "--view-only" && !config.ViewOnlyViewers.Contains(args[i + 1], StringComparer.OrdinalIgnoreCase)) config.ViewOnlyViewers.Add(args[i + 1]);
        }
        config.Save(HostPaths.ConfigFile);
        var identity = HostPaths.LoadOrCreateIdentity();
        Console.WriteLine($"Saved {HostPaths.ConfigFile}\nThis host's device id: {identity.DeviceId}\nAny device paired with your server may connect: {(config.TrustAllPaired ? "yes" : "no, only the lists below")}\nTrusted viewers (control): {Describe(config.TrustedViewers)}\nView-only viewers: {Describe(config.ViewOnlyViewers)}");
        return 0;
    }

    /// <summary>trust-all on|off: switch between "any device paired with my server" and the per-PC list of viewer ids.</summary>
    public static int TrustAll(bool on)
    {
        if (!HostPaths.UserMode && !RequireAdmin()) return 1;
        HostPaths.EnsureDataDir();
        var config = HostConfig.Load(HostPaths.ConfigFile);
        config.TrustAllPaired = on;
        config.Save(HostPaths.ConfigFile);
        Console.WriteLine(on ? "Any device paired with your server may now connect to this PC." : "Only the viewers on this PC's trust list may connect now.");
        return 0;
    }

    public static int Trust(string viewerId, bool viewOnly)
    {
        if (!HostPaths.UserMode && !RequireAdmin()) return 1;
        HostPaths.EnsureDataDir();
        var config = HostConfig.Load(HostPaths.ConfigFile);
        config.TrustedViewers.RemoveAll(v => string.Equals(v, viewerId, StringComparison.OrdinalIgnoreCase));
        config.ViewOnlyViewers.RemoveAll(v => string.Equals(v, viewerId, StringComparison.OrdinalIgnoreCase));
        (viewOnly ? config.ViewOnlyViewers : config.TrustedViewers).Add(viewerId);
        config.Save(HostPaths.ConfigFile);
        Console.WriteLine($"Trusted viewers (control): {Describe(config.TrustedViewers)}\nView-only viewers: {Describe(config.ViewOnlyViewers)}");
        return 0;
    }

    public static int Untrust(string viewerId)
    {
        if (!HostPaths.UserMode && !RequireAdmin()) return 1;
        var config = HostConfig.Load(HostPaths.ConfigFile);
        var removed = config.TrustedViewers.RemoveAll(v => string.Equals(v, viewerId, StringComparison.OrdinalIgnoreCase))
                      + config.ViewOnlyViewers.RemoveAll(v => string.Equals(v, viewerId, StringComparison.OrdinalIgnoreCase));
        config.Save(HostPaths.ConfigFile);
        Console.WriteLine(removed > 0 ? "Removed. That viewer can no longer connect." : "That viewer was not in the list.");
        return 0;
    }

    /// <summary>Lets the service send Ctrl+Alt+Del (Group Policy "Disable or enable software Secure Attention Sequence" = Services).</summary>
    public static int EnableSas()
    {
        if (!RequireAdmin()) return 1;
        using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        key.SetValue("SoftwareSASGeneration", 1, RegistryValueKind.DWord);
        Console.WriteLine("Services may now send Ctrl+Alt+Del on this PC.");
        return 0;
    }

    public static string ServiceState()
    {
        var (code, output) = Run("sc.exe", "query", ServiceName);
        if (code == 1060) return "not installed";
        return code != 0 ? $"unknown (sc exit {code})" : output.Split('\n').FirstOrDefault(l => l.Contains("STATE"))?.Trim() ?? "unknown";
    }

    private static string Describe(List<string> ids) => ids.Count == 0 ? "none" : string.Join(", ", ids);

    private static void CopyIfDifferent(string source, string target)
    {
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, target, overwrite: true);
    }

    private static void TryDeleteProgram()
    {
        try { if (File.Exists(HostPaths.InstalledExe)) File.Delete(HostPaths.InstalledExe); }
        catch (IOException ex) { Console.WriteLine("Left the program file behind: " + ex.Message); }
    }

    private static bool ServiceExists() => Run("sc.exe", "query", ServiceName).Code != 1060;

    private static void WaitStopped()
    {
        for (var i = 0; i < 20; i++)
        {
            var state = ServiceState();
            if (!state.Contains("RUNNING") && !state.Contains("STOP_PENDING")) return;
            Thread.Sleep(500);
        }
    }

    private static int Sc(params string[] args)
    {
        var (code, output) = Run("sc.exe", args);
        if (code != 0) Console.WriteLine($"sc {args[0]} failed (exit {code}): {output.Trim()}");
        return code;
    }

    private static (int Code, string Output) Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private static bool RequireAdmin()
    {
        if (HostPaths.IsAdministrator()) return true;
        Console.WriteLine("This needs an elevated prompt: right-click Terminal, Run as administrator. (Or add --user to install without administrator rights.)");
        return false;
    }
}
