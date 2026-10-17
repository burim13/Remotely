using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Remotely.Core.Crypto;

namespace Remotely.Server;

public static class RemotelyServer
{
    public static WebApplication Build(string[] args, Action<ServerOptions>? configureOptions = null, TimeProvider? time = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService(o => o.ServiceName = "Remotely.Server");

        var options = new ServerOptions();
        builder.Configuration.GetSection(ServerOptions.Section).Bind(options);
        configureOptions?.Invoke(options);
        var dataDir = options.ResolveDataDir();
        Directory.CreateDirectory(dataDir);

        // Settings saved through the admin API (settings.json) win over appsettings.json.
        var settings = RuntimeSettings.Load(dataDir, options);

        var clock = time ?? TimeProvider.System;
        var token = AdminToken.LoadOrCreate(dataDir, out var tokenProblem);
        CertificateHolder? certificate = options.TlsEnabled ? new CertificateHolder(ServerCertificate.LoadOrCreate(dataDir)) : null;
        var store = new DeviceStore(dataDir);
        var audit = new AuditLog(dataDir, options.AuditMaxBytes, clock);

        if (options.FileLogging)
            builder.Logging.AddProvider(new RotatingFileLoggerProvider(Path.Combine(dataDir, "logs"), options.LogMaxFileMegabytes * 1024L * 1024L, options.LogFilesKept));

        builder.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(Math.Max(1, options.ShutdownSeconds)));
        builder.WebHost.ConfigureKestrel(k =>
        {
            // The public port faces the internet: bound what an anonymous caller can hold open or send.
            k.Limits.MaxConcurrentConnections = 256;
            k.Limits.MaxConcurrentUpgradedConnections = 256;
            k.Limits.MaxRequestBodySize = 64 * 1024;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            k.AddServerHeader = false;
            // ListenAnyIP uses a dual-mode IPv6 socket where the machine has IPv6 (so IPv4 and IPv6 both work), and IPv4 only otherwise.
            k.ListenAnyIP(options.PublicPort, l =>
            {
                // The selector lets a renewed or restored certificate take effect for new connections without a restart.
                if (certificate is not null) l.UseHttps(https => https.ServerCertificateSelector = (_, _) => certificate.Current);
            });
            k.Listen(IPAddress.Loopback, options.AdminPort);
            if (Ipv6LoopbackAvailable()) k.Listen(IPAddress.IPv6Loopback, options.AdminPort);
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(audit);
        builder.Services.AddSingleton(new ServerMetrics(clock));
        builder.Services.AddSingleton(new RateLimits(options.Limits, clock));
        builder.Services.AddSingleton<PairingCodes>();
        builder.Services.AddSingleton<Hub>();
        builder.Services.AddSingleton(sp => new AdminOperations(options, dataDir, store, sp.GetRequiredService<PairingCodes>(), settings, audit, sp.GetRequiredService<Hub>(),
            certificate, token, sp.GetRequiredService<ServerMetrics>(), clock, sp.GetRequiredService<ILogger<AdminOperations>>()));
        builder.Services.AddHostedService(_ => new MaintenanceService(store));

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        var hub = app.Services.GetRequiredService<Hub>();
        var metrics = app.Services.GetRequiredService<ServerMetrics>();
        var limits = app.Services.GetRequiredService<RateLimits>();

        app.Logger.LogInformation("Remotely server {Version} starting. Data: {DataDir}. Public port {Port}, admin port {AdminPort} (loopback).", AdminOperations.Version, dataDir, options.PublicPort, options.AdminPort);
        if (certificate is not null)
            app.Logger.LogInformation("Server key pin (give this to devices when pairing): {Pin}", certificate.Pin);
        else
            app.Logger.LogWarning("TLS is OFF. Use this only for local testing.");
        if (tokenProblem is not null) app.Logger.LogError("{Problem}", tokenProblem);
        else if (token is not null) app.Logger.LogInformation("Admin API v1 token file: {Path}", token.Path);

        // Graceful shutdown: tell every device first (so long-lived sockets do not hold the stop up), then write what is only in memory.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            app.Logger.LogInformation("Stopping: closing device connections");
            hub.CloseAllAsync("The server is stopping").GetAwaiter().GetResult();
        });
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            store.Flush();
            audit.Dispose();
            app.Logger.LogInformation("Stopped");
        });

        // Device endpoint: public port only.
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (ctx.Connection.LocalPort != options.PublicPort || !ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (hub.IsStopping)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            var remote = RateLimits.SourceKey(ctx.Connection.RemoteIpAddress);
            switch (limits.Connect.Hit(remote))
            {
                case RateResult.DeniedFirst:
                    app.Logger.LogWarning("Too many connection attempts from {Remote}; refusing for a while", remote);
                    goto case RateResult.Denied;
                case RateResult.Denied:
                    metrics.ConnectionRateLimited();
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
            }
            metrics.ConnectionAccepted();
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(new DeviceConnection(socket, remote), ctx.RequestAborted);
        });

        AdminApi.Map(app, options);

        return app;
    }

    private static bool Ipv6LoopbackAvailable()
    {
        if (!Socket.OSSupportsIPv6) return false;
        try
        {
            using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            return true;
        }
        catch (SocketException) { return false; }
    }
}

/// <summary>Writes the things kept only in memory (last-seen times) to disk every so often, so a crash loses little.</summary>
internal sealed class MaintenanceService(DeviceStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) store.Flush();
        }
        catch (OperationCanceledException) { }
    }
}
