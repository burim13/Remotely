using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Remotely.Core.Admin;

namespace Remotely.Server;

/// <summary>
/// The admin HTTP API. Everything here is served on the loopback admin port only. <c>/admin/v1/*</c> needs the admin token as a bearer token;
/// the older <c>/admin/pair-codes</c> and <c>/admin/devices</c> endpoints (used by the installers and CI) keep working without it unless
/// <see cref="ServerOptions.LegacyAdminRequiresToken"/> is set. See docs/server-admin-api.md.
/// </summary>
public static class AdminApi
{
    private const int RestoreLimit = DataBackup.MaxBytes;

    public static void Map(WebApplication app, ServerOptions options)
    {
        var ops = app.Services.GetRequiredService<AdminOperations>();
        var audit = app.Services.GetRequiredService<AuditLog>();
        var metrics = app.Services.GetRequiredService<ServerMetrics>();
        var limits = app.Services.GetRequiredService<RateLimits>();

        bool IsLoopbackRequest(HttpContext ctx)
        {
            var c = ctx.Connection;
            return c.LocalPort == options.AdminPort && c.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip) && !ctx.Request.Headers.ContainsKey("X-Forwarded-For");
        }

        IResult? CheckToken(HttpContext ctx)
        {
            if (ops.Token is null) return Error(503, "The admin API v1 is not available: the admin token file could not be created. See the server log.");
            var header = ctx.Request.Headers.Authorization.ToString();
            var presented = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
            if (ops.Token.Verify(presented)) return null;
            metrics.AdminUnauthorized();
            if (limits.AdminFailures.Hit("admin") != RateResult.Denied)
                audit.Write("admin.unauthorized", "", null, "refused", $"{ctx.Request.Method} {ctx.Request.Path}" + (presented is null ? " without a token" : " with a wrong token"));
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            return Error(401, "The admin token is missing or wrong.");
        }

