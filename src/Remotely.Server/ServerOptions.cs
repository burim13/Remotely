namespace Remotely.Server;

public sealed class ServerOptions
{
    public const string Section = "Remotely";

    /// <summary>Port devices connect to (all interfaces).</summary>
    public int PublicPort { get; set; } = 8443;

    /// <summary>Loopback-only port for the admin API (pair codes, device list). Never exposed to the network.</summary>
    public int AdminPort { get; set; } = 8181;

    /// <summary>Serve the public port over TLS with a self-generated certificate that clients pin. Turn off only for local testing.</summary>
    public bool TlsEnabled { get; set; } = true;

    /// <summary>Where devices.json and the TLS certificate live. Empty means %ProgramData%\Remotely (Windows) or ~/.local/share/Remotely.</summary>
    public string DataDir { get; set; } = "";

    public string ResolveDataDir()
    {
        if (!string.IsNullOrWhiteSpace(DataDir)) return DataDir;
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "Remotely");
    }
}
