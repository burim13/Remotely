namespace Remotely.Server;

/// <summary>Per-source limits on attempts. 0 turns a limit off (for a load test from one machine, for example).</summary>
public sealed class LimitOptions
{
    /// <summary>New device connections per source address per minute (burst of the same size).</summary>
    public int ConnectAttemptsPerMinute { get; set; } = 120;

    /// <summary>Pairing attempts per source address per minute, on top of the lockout after five wrong codes.</summary>
    public int PairAttemptsPerMinute { get; set; } = 20;

    /// <summary>Requests to open a session per device per minute.</summary>
    public int SessionRequestsPerMinute { get; set; } = 60;
}

public sealed class ServerOptions
{
    public const string Section = "Remotely";

    /// <summary>Port devices connect to (all interfaces, IPv4 and IPv6 where the machine has it).</summary>
    public int PublicPort { get; set; } = 8443;

    /// <summary>Loopback-only port for the admin API (pair codes, device list, /admin/v1). Never exposed to the network.</summary>
    public int AdminPort { get; set; } = 8181;

    /// <summary>Serve the public port over TLS with a self-generated certificate that clients pin. Turn off only for local testing.</summary>
    public bool TlsEnabled { get; set; } = true;

    /// <summary>Where devices.json and the TLS certificate live. Empty means %ProgramData%\Remotely Server (Windows) or ~/.local/share/RemotelyServer.</summary>
    public string DataDir { get; set; } = "";

    /// <summary>Most devices that may be paired. Changeable at run time through the admin API.</summary>
    public int MaxDevices { get; set; } = 1000;

    /// <summary>Most sessions one device may take part in at once. Changeable at run time through the admin API.</summary>
    public int MaxSessionsPerDevice { get; set; } = 8;

    public LimitOptions Limits { get; set; } = new();

    /// <summary>The older /admin/pair-codes and /admin/devices endpoints (used by the installers and CI) work without the admin token. Set true to demand it there too.</summary>
    public bool LegacyAdminRequiresToken { get; set; }

    /// <summary>Upper bound for the audit log on disk (two segments together).</summary>
    public long AuditMaxBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Write structured logs to logs/server.log in the data directory, rotated by size and day.</summary>
    public bool FileLogging { get; set; } = true;

    public int LogMaxFileMegabytes { get; set; } = 10;

    /// <summary>Rotated log files kept besides the current one.</summary>
    public int LogFilesKept { get; set; } = 14;

    /// <summary>How long a stop waits for connections to close before giving up.</summary>
    public int ShutdownSeconds { get; set; } = 10;

    public string ResolveDataDir()
    {
        if (!string.IsNullOrWhiteSpace(DataDir)) return DataDir;
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, OperatingSystem.IsWindows() ? "Remotely Server" : "RemotelyServer");
    }
}
