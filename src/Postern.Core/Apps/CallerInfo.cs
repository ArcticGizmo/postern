namespace Postern.Core.Apps;

/// <summary>The process at the other end of the pipe, as Windows reports it. Resolved by the broker per connection.</summary>
public sealed record CallerInfo(uint ProcessId, string? ExePath, bool SignatureTrusted, string? Signer)
{
    /// <summary>True when this caller is the process a registration was made for.</summary>
    public bool Matches(AppRegistration registration)
    {
        if (ExePath is null || !string.Equals(Path.GetFullPath(ExePath), Path.GetFullPath(registration.ExePath), StringComparison.OrdinalIgnoreCase))
            return false;
        // Registered while signed: still has to be signed by the same publisher (catches a swapped binary).
        return registration.Signer is null || (SignatureTrusted && string.Equals(Signer, registration.Signer, StringComparison.Ordinal));
    }
}
