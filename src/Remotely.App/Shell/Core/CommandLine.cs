namespace Remotely.App.Shell;

/// <summary>
/// Command-line options of Remotely.exe (documented in docs/cli.md):
///   --invite RMT1-...        join the server in the invite, then show My computers (the installer uses this)
///   --pair CODE              pair with the already configured server
///   --connect-token TOKEN    open the This network page with the token filled in
///   --minimized              start in the tray without showing the window
///   --show PageName          open the app on that Page class (CI uses this; never forwards to a running instance)
///   --uia-scan RESULT [PAGES]   open every page named in PAGES (default: tools/ci/pages.txt format, one Page class per line; without it every page class of the app), list interactive elements Narrator would read without a name into RESULT, exit 0 when there are none (A15)
///   --show-result FILE       with --show: where to write the one-line result (default: show-result.txt next to the exe)
///   --selftest-shell FILE    start a second copy of this program with --invite and check that a first instance receives it (CI)
///   --instance-name NAME     test only: single-instance name to use instead of the per-user default
///   --selftest FILE          share this PC, connect to it, write PASS or FAIL to FILE, exit (CI)
///   --selftest-server FILE HOSTID   the same through the configured server (CI)
///   --selftest-session FILE  open a real session window against an in-process fake host and check frame, input and transfers panel (CI)
///   --selftest-share FILE    host-side checks of Share this PC: consent, remembered PCs, banner, pause (CI)
/// An option that needs a value and does not get one is reported in <see cref="Problems"/> and ignored; unknown options are ignored and reported.
/// </summary>
public sealed record AppOptions
{
    public string? Invite { get; init; }
    public string? Pair { get; init; }
    public string? ConnectToken { get; init; }
    public string? Show { get; init; }
    public bool Minimized { get; init; }
    public string? ShowResult { get; init; }
    public string? SelfTestShellResultPath { get; init; }
    public string? InstanceName { get; init; }
    public string? SelfTestResultPath { get; init; }
    public string? UiaScanResultPath { get; init; }
    public string? UiaScanPagesPath { get; init; }
    public string? SelfTestServerResultPath { get; init; }
    public string? SelfTestShareResultPath { get; init; }
    public string? SelfTestServerHostPrefix { get; init; }
    public string? SelfTestSessionResultPath { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool IsSelfTest => SelfTestResultPath is not null || SelfTestServerResultPath is not null || SelfTestSessionResultPath is not null || SelfTestShareResultPath is not null || SelfTestShellResultPath is not null;

    /// <summary>Self-tests and --show are test and CI launches: they never join or forward to a running instance.</summary>
    public bool UsesSingleInstance => !IsSelfTest && Show is null && UiaScanResultPath is null;

    /// <summary>The launch carries something the running window must act on (as opposed to just being opened again).</summary>
    public bool HasWork => Invite is not null || Pair is not null || ConnectToken is not null;

    /// <summary>Parses the arguments after the program name.</summary>
    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        var o = new AppOptions();
        var problems = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            var key = name.ToLowerInvariant();
            string? Value(int offset) => i + offset < args.Count && !args[i + offset].StartsWith("--", StringComparison.Ordinal) ? args[i + offset] : null;
            switch (key)
            {
                case "--minimized":
                    o = o with { Minimized = true };
                    break;
                case "--invite" or "--pair" or "--connect-token" or "--show" or "--selftest" or "--selftest-session" or "--selftest-share" or "--show-result" or "--selftest-shell" or "--instance-name":
                    if (Value(1) is { Length: > 0 } v)
                    {
                        o = key switch
                        {
                            "--invite" => o with { Invite = v.Trim() },
                            "--pair" => o with { Pair = v.Trim() },
                            "--connect-token" => o with { ConnectToken = v.Trim() },
                            "--show" => o with { Show = v.Trim() },
                            "--show-result" => o with { ShowResult = v },
                            "--selftest-shell" => o with { SelfTestShellResultPath = v },
                            "--instance-name" => o with { InstanceName = v.Trim() },
                            "--selftest-session" => o with { SelfTestSessionResultPath = v },
                            "--selftest-share" => o with { SelfTestShareResultPath = v },
                            _ => o with { SelfTestResultPath = v },
                        };
                        i++;
                    }
                    else problems.Add($"{name} needs a value");
                    break;
                case "--uia-scan":
                    if (Value(1) is { Length: > 0 } result)
                    {
                        var pages = Value(2);
                        o = o with { UiaScanResultPath = result, UiaScanPagesPath = pages };
                        i += pages is null ? 1 : 2;
                    }
                    else problems.Add($"{name} needs a result file");
                    break;
                case "--selftest-server":
                    if (Value(1) is { } file && Value(2) is { } host)
                    {
                        o = o with { SelfTestServerResultPath = file, SelfTestServerHostPrefix = host };
                        i += 2;
                    }
                    else problems.Add($"{name} needs a result file and a host id");
                    break;
                default:
                    problems.Add($"unknown option {name}");
                    break;
            }
        }
        return o with { Problems = problems };
    }
}

/// <summary>What goes into the Windows "Run" registry value for start-with-Windows.</summary>
public static class StartupCommand
{
    public static string Build(string exePath) => "\"" + exePath.Replace("\"", "") + "\" --minimized";
}

/// <summary>Which tray icon file suits the taskbar. "light" is a light glyph, for a dark taskbar.</summary>
public static class TrayTheme
{
    /// <param name="systemUsesLightTheme">HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme; null when the value is missing (treated as a dark taskbar).</param>
    public static string IconFile(int? systemUsesLightTheme) => systemUsesLightTheme == 1 ? "tray-dark.ico" : "tray-light.ico";
}
