using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remotely.Host;
using Remotely.Host.Commands;
using Remotely.Host.Helper;
using Remotely.Host.Service;

const string Usage = """
    Remotely.Host

      Two ways to run it. Add --user to any command for the no-administrator mode.
        service mode (default, run as administrator): starts at boot, survives sign-out, shows the sign-in/lock screen and UAC, can send Ctrl+Alt+Del
        user mode (--user): runs only while you are signed in, no service, no administrator rights

      install [--user]             copy to the install folder and start it (service: Program Files; user: your profile, starts at sign-in)
      uninstall [--user]           remove it (keeps settings, key and logs)
      setup [--user] --invite RMT1-... [--name "Dad's PC"] [--trusted-only]   join your server with the invite its 'info' command prints
      setup [--user] --server wss://host:8443/ws --pin <server key pin> --code XXXX-XXXX --name "Dad's PC" [--trust <viewer id>] [--view-only <viewer id>] [--insecure]   (long form, per-PC trust list)
      trust-all [--user] on|off    on: any device paired with your server may connect (the invite default); off: only listed viewers
      trust [--user] <viewer id> [--view-only]     allow a viewer (control, or watch only)
      untrust [--user] <viewer id>                 remove a viewer
      enable-sas                   allow the service to send Ctrl+Alt+Del (administrator)
      diag [capture.jpg]           print what this PC allows; optionally grab one screen image
      service | user | helper      run modes started by Windows / the installer, not by hand
    """;

var userFlag = args.Contains("--user");
if (userFlag || args.FirstOrDefault() == "user") HostPaths.UserMode = true;
var command = args.FirstOrDefault();
var rest = args.Skip(1).Where(a => a != "--user").ToArray();

switch (command)
{
    case "service":
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Services.AddWindowsService(o => o.ServiceName = "Remotely.Host");
        builder.Services.AddSingleton(new FileLog("service"));
        builder.Services.AddHostedService<HostService>();
        await builder.Build().RunAsync();
        return 0;
    case "user":
        return await UserModeRunner.RunAsync(new FileLog("user"), keepConsole: args.Contains("--console"));
    case "helper" when args.Length >= 3 && args[1] == "--pipe":
        return HelperProgram.Run(args[2], new FileLog("helper"));
    case "install": return Installer.Install();
    case "uninstall": return Installer.Uninstall();
    case "setup": return Installer.Setup(rest);
    case "trust" when rest.Length >= 1: return Installer.Trust(rest[0], rest.Contains("--view-only"));
    case "trust-all" when rest.Length >= 1 && rest[0] is "on" or "off": return Installer.TrustAll(rest[0] == "on");
    case "untrust" when rest.Length >= 1: return Installer.Untrust(rest[0]);
    case "enable-sas": return Installer.EnableSas();
    case "diag": return Diagnostics.Run(rest.Length >= 1 ? rest[0] : null);
    default:
        Console.WriteLine(Usage);
        return 1;
}
