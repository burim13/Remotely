using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Remotely.Client;
using Remotely.Core.Admin;
using Remotely.Core.Crypto;
using Remotely.Server;

namespace Remotely.Tests;

/// <summary>A real server on loopback ports with its own data directory.</summary>
public sealed class TestServer : IAsyncDisposable
{
    private WebApplication _app;
    private readonly string _dataDir;
    private readonly bool _ownsDataDir;
    private readonly Action<ServerOptions>? _configure;
    private readonly HttpClient _admin = new();
    private ServerAdminClient? _adminClient;

    /// <summary>Switches the per-source rate limits off, for tests that open many connections from one address (the soak fleet).</summary>
    public static readonly Action<ServerOptions> NoLimits = o =>
    {
        o.Limits.ConnectAttemptsPerMinute = 0;
        o.Limits.PairAttemptsPerMinute = 0;
        o.Limits.SessionRequestsPerMinute = 0;
    };

    public int PublicPort { get; } = FreePort();
    public int AdminPort { get; } = FreePort();
    public bool Tls { get; }
    public string? Pin { get; private set; }
    public string DataDir => _dataDir;

    /// <summary>The server's services, for tests that look inside (device store, hub, metrics).</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>The typed admin client, authenticated with the token file in this server's data directory.</summary>
    public ServerAdminClient Admin => _adminClient ??= ServerAdminClient.ForDataDirectory(_dataDir, AdminPort);

    private TestServer(bool tls, Action<ServerOptions>? configure, string? dataDir)
    {
        Tls = tls;
        _configure = configure;
        _ownsDataDir = dataDir is null;
        _dataDir = dataDir ?? Path.Combine(Path.GetTempPath(), "remotely-test-" + Guid.NewGuid().ToString("N"));
        _app = Build();
    }

    private WebApplication Build()
    {
        var app = RemotelyServer.Build([], o =>
        {
            o.PublicPort = PublicPort;
            o.AdminPort = AdminPort;
            o.TlsEnabled = Tls;
            o.DataDir = _dataDir;
            _configure?.Invoke(o);
        });
        if (Tls) Pin = ServerCertificate.Pin(ServerCertificate.LoadOrCreate(_dataDir));
        return app;
    }

    /// <param name="configure">Changes server options after the fixture's own (ports, data directory), for example to raise the rate limits.</param>
    /// <param name="dataDir">Use an existing data directory (it is not deleted at the end), for upgrade tests.</param>
    public static async Task<TestServer> StartAsync(bool tls = false, Action<ServerOptions>? configure = null, string? dataDir = null)
    {
        // FreePort() only says a port was free a moment ago; with many tests starting servers at once another can take it first. Try again with new ports.
        for (var attempt = 1; ; attempt++)
        {
            var server = new TestServer(tls, configure, dataDir);
            try
            {
                await server._app.StartAsync();
                return server;
            }
            catch (IOException) when (attempt < 5)
            {
                await server._app.DisposeAsync();
            }
        }
    }

    /// <summary>Stops the server and starts a new one on the same ports and data directory, as a service restart does.</summary>
    public async Task RestartAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = Build();
        await _app.StartAsync();
    }

    public async Task StopAsync() => await _app.StopAsync();

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

    /// <summary>The admin token as the file holds it.</summary>
    public string AdminToken => File.ReadAllText(Path.Combine(_dataDir, ServerAdminClient.TokenFileName)).Trim();

    /// <summary>A raw HTTP request to the admin port, optionally with a bearer token, for tests of what the API refuses.</summary>
    public async Task<HttpResponseMessage> RawAdminAsync(HttpMethod method, string path, string? token = null, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{AdminPort}{path}") { Content = content };
        if (token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await _admin.SendAsync(request);
    }

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
        _adminClient?.Dispose();
        if (_ownsDataDir)
        {
            try { Directory.Delete(_dataDir, true); } catch (IOException) { }
        }
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
