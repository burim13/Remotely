using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;
using Remotely.Core.Protocol;

namespace Remotely.Server;

/// <summary>Command-line side of the server: install it as a Windows service, show the key pin, hand out pairing codes. Run on the server PC itself.</summary>
internal static class ServerCommands
{
    private const string ServiceName = "Remotely.Server";

    public const string Usage = """
        Remotely.Server

          (no command)     run the server in this window (Ctrl+C to stop)
          install          install and start it as a Windows service, and open its port in Windows Firewall (administrator)
          uninstall        stop and remove the service and the firewall rule (keeps your data)
          pin              print the server key pin that devices must be given
          invite [--address host]   print an invite to paste into Remotely on each new device (valid 1 hour, up to 10 devices)
          set-address <host>        remember the name or address devices use to reach this server (for example myhome.example.com)
          pair-code        print a one-time code to add a new device (the server must be running)
          devices          list paired devices
          remove <id>      forget a device
          info [--pause]   show a fresh invite, and the address and key pin it contains
        """;

    public static ServerOptions LoadOptions()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();
        var options = new ServerOptions();
        config.GetSection(ServerOptions.Section).Bind(options);
        return options;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var options = LoadOptions();
        switch (args[0])
        {
            case "install": return Install(options);
            case "uninstall": return Uninstall(options);
            case "pin": Console.WriteLine(ServerCertificate.Pin(ServerCertificate.LoadOrCreate(options.ResolveDataDir()))); return 0;
            case "pair-code": return await PairCodeAsync(options, quiet: false);
            case "invite": return await InviteAsync(options, ArgValue(args, "--address"), copy: false, pause: false, details: false);
            case "set-address" when args.Length >= 2: return SetAddress(options, args[1]);
            case "devices": return await DevicesAsync(options);
            case "remove" when args.Length >= 2: return await RemoveAsync(options, args[1]);
            case "info": return await InviteAsync(options, ArgValue(args, "--address"), copy: true, pause: args.Contains("--pause"), details: true);
            default:
                Console.WriteLine(Usage);
                return 1;
        }
    }

    private static string ProgramDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Remotely Server");
    private static string InstalledExe => Path.Combine(ProgramDir, "Remotely.Server.exe");

    private static int Install(ServerOptions options)
    {
        if (!RequireAdmin()) return 1;
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        if (Service("query").Code != 1060) { Service("stop"); WaitStopped(); }

        Directory.CreateDirectory(ProgramDir);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, InstalledExe, overwrite: true);
            var settings = Path.Combine(Path.GetDirectoryName(source)!, "appsettings.json");
            if (File.Exists(settings)) File.Copy(settings, Path.Combine(ProgramDir, "appsettings.json"), overwrite: true);
        }

        // The service runs as LocalService (no administrator rights, no password). Give it its own data folder.
        var data = options.ResolveDataDir();
        Directory.CreateDirectory(data);
        Run("icacls.exe", data, "/grant", @"NT AUTHORITY\LocalService:(OI)(CI)M", "/T", "/Q");
        // Load or create the certificate now, as administrator, so the pin is known before the first start.
        ServerCertificate.LoadOrCreate(data);
        Run("icacls.exe", data, "/grant", @"NT AUTHORITY\LocalService:(OI)(CI)M", "/T", "/Q");

        var binPath = $"\"{InstalledExe}\"";
        var code = Service("query").Code == 1060
            ? Service("create", "binPath=", binPath, "start=", "auto", "obj=", @"NT AUTHORITY\LocalService", "DisplayName=", "Remotely Server").Code
            : Service("config", "binPath=", binPath, "start=", "auto", "obj=", @"NT AUTHORITY\LocalService").Code;
        if (code != 0) return code;
        Service("description", "Your own Remotely coordinator: introduces your devices and relays their encrypted connections.");
        Service("failure", "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/5000");

        Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=Remotely Server");
        Run("netsh.exe", "advfirewall", "firewall", "add", "rule", "name=Remotely Server", "dir=in", "action=allow", "protocol=TCP", $"localport={options.PublicPort}");

        var started = Service("start").Code;
        Console.WriteLine(started == 0 ? $"Installed and started. Listening on port {options.PublicPort}. Data: {data}" : "Installed, but the service did not start; check Event Viewer.");
        return started;
    }

    private static int Uninstall(ServerOptions options)
    {
        if (!RequireAdmin()) return 1;
        if (Service("query").Code != 1060) { Service("stop"); WaitStopped(); Service("delete"); }
        Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", "name=Remotely Server");
        try { if (File.Exists(InstalledExe)) File.Delete(InstalledExe); } catch (IOException ex) { Console.WriteLine("Left the program file behind: " + ex.Message); }
        Console.WriteLine($"Removed. Your devices and key are kept in {options.ResolveDataDir()}.");
        return 0;
    }

    private static async Task<int> PairCodeAsync(ServerOptions options, bool quiet)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                var response = await http.PostAsync($"http://127.0.0.1:{options.AdminPort}/admin/pair-codes", null);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
                Console.WriteLine(quiet ? "  Pairing code: " + body!["code"] : body!["code"].GetString());
                if (!quiet) Console.Error.WriteLine("This code works once and expires in 15 minutes.");
                return 0;
            }
            catch (HttpRequestException) { await Task.Delay(1000); }   // the service may still be starting
            catch (TaskCanceledException) { await Task.Delay(1000); }
        }
        Console.Error.WriteLine("The server is not running. Start it (or run 'Remotely.Server.exe' in this window) and try again.");
        return 2;
    }

    private static async Task<int> DevicesAsync(ServerOptions options)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var list = await http.GetFromJsonAsync<List<Dictionary<string, System.Text.Json.JsonElement>>>($"http://127.0.0.1:{options.AdminPort}/admin/devices") ?? [];
            if (list.Count == 0) Console.WriteLine("No devices are paired yet.");
            foreach (var d in list)
                Console.WriteLine($"{(d["online"].GetBoolean() ? "online " : "offline")}  {(d["canHost"].GetBoolean() ? "host  " : "viewer")}  {d["name"].GetString(),-24} {d["deviceId"].GetString()}");
            return 0;
        }
        catch (HttpRequestException) { Console.Error.WriteLine("The server is not running."); return 2; }
    }

    private static async Task<int> RemoveAsync(ServerOptions options, string id)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var response = await http.DeleteAsync($"http://127.0.0.1:{options.AdminPort}/admin/devices/{Uri.EscapeDataString(id)}");
            Console.WriteLine(response.IsSuccessStatusCode ? "Removed. That device can no longer connect." : "No such device.");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException) { Console.Error.WriteLine("The server is not running."); return 2; }
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string AddressFile(ServerOptions options) => Path.Combine(options.ResolveDataDir(), "address.txt");

    private static int SetAddress(ServerOptions options, string address)
    {
        try
        {
            Directory.CreateDirectory(options.ResolveDataDir());
            File.WriteAllText(AddressFile(options), address.Trim());
            Console.WriteLine($"Devices will now be told to connect to: {BuildUri(options, address)}");
            return 0;
        }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine("This needs an elevated prompt (run as administrator)."); return 1; }
    }

    /// <summary>"myhome.example.com", "myhome.example.com:9000" or a full wss:// address become the address devices connect to.</summary>
    internal static string BuildUri(ServerOptions options, string address)
    {
        address = address.Trim();
        if (address.Contains("://", StringComparison.Ordinal)) return address;
        var scheme = options.TlsEnabled ? "wss" : "ws";
        var hasPort = address.Contains(':') && !address.Contains("::") && address.Count(c => c == ':') == 1;
        return $"{scheme}://{(hasPort ? address : $"{address}:{options.PublicPort}")}/ws";
    }

    /// <summary>The address to put in an invite: the one given, else the one saved at install, else this PC's home-network address.</summary>
    private static (string? Uri, string Source) ResolveAddress(ServerOptions options, string? given)
    {
        if (!string.IsNullOrWhiteSpace(given)) return (BuildUri(options, given), "given");
        try
        {
            if (File.Exists(AddressFile(options)) && File.ReadAllText(AddressFile(options)).Trim() is { Length: > 0 } saved) return (BuildUri(options, saved), "saved");
        }
        catch (IOException) { }
        var lan = LocalNetworkAddress();
        return lan is null ? (null, "none") : (BuildUri(options, lan), "automatic");
    }

    private static string? LocalNetworkAddress()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                                     && n.NetworkInterfaceType is not (System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel))
                         .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any))))
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(u.Address)
                        && !u.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        return u.Address.ToString();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return null;
    }

    private static async Task<string?> RequestCodeAsync(ServerOptions options, int uses, int minutes)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                var response = await http.PostAsync($"http://127.0.0.1:{options.AdminPort}/admin/pair-codes?uses={uses}&minutes={minutes}", null);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
                return body!["code"].GetString();
            }
            catch (HttpRequestException) { await Task.Delay(1000); }   // the service may still be starting
            catch (TaskCanceledException) { await Task.Delay(1000); }
        }
        return null;
    }

    private static async Task<int> InviteAsync(ServerOptions options, string? address, bool copy, bool pause, bool details)
    {
        var pin = options.TlsEnabled ? ServerCertificate.Pin(ServerCertificate.LoadOrCreate(options.ResolveDataDir())) : "";
        var (uri, source) = ResolveAddress(options, address);
        if (uri is null)
        {
            Console.Error.WriteLine("Could not work out this server's address. Run:  Remotely.Server.exe set-address <name or IP address>");
            return 3;
        }
        var code = await RequestCodeAsync(options, uses: 10, minutes: 60);
        if (code is null)
        {
            Console.Error.WriteLine("The server is not running. Start it (or run 'Remotely.Server.exe' in this window) and try again.");
            if (pause) { Console.WriteLine(); Console.Write("Press Enter to close."); Console.ReadLine(); }
            return 2;
        }
        var invite = new Invite(uri, pin, code).Encode();
        Console.WriteLine("Remotely server");
        Console.WriteLine();
        Console.WriteLine("  Invite for a new device (paste it into Remotely on that device):");
        Console.WriteLine();
        Console.WriteLine("  " + invite);
        Console.WriteLine();
        Console.WriteLine("  It works for 1 hour and up to 10 devices. Run this again for a fresh one.");
        if (copy && CopyToClipboard(invite)) Console.WriteLine("  (It has been copied to the clipboard.)");
        Console.WriteLine();
        Console.WriteLine($"  Devices will connect to {uri}" + (source == "automatic" ? "  (this PC's home-network address)" : ""));
        if (source == "automatic")
            Console.WriteLine("  That works at home. To reach the server from outside, forward TCP port " + options.PublicPort + " on your router and run:  Remotely.Server.exe set-address <your public name>");
        else
            Console.WriteLine("  Away from home this needs TCP port " + options.PublicPort + " forwarded on your router to this PC.");
        if (details && options.TlsEnabled)
        {
            Console.WriteLine();
            Console.WriteLine($"  (Details inside the invite: server key pin {pin})");
        }
        if (pause) { Console.WriteLine(); Console.Write("Press Enter to close."); Console.ReadLine(); }
        return 0;
    }

    private static bool CopyToClipboard(string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var info = new ProcessStartInfo("clip.exe") { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(info)!;
            process.StandardInput.Write(text);
            process.StandardInput.Close();
            return process.WaitForExit(3000) && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return false; }
    }

    private static (int Code, string Output) Service(string verb, params string[] rest) => Run("sc.exe", [verb, ServiceName, .. rest]);

    private static void WaitStopped()
    {
        for (var i = 0; i < 20; i++)
        {
            var (_, output) = Service("query");
            if (!output.Contains("RUNNING") && !output.Contains("STOP_PENDING")) return;
            Thread.Sleep(500);
        }
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
        if (OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        Console.WriteLine("This needs an elevated prompt: right-click Terminal, Run as administrator.");
        return false;
    }
}
