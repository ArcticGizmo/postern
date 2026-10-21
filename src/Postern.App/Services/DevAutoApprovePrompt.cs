#if DEBUG
using Microsoft.Extensions.Logging;
using Postern.Core.Apps;

namespace Postern.App.Services;

/// <summary>
/// DEBUG BUILDS ONLY (the file compiles to nothing in Release). Approves registrations whose name is on the
/// --dev-auto-approve list, so smoke tests can run without a human clicking Allow. Everything else goes to the
/// real prompt. Logged as a warning every time, so it can't happen quietly.
/// </summary>
internal sealed class DevAutoApprovePrompt(IReadOnlyList<string> names, IApprovalPrompt fallback, ILogger log) : IApprovalPrompt
{
    public Task<bool> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (!names.Contains(request.Name, StringComparer.OrdinalIgnoreCase))
            return fallback.AskAsync(request, cancellationToken);
        log.LogWarning("DEV AUTO-APPROVE: '{Name}' ({Exe}) granted {Scopes} without asking", request.Name, request.ExePath,
            string.Join(", ", request.Scopes));
        return Task.FromResult(true);
    }
}
#endif
