using Postern.Core.Scopes;

namespace Postern.Core.Apps;

/// <summary>The outcome for one request. <see cref="Code"/> is what goes in the Postern-Error header when denied.</summary>
public sealed record AccessDecision(bool Allowed, int Status, string Code, string Detail, AppRegistration? App = null, Scope? MatchedScope = null)
{
    public static AccessDecision Allow(AppRegistration app, Scope scope) => new(true, 200, "allowed", $"matched '{scope}'", app, scope);
    public static AccessDecision Deny(int status, string code, string detail, AppRegistration? app = null) => new(false, status, code, detail, app);
}

/// <summary>
/// Token → registration → caller → scope. Every branch that isn't an explicit allow is a deny (fail closed).
/// </summary>
public static class AccessPolicy
{
    public static AccessDecision Evaluate(IRegistrationStore store, string? token, CallerInfo caller, string method, string provider, string decodedPath)
    {
        if (string.IsNullOrEmpty(token))
            return AccessDecision.Deny(401, "no-token", "Send Postern-App-Token. Register first: POST /_postern/register.");

        var app = store.FindByTokenHash(Secrets.Hash(token));
        if (app is null)
            return AccessDecision.Deny(401, "unknown-token", "Token not recognised. It may be from an old registration; register again.");

        switch (app.Status)
        {
            case RegistrationStatus.Approved:
                break;
            case RegistrationStatus.Revoked:
                return AccessDecision.Deny(403, "app-revoked", $"Access for '{app.Name}' was revoked in Postern.", app);
            case RegistrationStatus.Superseded:
                return AccessDecision.Deny(401, "token-superseded", $"'{app.Name}' has a newer registration; this token was replaced.", app);
            default:
                return AccessDecision.Deny(403, "app-not-approved", $"'{app.Name}' is {app.Status}.", app);
        }

        if (!caller.Matches(app))
            return AccessDecision.Deny(403, "caller-mismatch",
                $"Token belongs to '{app.Name}' ({app.ExePath}) but the caller is pid {caller.ProcessId} ({caller.ExePath ?? "unknown exe"}).", app);

        if (!RequestPath.TryParse(decodedPath, out var path, out var pathError))
            return AccessDecision.Deny(400, "bad-path", pathError, app);

        foreach (var scope in app.Scopes)
            if (scope.Allows(method, provider, path))
                return AccessDecision.Allow(app, scope);

        return AccessDecision.Deny(403, "scope-denied", $"'{app.Name}' has no scope for {method.ToUpperInvariant()} {provider}{path}.", app);
    }
}
