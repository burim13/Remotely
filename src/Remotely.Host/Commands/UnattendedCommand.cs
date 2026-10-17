using System.IO.Pipes;
using System.Security.Principal;
using Remotely.Host.Unattended;
using Remotely.Unattended;

namespace Remotely.Host.Commands;

/// <summary>
/// <c>Remotely.Host.exe unattended ...</c>: the machine-wide choices for unattended access (the app runs these elevated, an administrator can run them by hand) and a small
/// client for the pipe, which is what scripts and CI use. See docs/unattended.md. Exit code 0 means done; anything else means it did not happen.
/// </summary>
internal static class UnattendedCommand
{
    public const string Usage = """
        unattended status                  print the settings, the Ctrl+Alt+Del policy, the firewall rules and the service state
        unattended lan on|off              (administrator) allow or forbid same-network token sharing through the service, with its firewall rules
        unattended ask on|off              (administrator) ask the person at the PC before a PC with a token is let in (default on)
        unattended keep-awake on|off       keep Windows awake while someone views this PC (default on; works with --user too)
        unattended sas on|off              (administrator) allow or forbid services to send Ctrl+Alt+Del (the Windows policy value SoftwareSASGeneration)
        unattended pipe status|start|stop|new-token|end-session
                                           talk to the running service over the local pipe as the signed-in user (needs the service running and a user signed in)
        service-start | service-stop       (administrator) start or stop the installed service
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine(Usage); return 1; }
        var on = args.Length >= 2 && args[1] is "on";
        var valid = args.Length >= 2 && args[1] is "on" or "off";
        var store = new UnattendedSettingsStore(HostPaths.UnattendedFile);
        switch (args[0])
        {
            case "status": return Status(store);
            case "lan" when valid: return Lan(store, on);
            case "ask" when valid: return Change(store, s => s.AskBeforeConnect = on, on ? "Asking before a PC with a token is let in." : "Not asking: a PC that knows the token is let in without a question.", serviceOnly: true);
            case "keep-awake" when valid: return Change(store, s => s.KeepAwake = on, on ? "Windows will be kept awake while someone views this PC." : "Windows may sleep while someone views this PC.", serviceOnly: false);
            case "sas" when valid: return Sas(on);
            case "pipe" when args.Length >= 2: return Pipe(args[1]).GetAwaiter().GetResult();
            default: Console.WriteLine(Usage); return 1;
        }
    }

    private static int Change(UnattendedSettingsStore store, Action<UnattendedSettings> change, string done, bool serviceOnly)
    {
        if (HostPaths.UserMode && serviceOnly) { Console.WriteLine("This setting belongs to the service; leave out --user."); return 1; }
        if (!HostPaths.UserMode && !Installer.RequireAdmin()) return 1;
        try
        {
            HostPaths.EnsureDataDir();
            store.Update(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("Could not save the setting: " + ex.Message);
            return 1;
        }
        Console.WriteLine(done);
        return 0;
    }

    private static int Lan(UnattendedSettingsStore store, bool on)
    {
        if (HostPaths.UserMode) { Console.WriteLine("Sharing through the service needs the service; leave out --user."); return 1; }
        if (!Installer.RequireAdmin()) return 1;
        if (on && !File.Exists(HostPaths.InstalledExe))
            Console.WriteLine($"Note: the service program is not at {HostPaths.InstalledExe} yet; the firewall rules name that path, so run 'install' too.");
        // Firewall first: turning sharing on without the rule would look enabled and silently not work; turning it off must remove the rule even if saving fails.
        if (on) { if (!LanFirewall.Add(HostPaths.InstalledExe, Console.Out)) return 1; }
        else LanFirewall.Remove();
        var code = Change(store, s => s.LanAllowed = on,
            on ? "Same-network sharing through the service is allowed. The signed-in user can now switch it on in the app." : "Same-network sharing through the service is forbidden and its firewall rules are removed. A running service stops listening within a few seconds.",
            serviceOnly: true);
        return code;
    }

    private static int Sas(bool on)
    {
        if (HostPaths.UserMode) { Console.WriteLine("Ctrl+Alt+Del from a service needs the service; leave out --user."); return 1; }
        if (!Installer.RequireAdmin()) return 1;
        if (on) SasRegistry.Allow(); else SasRegistry.Deny();
        Console.WriteLine(on ? "Services may now send Ctrl+Alt+Del on this PC." : "Services may no longer send Ctrl+Alt+Del on this PC (the policy value was removed).");
        return 0;
    }

    private static int Status(UnattendedSettingsStore store)
    {
        string Line(string name, object value) => $"{name}: {value}";
        Console.WriteLine(Line("service", Installer.ServiceState()));
        try
        {
            var s = store.Load();
            Console.WriteLine(Line("lan-allowed", s.LanAllowed));
            Console.WriteLine(Line("ask-before-connect", s.AskBeforeConnect));
            Console.WriteLine(Line("keep-awake", s.KeepAwake));
        }
        catch (UnauthorizedAccessException) { Console.WriteLine("settings: not readable by this user (run as administrator)"); }
        var sas = SasRegistry.Read();
        Console.WriteLine(Line("sas-value", sas?.ToString() ?? "not set"));
        Console.WriteLine(Line("sas-services-may-send", SasPolicy.ServicesMaySend(sas)));
        Console.WriteLine(Line("firewall-rules", LanFirewall.Exists()));
        return 0;
    }

    private static async Task<int> Pipe(string command)
    {
        if (!PipeCommands.All.Contains(command) || command == PipeCommands.Answer) { Console.WriteLine(Usage); return 1; }
        using var client = new UnattendedPipeClient(CreateClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var reply = await client.CallAsync(new PipeRequest { Cmd = command }, cts.Token).ConfigureAwait(false);
            Console.WriteLine($"ok: {reply.Ok}");
            if (reply.Error is not null) Console.WriteLine($"error: {reply.Error}");
            if (reply.Status is { } s)
            {
                Console.WriteLine($"lan-allowed: {s.LanAllowed}");
                Console.WriteLine($"listening: {s.Listening}");
                if (s.Token is not null) Console.WriteLine($"token: {s.Token}");
                if (s.Listening) Console.WriteLine($"port: {s.Port}");
                Console.WriteLine($"helper: {s.HelperAttached}");
                Console.WriteLine($"capture-desktop: {s.CaptureDesktop ?? "none"}");
                Console.WriteLine($"controller: {s.Controller ?? "none"}");
                Console.WriteLine($"awake-held: {s.AwakeHeld}");
            }
            return reply.Ok ? 0 : 1;
        }
        catch (UnattendedPipeException ex)
        {
            Console.WriteLine($"pipe: {ex.Problem} ({ex.Message})");
            return 1;
        }
    }

    /// <summary>The client end with the narrowest rights that work (no right to create pipe instances) and "identification" level, so the service can read who this is but not act as them.</summary>
    internal static NamedPipeClientStream CreateClient() => new(".", UnattendedPipe.Name, PipeAccessRights.ReadData | PipeAccessRights.WriteData, PipeOptions.Asynchronous,
        TokenImpersonationLevel.Identification, System.IO.HandleInheritability.None);
}
