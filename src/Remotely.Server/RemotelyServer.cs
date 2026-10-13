using System.Net;
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

        X509Certificate2? cert = options.TlsEnabled ? ServerCertificate.LoadOrCreate(dataDir) : null;

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenAnyIP(options.PublicPort, l => { if (cert is not null) l.UseHttps(cert); });
            k.Listen(IPAddress.Loopback, options.AdminPort);
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSingleton(new DeviceStore(dataDir));
        builder.Services.AddSingleton<PairingCodes>();
        builder.Services.AddSingleton<Hub>();

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        if (cert is not null)
            app.Logger.LogInformation("Server key pin (give this to devices when pairing): {Pin}", ServerCertificate.Pin(cert));
        else
            app.Logger.LogWarning("TLS is OFF. Use this only for local testing.");

        // Device endpoint: public port only.
        app.Map("/ws", async (HttpContext ctx, Hub hub) =>
        {
            if (ctx.Connection.LocalPort != options.PublicPort || !ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            await hub.RunAsync(new DeviceConnection(socket, remote), ctx.RequestAborted);
        });

        // Admin API: loopback port only. Reached by running curl on the server itself.
        var admin = app.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
        {
            var c = ctx.HttpContext.Connection;
            return c.LocalPort == options.AdminPort && c.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)
                   && !ctx.HttpContext.Request.Headers.ContainsKey("X-Forwarded-For")
                ? await next(ctx)
                : Results.NotFound();
        });
        admin.MapPost("/pair-codes", (PairingCodes pairing) =>
        {
            var (code, expires) = pairing.Create();
            return Results.Ok(new { code, expiresUtc = expires });
        });
        admin.MapGet("/devices", (DeviceStore store, Hub hub) =>
            Results.Ok(store.All().Select(d => new { d.DeviceId, d.Name, d.CanHost, online = hub.IsOnline(d.DeviceId), d.CreatedUtc })));
        admin.MapDelete("/devices/{id}", (string id, DeviceStore store) => store.Remove(id) ? Results.NoContent() : Results.NotFound());

        app.MapGet("/healthz", (HttpContext ctx) =>
            ctx.Connection.LocalPort == options.PublicPort ? Results.Ok("ok") : Results.NotFound());

        return app;
    }
}
