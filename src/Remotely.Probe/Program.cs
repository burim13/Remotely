using System.Text.Json;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.HostCore;

// Prototype viewer for testing a host from any machine. Not the real viewer: the identity key is stored unprotected.
const string Usage = """
    Remotely.Probe: prototype test viewer

      pair  --server wss://host:8443/ws --pin <server key pin> --code XXXX-XXXX --name "My laptop" [--insecure]
      list
      watch <host device id or unique prefix> [--out frames] [--count 5] [--interval 1000]

    State is kept in ~/.remotely-probe (identity.key, probe.json).
    """;

var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remotely-probe");
var identityPath = Path.Combine(dir, "identity.key");
var settingsPath = Path.Combine(dir, "probe.json");

try
{
    return args.FirstOrDefault() switch
    {
        "pair" => await PairAsync(args[1..]),
        "list" => await ListAsync(),
        "watch" when args.Length >= 2 => await WatchAsync(args[1], args[2..]),
        _ => Print(Usage, 1),
    };
}
catch (Exception ex) when (ex is CoordinatorException or HandshakeException or IOException or InvalidOperationException or FormatException or ArgumentException)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}

static int Print(string text, int code) { Console.WriteLine(text); return code; }

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

DeviceIdentity LoadOrCreateIdentity()
{
    Directory.CreateDirectory(dir);
    if (File.Exists(identityPath)) return DeviceIdentity.FromPkcs8(File.ReadAllBytes(identityPath));
    var identity = DeviceIdentity.Generate();
    File.WriteAllBytes(identityPath, identity.ExportPkcs8());
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(identityPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    return identity;
}

CoordinatorOptions LoadOptions()
{
    var s = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(settingsPath))!;
    return new CoordinatorOptions { ServerUri = new Uri(s["server"]), PinnedServerKey = s["pin"], AllowInsecureTransport = s.GetValueOrDefault("insecure") == "true" };
}

async Task<int> PairAsync(string[] a)
{
    var server = Option(a, "--server") ?? throw new ArgumentException("--server is required");
    var pin = Option(a, "--pin") ?? "";
    var code = Option(a, "--code") ?? throw new ArgumentException("--code is required");
    var name = Option(a, "--name") ?? Environment.MachineName;
    var insecure = a.Contains("--insecure");
    var identity = LoadOrCreateIdentity();
    var options = new CoordinatorOptions { ServerUri = new Uri(server), PinnedServerKey = pin, AllowInsecureTransport = insecure };
    await using var client = await CoordinatorClient.ConnectAsync(options, identity);
    if (!client.IsPaired) await client.PairAsync(code, name, canHost: false);
    File.WriteAllBytes(settingsPath, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
    { ["server"] = server, ["pin"] = pin, ["insecure"] = insecure ? "true" : "false" }));
    Console.WriteLine($"Paired. This viewer's device id (give it to the host's 'trust' command):\n{identity.DeviceId}");
    return 0;
}

async Task<int> ListAsync()
{
    await using var client = await CoordinatorClient.ConnectAsync(LoadOptions(), LoadOrCreateIdentity());
    foreach (var d in await client.ListDevicesAsync())
        Console.WriteLine($"{(d.Online ? "online " : "offline")}  {(d.CanHost ? "host  " : "viewer")}  {d.Name,-24} {d.DeviceId}");
    return 0;
}

async Task<int> WatchAsync(string hostPrefix, string[] a)
{
    var outDir = Option(a, "--out") ?? "frames";
    var count = int.Parse(Option(a, "--count") ?? "5");
    var interval = int.Parse(Option(a, "--interval") ?? "1000");
    Directory.CreateDirectory(outDir);

    var identity = LoadOrCreateIdentity();
    await using var client = await CoordinatorClient.ConnectAsync(LoadOptions(), identity);
    var matches = (await client.ListDevicesAsync()).Where(d => d.CanHost && d.DeviceId.StartsWith(hostPrefix.Replace("-", ""), StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count != 1) throw new ArgumentException(matches.Count == 0 ? "No such host." : "Prefix is ambiguous.");

    var relay = await client.ConnectToAsync(matches[0].DeviceId);
    using var session = await SecureSession.InitiateAsync(relay, identity);
    Console.WriteLine($"Secure session with {matches[0].Name} ({DeviceIds.Short(matches[0].DeviceId)}).");
    for (var i = 1; i <= count; i++)
    {
        await session.SendAsync(new[] { HostMessages.FrameRequest });
        var reply = await session.ReceiveAsync() ?? throw new IOException("Host ended the session.");
        if (reply[0] == HostMessages.Error) { Console.WriteLine($"#{i}: host error: {System.Text.Encoding.UTF8.GetString(reply[1..])}"); }
        else if (reply[0] == HostMessages.FrameReply)
        {
            var frame = FrameCodec.Decode(reply.AsSpan(1));
            var file = Path.Combine(outDir, $"frame-{i:0000}.jpg");
            await File.WriteAllBytesAsync(file, frame.Jpeg);
            Console.WriteLine($"#{i}: {frame.Width}x{frame.Height} desktop={frame.Desktop} {frame.Jpeg.Length / 1024} KB -> {file}");
        }
        if (i < count) await Task.Delay(interval);
    }
    await session.CloseAsync();
    return 0;
}
