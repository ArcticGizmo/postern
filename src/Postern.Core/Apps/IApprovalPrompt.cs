using Postern.Core.Scopes;

namespace Postern.Core.Apps;

/// <summary>What the user is asked to approve. <see cref="PreviousScopes"/> is set when this replaces an existing approval.</summary>
public sealed record ApprovalRequest(
    string RegistrationId,
    string Name,
    string ExePath,
    uint ProcessId,
    bool SignatureTrusted,
    string? Signer,
    IReadOnlyList<Scope> Scopes,
    IReadOnlyList<Scope>? PreviousScopes);

/// <summary>Asks the user. Returns true only for an explicit approve; cancellation, close or error must mean no.</summary>
public interface IApprovalPrompt
{
    Task<bool> AskAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Headless mode: nobody to ask, so nothing gets approved.</summary>
public sealed class DenyAllPrompt : IApprovalPrompt
{
    public Task<bool> AskAsync(ApprovalRequest request, CancellationToken cancellationToken) => Task.FromResult(false);
}
