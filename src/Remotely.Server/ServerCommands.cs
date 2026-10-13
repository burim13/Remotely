using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Principal;
using Microsoft.Extensions.Configuration;

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
          pair-code        print a one-time code to add a new device (the server must be running)
          devices          list paired devices
          remove <id>      forget a device
          info [--pause]   show the address, key pin and a fresh pairing code together
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
            case "devices": return await DevicesAsync(options);
            case "remove" when args.Length >= 2: return await RemoveAsync(options, args[1]);
            case "info": return await InfoAsync(options, pause: args.Contains("--pause"));
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

    private static async Task<int> InfoAsync(ServerOptions options, bool pause)
    {
        var pin = ServerCertificate.Pin(ServerCertificate.LoadOrCreate(options.ResolveDataDir()));
        Console.WriteLine("Remotely server");
        Console.WriteLine();
        Console.WriteLine($"  Port:           {options.PublicPort}  (forward this TCP port on your router to this PC)");
        Console.WriteLine($"  Address to use: wss://<your address>:{options.PublicPort}/ws");
        Console.WriteLine($"  Server key pin: {pin}");
        var code = await PairCodeAsync(options, quiet: true);
        Console.WriteLine();
        Console.WriteLine("Enter the address, the key pin and the pairing code in Remotely on each device.");
        if (pause) { Console.WriteLine(); Console.Write("Press Enter to close."); Console.ReadLine(); }
        return code;
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
