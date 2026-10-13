using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Remotely.Client;
using Remotely.Core.Crypto;
using Remotely.Server;

namespace Remotely.Tests;

/// <summary>A real server on loopback ports with its own data directory.</summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "remotely-test-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _admin = new();

    public int PublicPort { get; } = FreePort();
    public int AdminPort { get; } = FreePort();
    public bool Tls { get; }
    public string? Pin { get; }
    public string DataDir => _dataDir;

    private TestServer(bool tls)
    {
        Tls = tls;
        _app = RemotelyServer.Build([], o =>
        {
            o.PublicPort = PublicPort;
            o.AdminPort = AdminPort;
            o.TlsEnabled = tls;
            o.DataDir = _dataDir;
        });
        if (tls) Pin = ServerCertificate.Pin(ServerCertificate.LoadOrCreate(_dataDir));
    }

    public static async Task<TestServer> StartAsync(bool tls = false)
    {
        var server = new TestServer(tls);
        await server._app.StartAsync();
        return server;
    }

    public CoordinatorOptions Options(string? pinOverride = null, bool? insecure = null) => new()
    {
        ServerUri = new Uri($"{(Tls ? "wss" : "ws")}://127.0.0.1:{PublicPort}/ws"),
        PinnedServerKey = pinOverride ?? Pin,
        AllowInsecureTransport = insecure ?? !Tls,
        RequestTimeout = TimeSpan.FromSeconds(10),
    };

    public async Task<string> NewPairCodeAsync()
    {
        var response = await _admin.PostAsync($"http://127.0.0.1:{AdminPort}/admin/pair-codes", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["code"]!.ToString()!;
    }

    public async Task<HttpStatusCode> GetStatusAsync(string url) => (await _admin.GetAsync(url)).StatusCode;

    /// <summary>Connects and pairs a new device in one step.</summary>
    public async Task<(CoordinatorClient Client, DeviceIdentity Identity)> PairedDeviceAsync(string name, bool canHost)
    {
        var identity = DeviceIdentity.Generate();
        var client = await CoordinatorClient.ConnectAsync(Options(), identity);
        await client.PairAsync(await NewPairCodeAsync(), name, canHost);
        return (client, identity);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _admin.Dispose();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
