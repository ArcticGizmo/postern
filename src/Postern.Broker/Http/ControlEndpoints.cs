using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Postern.Core.Apps;
using Postern.Core.Audit;
using Postern.Core.RateLimiting;

namespace Postern.Broker.Http;

/// <summary>/_postern/*: registration, status, rate limits. Provider names can't start with '_', so these never collide.</summary>
internal static class ControlEndpoints
{
    static readonly TimeSpan MaxClaimWait = TimeSpan.FromSeconds(60);

    public static void Map(IEndpointRouteBuilder app, BrokerRuntime runtime)
    {
        var group = app.MapGroup("/_postern");
        group.MapPost("/register", (HttpContext context) => Register(context, runtime));
        group.MapPost("/register/{id}/claim", (HttpContext context, string id, int? wait) => Claim(context, runtime, id, wait));
        group.MapGet("/status", (HttpContext context) => Status(context, runtime));
        group.MapGet("/ratelimits", (HttpContext context) => RateLimitsFor(context, runtime));
    }

    static async Task Register(HttpContext context, BrokerRuntime runtime)
    {
        var caller = BrokerMiddleware.Caller(context);
        var request = await ReadJson(context, BrokerJson.Default.RegisterRequest);
        if (request is null)
        {
            await PosternErrors.Write(context, 400, "bad-request", "Body must be JSON: { \"name\": \"...\", \"scopes\": [\"GET github/...\"] }");
            return;
        }

        try
        {
            var ticket = runtime.Registrations.Begin(request.Name, request.Scopes ?? [], caller);
            Audit(runtime, caller, "/register", 202, "registration-requested", request.Name);
            context.Response.StatusCode = 202;
            await context.Response.WriteAsJsonAsync(new RegisterResponse(ticket.Id, ticket.Claim, "pending"), BrokerJson.Default.RegisterResponse);
        }
        catch (RegistrationException ex)
        {
            Audit(runtime, caller, "/register", ex.Status, ex.Code, request.Name);
            await PosternErrors.Write(context, ex.Status, ex.Code, ex.Message);
        }
    }

    static async Task Claim(HttpContext context, BrokerRuntime runtime, string id, int? waitSeconds)
    {
        var caller = BrokerMiddleware.Caller(context);
        var request = await ReadJson(context, BrokerJson.Default.ClaimRequest);
        if (request?.Claim is null)
        {
            await PosternErrors.Write(context, 400, "bad-request", "Body must be JSON: { \"claim\": \"psc_...\" }");
            return;
        }

        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds ?? 30, 0, (int)MaxClaimWait.TotalSeconds));
        ClaimResult result;
        try
        {
            result = await runtime.Registrations.ClaimAsync(id, request.Claim, caller, wait, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            return; // client gave up waiting
        }

        switch (result.Status)
        {
            case ClaimStatus.Issued:
                Audit(runtime, caller, "/register/claim", 200, "token-issued", runtime.Registrations.All().FirstOrDefault(r => r.Id == id)?.Name);
                await context.Response.WriteAsJsonAsync(new ClaimResponse("approved", result.Token, result.Detail), BrokerJson.Default.ClaimResponse);
                break;
            case ClaimStatus.Pending:
                context.Response.StatusCode = 202;
                await context.Response.WriteAsJsonAsync(new ClaimResponse("pending", null, result.Detail), BrokerJson.Default.ClaimResponse);
                break;
            case ClaimStatus.Denied:
                await PosternErrors.Write(context, 403, "registration-denied", result.Detail);
                break;
            case ClaimStatus.AlreadyClaimed:
                await PosternErrors.Write(context, 409, "already-claimed", result.Detail);
                break;
            case ClaimStatus.CallerMismatch:
                Audit(runtime, caller, "/register/claim", 403, "caller-mismatch", null);
                await PosternErrors.Write(context, 403, "caller-mismatch", result.Detail);
                break;
            default:
                await PosternErrors.Write(context, 404, "registration-not-found", result.Detail);
                break;
        }
    }

    static Task Status(HttpContext context, BrokerRuntime runtime)
    {
        var app = AuthenticatedApp(context, runtime);
        var body = new StatusResponse(
            BrokerRuntime.Version,
            runtime.Options.PipeName,
            Environment.ProcessId,
            [.. runtime.Providers.Select(p => p.Name)],
            app is null ? null : new AppStatus(app.Id, app.Name, app.Status.ToString().ToLowerInvariant(), [.. app.Scopes.Select(s => s.ToString())]));
        return context.Response.WriteAsJsonAsync(body, BrokerJson.Default.StatusResponse);
    }

    static async Task RateLimitsFor(HttpContext context, BrokerRuntime runtime)
    {
        var app = AuthenticatedApp(context, runtime);
        if (app is not { Status: RegistrationStatus.Approved })
        {
            await PosternErrors.Write(context, 401, "no-token", "Rate limits are visible to approved apps, for the providers in their scopes.");
            return;
        }
        var providers = app.Scopes.Select(s => s.Provider).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<RateLimitSnapshot> visible = [.. runtime.RateLimits.All().Where(s => providers.Contains(s.Provider))];
        await context.Response.WriteAsJsonAsync(visible, BrokerJson.Default.IReadOnlyListRateLimitSnapshot);
    }

    /// <summary>The caller's registration if it sent a token that belongs to it (any status), else null.</summary>
    static AppRegistration? AuthenticatedApp(HttpContext context, BrokerRuntime runtime)
    {
        string? token = context.Request.Headers[ProxyEndpoint.TokenHeader];
        if (string.IsNullOrEmpty(token))
            return null;
        var app = runtime.RegistrationStore.FindByTokenHash(Secrets.Hash(token));
        return app is not null && BrokerMiddleware.Caller(context).Matches(app) ? app : null;
    }

    static async Task<T?> ReadJson<T>(HttpContext context, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        try
        {
            return await context.Request.ReadFromJsonAsync(type, context.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }

    static void Audit(BrokerRuntime runtime, CallerInfo caller, string path, int status, string outcome, string? appName) =>
        runtime.Audit.Write(new AuditEntry(runtime.Time.GetUtcNow(), null, appName, caller.ProcessId, caller.ExePath, "POST", "_postern", path, 0, status, outcome, null));
}
