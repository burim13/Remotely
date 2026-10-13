using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Remotely.Host;
using Remotely.Host.Commands;
using Remotely.Host.Helper;
using Remotely.Host.Service;

const string Usage = """
    Remotely.Host (prototype)

      install                      copy to Program Files and register the auto-start service (run as administrator)
      uninstall                    remove the service (keeps settings, key and logs)
      setup --server wss://host:8443/ws --pin <server key pin> --code XXXX-XXXX --name "Dad's PC" [--trust <viewer id>] [--insecure]
      trust <viewer device id>     allow a viewer to connect
      diag [capture.jpg]           print what this PC allows; optionally grab one screen image
      service                      run as the service (started by Windows, not by hand)
      helper --pipe <name>         the capture helper (started by the service, not by hand)
    """;

switch (args.FirstOrDefault())
{
    case "service":
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Services.AddWindowsService(o => o.ServiceName = "Remotely.Host");
        builder.Services.AddSingleton(new FileLog("service"));
        builder.Services.AddHostedService<HostService>();
        await builder.Build().RunAsync();
        return 0;
    case "helper" when args.Length >= 3 && args[1] == "--pipe":
        return HelperProgram.Run(args[2], new FileLog("helper"));
    case "install": return Installer.Install();
    case "uninstall": return Installer.Uninstall();
    case "setup": return Installer.Setup(args[1..]);
    case "trust" when args.Length >= 2: return Installer.Trust(args[1]);
    case "diag": return Diagnostics.Run(args.Length >= 2 ? args[1] : null);
    default:
        Console.WriteLine(Usage);
        return 1;
}
