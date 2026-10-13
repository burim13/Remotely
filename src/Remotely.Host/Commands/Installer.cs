using System.Diagnostics;
using Remotely.HostCore;

namespace Remotely.Host.Commands;

/// <summary>Prototype install: copies this exe to Program Files and registers an auto-start LocalSystem service through sc.exe. The real installer (Inno Setup) replaces this later.</summary>
internal static class Installer
{
    private const string ServiceName = "Remotely.Host";

    public static int Install()
    {
        if (!RequireAdmin()) return 1;
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        Directory.CreateDirectory(HostPaths.ProgramDir);
        if (ServiceExists()) { Sc("stop", ServiceName); WaitStopped(); }
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(HostPaths.InstalledExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, HostPaths.InstalledExe, overwrite: true);

        var binPath = $"\"{HostPaths.InstalledExe}\" service";
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
        if (!RequireAdmin()) return 1;
        if (ServiceExists()) { Sc("stop", ServiceName); WaitStopped(); Sc("delete", ServiceName); }
        try { if (File.Exists(HostPaths.InstalledExe)) File.Delete(HostPaths.InstalledExe); } catch (IOException ex) { Console.WriteLine("Left the program file behind: " + ex.Message); }
        Console.WriteLine("Service removed. Settings, key and logs in " + HostPaths.DataDir + " were kept.");
        return 0;
    }

    public static int Setup(string[] args)
    {
        if (!RequireAdmin()) return 1;
        HostPaths.EnsureDataDir();
        var config = HostConfig.Load(HostPaths.ConfigFile);
        string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        config.ServerUri = Opt("--server") ?? config.ServerUri;
        config.ServerPin = Opt("--pin") ?? config.ServerPin;
        config.PairCode = Opt("--code") ?? config.PairCode;
        config.DeviceName = Opt("--name") ?? config.DeviceName;
        if (args.Contains("--insecure")) config.AllowInsecureTransport = true;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--trust" && !config.IsTrusted(args[i + 1])) config.TrustedViewers.Add(args[i + 1]);
        config.Save(HostPaths.ConfigFile);
        var identity = HostPaths.LoadOrCreateIdentity();
        Console.WriteLine($"Saved {HostPaths.ConfigFile}\nThis host's device id: {identity.DeviceId}\nTrusted viewers: {(config.TrustedViewers.Count == 0 ? "none (nobody can connect yet)" : string.Join(", ", config.TrustedViewers))}");
        return 0;
    }

    public static int Trust(string viewerId)
    {
        if (!RequireAdmin()) return 1;
        HostPaths.EnsureDataDir();
        var config = HostConfig.Load(HostPaths.ConfigFile);
        if (!config.IsTrusted(viewerId)) config.TrustedViewers.Add(viewerId);
        config.Save(HostPaths.ConfigFile);
        Console.WriteLine("Trusted viewers: " + string.Join(", ", config.TrustedViewers));
        return 0;
    }

    public static string ServiceState()
    {
        var (code, output) = Run("sc.exe", "query", ServiceName);
        if (code == 1060) return "not installed";
        return code != 0 ? $"unknown (sc exit {code})" : output.Split('\n').FirstOrDefault(l => l.Contains("STATE"))?.Trim() ?? "unknown";
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
        Console.WriteLine("This needs an elevated prompt: right-click Terminal, Run as administrator.");
        return false;
    }
}
