namespace Remotely.Unattended;

public enum ServiceState { NotInstalled, Stopped, Starting, Running, Stopping, Paused, Unknown }

/// <summary>Everything the page knows at one moment. <see cref="Link"/> is only meaningful while <see cref="Service"/> is <see cref="ServiceState.Running"/>.</summary>
public sealed record UnattendedView(ServiceState Service, PipeProblem? Link, UnattendedStatus? Status, bool? SasAllowsServices);

/// <summary>Chooses the one sentence (a key in Resources.resw plus its arguments) for the live status line. No wording here: the page looks the key up.</summary>
public static class StatusLine
{
    public sealed record Line(string Key, string[] Args);

    public static readonly IReadOnlyList<string> AllKeys =
    [
        "Unattended_Line_NotInstalled", "Unattended_Line_Stopped", "Unattended_Line_Starting", "Unattended_Line_Stopping", "Unattended_Line_Paused", "Unattended_Line_Unknown",
        "Unattended_Line_LinkUnreachable", "Unattended_Line_LinkDenied", "Unattended_Line_LinkUntrusted", "Unattended_Line_LinkBad",
        "Unattended_Line_Asking", "Unattended_Line_SessionLock", "Unattended_Line_SessionDesktop", "Unattended_Line_Session",
        "Unattended_Line_ViewingLock", "Unattended_Line_ViewingDesktop", "Unattended_Line_Listening", "Unattended_Line_NoHelper", "Unattended_Line_Ready",
    ];

    public static Line Describe(UnattendedView view)
    {
        switch (view.Service)
        {
            case ServiceState.NotInstalled: return new("Unattended_Line_NotInstalled", []);
            case ServiceState.Stopped: return new("Unattended_Line_Stopped", []);
            case ServiceState.Starting: return new("Unattended_Line_Starting", []);
            case ServiceState.Stopping: return new("Unattended_Line_Stopping", []);
            case ServiceState.Paused: return new("Unattended_Line_Paused", []);
            case ServiceState.Unknown: return new("Unattended_Line_Unknown", []);
        }
        if (view.Link is { } problem)
            return new(problem switch
            {
                PipeProblem.Unreachable => "Unattended_Line_LinkUnreachable",
                PipeProblem.AccessDenied => "Unattended_Line_LinkDenied",
                PipeProblem.UntrustedServer => "Unattended_Line_LinkUntrusted",
                _ => "Unattended_Line_LinkBad",
            }, []);
        if (view.Status is not { } s) return new("Unattended_Line_LinkUnreachable", []);

        if (s.Question is { } q) return new("Unattended_Line_Asking", [q.ControllerName]);
        if (s.Controller is { } who)
            return s.CaptureDesktop switch
            {
                "Winlogon" => new("Unattended_Line_SessionLock", [who]),
                "Default" => new("Unattended_Line_SessionDesktop", [who]),
                _ => new("Unattended_Line_Session", [who]),
            };
        // Someone is looking through the server, not through a token.
        if (s.CaptureDesktop == "Winlogon") return new("Unattended_Line_ViewingLock", []);
        if (s.CaptureDesktop == "Default") return new("Unattended_Line_ViewingDesktop", []);
        if (!s.HelperAttached) return new("Unattended_Line_NoHelper", []);
        if (s.Listening) return new("Unattended_Line_Listening", []);
        return new("Unattended_Line_Ready", []);
    }

    /// <summary>The time left on a token (never negative), for "changes in 7:42".</summary>
    public static TimeSpan Remaining(DateTimeOffset? expiresUtc, DateTimeOffset now) =>
        expiresUtc is { } e && e > now ? e - now : TimeSpan.Zero;
}