        // First line of defence, ahead of routing and parameter binding: nothing under /admin is reachable except from loopback on the admin port,
        // and nothing under /admin/v1 without the token. The endpoint filters below check again.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsLoopbackRequest(ctx)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
                var needsToken = ctx.Request.Path.StartsWithSegments("/admin/v1", StringComparison.OrdinalIgnoreCase) || options.LegacyAdminRequiresToken;
                if (needsToken && CheckToken(ctx) is { } refused) { await refused.ExecuteAsync(ctx); return; }
            }
            await next(ctx);
        });

        // ---- legacy endpoints (installers and CI) ----
        var legacy = app.MapGroup("/admin").AddEndpointFilter(async (f, next) =>
        {
            if (!IsLoopbackRequest(f.HttpContext)) return Results.NotFound();
            if (options.LegacyAdminRequiresToken && CheckToken(f.HttpContext) is { } refused) return refused;
            return await next(f);
        });
        legacy.MapPost("/pair-codes", (PairingCodes pairing, int? uses, int? minutes) =>
        {
            var issued = pairing.Issue(uses ?? 1, minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null);
            audit.Write("invite.create", "", null, "ok", $"id {issued.Id}, {issued.UsesAllowed} uses (pair-code endpoint)");
            return Results.Ok(new { code = issued.Code, expiresUtc = issued.ExpiresUtc });
        });
        legacy.MapGet("/devices", (DeviceStore store, Hub hub) =>
            Results.Ok(store.All().Select(d => new { d.DeviceId, d.Name, d.CanHost, online = hub.IsOnline(d.DeviceId), d.CreatedUtc })));
        legacy.MapDelete("/devices/{id}", async (string id) =>
        {
            try
            {
                await ops.RevokeAsync(id, "the devices endpoint").ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (AdminException ex) when (ex.Status == 404) { return Results.NotFound(); }
        });

        // ---- admin API v1 ----
        var v1 = app.MapGroup("/admin/v1").AddEndpointFilter(async (f, next) =>
        {
            if (!IsLoopbackRequest(f.HttpContext)) return Results.NotFound();
            if (CheckToken(f.HttpContext) is { } refused) return refused;
            try
            {
                return await next(f);
            }
            catch (AdminException ex)
            {
                return Error(ex.Status, ex.Message);
            }
        });

        v1.MapGet("/status", () => Results.Ok(ops.Status()));
        v1.MapGet("/metrics", () => Results.Ok(ops.Metrics()));

        v1.MapPost("/invites", (InviteRequest request) => Results.Ok(ops.CreateInvite(request.LifetimeMinutes, request.Uses)));
        v1.MapGet("/invites", () => Results.Ok(ops.ListInvites()));
        v1.MapDelete("/invites/{id}", (string id) =>
        {
            ops.RevokeInvite(id);
            return Results.NoContent();
        });

        v1.MapGet("/devices", () => Results.Ok(ops.Devices()));
        v1.MapPut("/devices/{id}/name", (string id, NameRequest request) =>
        {
            ops.Rename(id, request.Name);
            return Results.NoContent();
        });
        v1.MapPut("/devices/{id}/blocked", async (string id, BlockedRequest request) =>
        {
            await ops.SetBlockedAsync(id, request.Blocked).ConfigureAwait(false);
            return Results.NoContent();
        });
        v1.MapDelete("/devices/{id}", async (string id) =>
        {
            await ops.RevokeAsync(id).ConfigureAwait(false);
            return Results.NoContent();
        });

        v1.MapGet("/audit", (string? since, int? max) =>
        {
            DateTimeOffset? from = null;
            if (!string.IsNullOrEmpty(since))
            {
                if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    return Error(400, "since must be a date and time such as 2026-01-31T12:00:00Z.");
                from = parsed;
            }
            return Results.Ok(ops.Audit(from, max ?? 500));
        });

        v1.MapGet("/settings", () => Results.Ok(ops.GetSettings()));
        v1.MapPut("/settings", (ServerSettings settings) => Results.Ok(ops.SetSettings(settings)));

        v1.MapGet("/backup", () => Results.File(ops.Backup(), "application/zip", $"Remotely-Server-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip"));
        v1.MapPost("/restore", async (HttpContext ctx) =>
        {
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = RestoreLimit;
            using var body = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted).ConfigureAwait(false)) > 0)
            {
                body.Write(buffer, 0, read);
                if (body.Length > RestoreLimit) return Error(413, "That file is too large to be a server backup.");
            }
            return Results.Ok(await ops.RestoreAsync(body.ToArray()).ConfigureAwait(false));
        });

        v1.MapPost("/certificate/renew", (RenewRequest? request) =>
            Results.Ok(ops.RenewCertificate(request?.NewKey ?? false, request?.ConfirmPinChange ?? false)));
        v1.MapPost("/token/rotate", () => Results.Ok(new { token = ops.RotateToken() }));

        var checks = app.Services.GetRequiredService<OutsideChecks>();
        v1.MapPost("/outside-check", (int? minutes) =>
        {
            var info = checks.Create(minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null);
            audit.Write("outside-check.create", "", null, "ok", $"expires {info.ExpiresUtc:O}");
            return Results.Ok(info);
        });
        v1.MapGet("/outside-check/{id}", (string id) => checks.Get(id) is { } r ? Results.Ok(r) : Error(404, "There is no such check (it may have expired)."));

        // ---- health ----
        app.MapGet("/healthz", (HttpContext ctx) =>
            ctx.Connection.LocalPort == options.PublicPort || IsLoopbackRequest(ctx)
                ? (app.Services.GetRequiredService<Hub>().IsStopping ? Results.Text("stopping", statusCode: 503) : Results.Ok("ok"))
                : Results.NotFound());
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}

internal sealed record InviteRequest(int LifetimeMinutes, int Uses);

internal sealed record NameRequest(string? Name);

internal sealed record BlockedRequest(bool Blocked);

internal sealed record RenewRequest(bool NewKey, bool ConfirmPinChange);
