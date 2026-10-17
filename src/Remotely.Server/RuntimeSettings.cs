using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Remotely.Core.Admin;

namespace Remotely.Server;

/// <summary>
/// The settings an operator changes through the admin API. Stored in settings.json (schema 1) plus address.txt (the file <c>set-address</c> already writes).
/// Address and the two limits take effect at once; the public port and the TLS switch are read when the server starts, so changing them asks for a restart.
/// Values saved here win over appsettings.json.
/// </summary>
public sealed class RuntimeSettings
{
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Stored(int SchemaVersion, int Port, int MaxDevices, int MaxSessionsPerDevice, bool RequireTls);

    private readonly string _dataDir;
    private readonly ServerOptions _running;
    private readonly object _gate = new();
    private volatile ServerSettings _current;

    private RuntimeSettings(string dataDir, ServerOptions running, ServerSettings current)
    {
        _dataDir = dataDir;
        _running = running;
        _current = current;
    }

    public ServerSettings Current => _current;
    public int MaxDevices => _current.MaxDevices;
    public int MaxSessionsPerDevice => _current.MaxSessionsPerDevice;
    public int RunningPort => _running.PublicPort;
    public bool RunningTls => _running.TlsEnabled;

    /// <summary>Reads settings.json, if there is one, over the options from appsettings.json. Call before the server binds its ports.</summary>
    public static RuntimeSettings Load(string dataDir, ServerOptions options)
    {
        var path = Path.Combine(dataDir, "settings.json");
        if (File.Exists(path))
        {
            var stored = Parse(File.ReadAllBytes(path), path);
            options.PublicPort = stored.Port;
            options.MaxDevices = stored.MaxDevices;
            options.MaxSessionsPerDevice = stored.MaxSessionsPerDevice;
            options.TlsEnabled = stored.RequireTls;
        }
        return new RuntimeSettings(dataDir, options, Compose(dataDir, options));
    }

    private static ServerSettings Compose(string dataDir, ServerOptions o) =>
        new(ServerAddress.Load(dataDir), o.PublicPort, o.MaxDevices, o.MaxSessionsPerDevice, o.TlsEnabled);

    private static Stored Parse(byte[] json, string source)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json, Json) ?? throw new DataFormatException($"{source} is empty.");
            if (stored.SchemaVersion > SchemaVersion)
                throw new DataFormatException($"{source} was written by a newer Remotely server (data schema {stored.SchemaVersion}; this server understands up to {SchemaVersion}).");
            if (stored.SchemaVersion < 1) throw new DataFormatException($"{source} has no schema version.");
            return stored;
        }
        catch (JsonException ex)
        {
            throw new DataFormatException($"{source} is damaged ({ex.Message}). Restore it from a backup or delete it to use the defaults.");
        }
    }

    /// <summary>Checks the file a backup holds without applying it.</summary>
    public static void Verify(byte[] json) => Parse(json, "settings.json");

    /// <summary>Null when the settings are acceptable; otherwise what is wrong, in a sentence.</summary>
    public string? Validate(ServerSettings s, int adminPort)
    {
        if (s.Port is < 1 or > 65535) return "The port must be between 1 and 65535.";
        if (s.Port == adminPort) return "That port is used by the local admin API.";
        if (s.MaxDevices is < 1 or > 10_000) return "The device limit must be between 1 and 10000.";
        if (s.MaxSessionsPerDevice is < 1 or > 64) return "The sessions per device limit must be between 1 and 64.";
        if (ServerAddress.Validate(s.Address, s.RequireTls, s.Port) is { } addressError) return addressError;
        if (s.Port != _running.PublicPort && !PortIsFree(s.Port)) return $"Port {s.Port} is already in use on this PC.";
        return null;
    }

    private static bool PortIsFree(int port)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>Saves (already validated) settings. Returns which of them only take effect after a restart.</summary>
    public IReadOnlyList<string> Apply(ServerSettings s)
    {
        s = s with { Address = s.Address.Trim() };
        lock (_gate)
        {
            DataFiles.WriteAtomic(Path.Combine(_dataDir, "settings.json"),
                JsonSerializer.SerializeToUtf8Bytes(new Stored(SchemaVersion, s.Port, s.MaxDevices, s.MaxSessionsPerDevice, s.RequireTls), Json));
            ServerAddress.Save(_dataDir, s.Address);
            _current = s;
        }
        var restart = new List<string>();
        if (s.Port != _running.PublicPort) restart.Add("port");
        if (s.RequireTls != _running.TlsEnabled) restart.Add("requireTls");
        return restart;
    }

    /// <summary>Re-reads files after a restore.</summary>
    public IReadOnlyList<string> Reload(ServerOptions options)
    {
        var path = Path.Combine(_dataDir, "settings.json");
        var current = _current;
        if (File.Exists(path))
        {
            var stored = Parse(File.ReadAllBytes(path), path);
            current = new ServerSettings(ServerAddress.Load(_dataDir), stored.Port, stored.MaxDevices, stored.MaxSessionsPerDevice, stored.RequireTls);
        }
        else
        {
            current = current with { Address = ServerAddress.Load(_dataDir) };
        }
        _current = current;
        var restart = new List<string>();
        if (current.Port != options.PublicPort) restart.Add("port");
        if (current.RequireTls != options.TlsEnabled) restart.Add("requireTls");
        return restart;
    }
}
