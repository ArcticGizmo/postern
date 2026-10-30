using Postern.Client;

namespace Postern.Sallyport;

public enum RegistrationState { Unavailable, Approved, NeedsApproval, Revoked, Denied }

/// <summary>Whether Sallyport is approved for every scope its scenarios need, and asking Postern when it isn't.</summary>
public static class Registration
{
    /// <summary>One scope per scenario that should be let through, plus whatever the soak calls.</summary>
    public static IReadOnlyList<string> ScopesFor(IEnumerable<Scenario> scenarios, Soak? soak = null) =>
    [
        .. scenarios.Select(s => s.ScopeNeeded).OfType<string>()
            .Append(soak is null ? null : $"GET {soak.Describe}").OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Where Sallyport stands with Postern now, without prompting anyone.</summary>
    public static async Task<(RegistrationState State, PosternStatus? Status)> CheckAsync(PosternClient postern, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
    {
        if (!postern.IsAvailable)
            return (RegistrationState.Unavailable, null);
        PosternStatus status;
        try
        {
            status = await postern.GetStatusAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is PosternUnavailableException or HttpRequestException)
        {
            return (RegistrationState.Unavailable, null);
        }

        // A token Postern doesn't know (a reset sandbox, say) is as good as none.
        if (postern.HasToken && status.App is null)
            postern.ForgetToken();

        var state = status.App switch
        {
            null => RegistrationState.NeedsApproval,
            { Status: "revoked" } => RegistrationState.Revoked,
            { Status: "approved" } app when scopes.All(s => app.Scopes.Contains(s, StringComparer.OrdinalIgnoreCase)) => RegistrationState.Approved,
            _ => RegistrationState.NeedsApproval,
        };
        return (state, status);
    }

    /// <summary>Asks Postern for <paramref name="scopes"/>; someone has to approve it there.</summary>
    public static async Task<(RegistrationState State, string Detail)> RequestAsync(PosternClient postern, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await postern.RegisterAsync(scopes, TimeSpan.FromMinutes(5), cancellationToken);
            return (result.Outcome == RegistrationOutcome.Approved ? RegistrationState.Approved : RegistrationState.Denied, result.Detail);
        }
        catch (Exception ex) when (ex is PosternUnavailableException or HttpRequestException)
        {
            return (RegistrationState.Unavailable, ex.Message);
        }
    }
}
