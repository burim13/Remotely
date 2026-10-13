using System.Text.Json;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.HostCore;
using Remotely.ViewerCore;

// Scriptable test viewer. Not the real viewer: the identity key is stored unprotected.
const string Usage = """
    Remotely.Probe: command-line test viewer

      pair  --server wss://host:8443/ws --pin <server key pin> --code XXXX-XXXX --name "My laptop" [--insecure]
      list
      watch   <host id or prefix> [--out frames] [--count 5] [--seconds 10]
      control <host id or prefix> [--move X,Y] [--click left|right] [--key VK] [--clip TEXT] [--sas]
                                  [--upload FILE] [--list-share] [--download NAME] [--wait 3]
              (X,Y are 0..65535 across the whole screen; VK is a Windows virtual-key code, e.g. 65 for A)

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
        "control" when args.Length >= 2 => await ControlAsync(args[1], args[2..]),
        _ => Print(Usage, 1),
    };
}
catch (Exception ex) when (ex is CoordinatorException or HandshakeException or IOException or InvalidOperationException or FormatException or ArgumentException or TimeoutException)
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

async Task<ViewerSession> OpenAsync(CoordinatorClient client, DeviceIdentity identity, string hostPrefix)
{
    var wanted = hostPrefix.Replace("-", "");
    var matches = (await client.ListDevicesAsync()).Where(d => d.CanHost && d.DeviceId.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count != 1) throw new ArgumentException(matches.Count == 0 ? "No such host." : "Prefix is ambiguous.");
    var session = await ViewerSession.OpenAsync(client, identity, matches[0].DeviceId);
    Console.WriteLine($"Secure session with {matches[0].Name} ({DeviceIds.Short(matches[0].DeviceId)}). Capabilities: {session.Hello.Capabilities}");
    session.HostError += e => Console.WriteLine("host error: " + e);
    return session;
}

async Task<int> WatchAsync(string hostPrefix, string[] a)
{
    var outDir = Option(a, "--out") ?? "frames";
    var count = int.Parse(Option(a, "--count") ?? "5");
    var seconds = int.Parse(Option(a, "--seconds") ?? "10");
    Directory.CreateDirectory(outDir);

    var identity = LoadOrCreateIdentity();
    await using var client = await CoordinatorClient.ConnectAsync(LoadOptions(), identity);
    await using var session = await OpenAsync(client, identity, hostPrefix);
    var received = 0;
    var done = new TaskCompletionSource();
    session.FrameReceived += frame =>
    {
        var n = Interlocked.Increment(ref received);
        if (n > count) return;
        var file = Path.Combine(outDir, $"frame-{n:0000}.jpg");
        File.WriteAllBytes(file, frame.Jpeg);
        Console.WriteLine($"#{n}: {frame.Width}x{frame.Height} desktop={frame.Desktop} {frame.Jpeg.Length / 1024} KB -> {file}");
        if (n == count) done.TrySetResult();
    };
    session.Closed += reason => { Console.WriteLine("Session closed" + (reason is null ? "" : ": " + reason)); done.TrySetResult(); };
    await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
    Console.WriteLine($"Received {Math.Min(received, count)} frame(s).");
    return received > 0 ? 0 : 3;
}

async Task<int> ControlAsync(string hostPrefix, string[] a)
{
    var identity = LoadOrCreateIdentity();
    await using var client = await CoordinatorClient.ConnectAsync(LoadOptions(), identity);
    await using var session = await OpenAsync(client, identity, hostPrefix);
    session.ClipboardReceived += t => Console.WriteLine($"clipboard from host: {t}");
    var failures = 0;

    if (Option(a, "--move") is { } move)
    {
        var parts = move.Split(',');
        session.SendInput(InputEvent.Move(ushort.Parse(parts[0]), ushort.Parse(parts[1])));
        Console.WriteLine($"sent mouse move {move} (can control: {session.CanControl})");
    }
    if (Option(a, "--click") is { } click)
    {
        var button = click == "right" ? MouseButton.Right : MouseButton.Left;
        session.SendInput(InputEvent.Down(button, 32768, 32768));
        session.SendInput(InputEvent.Up(button, 32768, 32768));
        Console.WriteLine($"sent {click} click");
    }
    if (Option(a, "--key") is { } key)
    {
        var vk = ushort.Parse(key);
        session.SendInput(InputEvent.Key(true, vk, 0, false));
        session.SendInput(InputEvent.Key(false, vk, 0, false));
        Console.WriteLine($"sent key {vk}");
    }
    if (Option(a, "--clip") is { } clip)
    {
        await session.SendClipboardAsync(clip);
        Console.WriteLine("sent clipboard text");
    }
    if (a.Contains("--sas"))
    {
        Console.WriteLine($"Ctrl+Alt+Del available: {session.CanSendSecureAttention}");
        await session.SendSecureAttentionAsync();
    }
    if (Option(a, "--upload") is { } upload)
    {
        try { Console.WriteLine("uploaded as: " + await session.UploadFileAsync(upload)); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Console.WriteLine("upload failed: " + ex.Message); failures++; }
    }
    if (a.Contains("--list-share"))
    {
        var list = await session.ListSharedFilesAsync();
        Console.WriteLine($"shared files: {list.Length}");
        foreach (var e in list) Console.WriteLine($"  {e.Name}  {e.Size} bytes");
    }
    if (Option(a, "--download") is { } name)
    {
        session.DownloadDirectory = Path.Combine(Directory.GetCurrentDirectory(), "downloads");
        try { Console.WriteLine("downloaded to: " + await session.DownloadFileAsync(name)); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Console.WriteLine("download failed: " + ex.Message); failures++; }
    }

    await Task.Delay(TimeSpan.FromSeconds(int.Parse(Option(a, "--wait") ?? "3")));
    return failures == 0 ? 0 : 4;
}
