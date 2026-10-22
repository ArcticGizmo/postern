using Postern.Core.Scopes;

namespace Postern.Core.Apps;

public enum RegistrationStatus
{
    /// <summary>Waiting for the user to approve or deny.</summary>
    Pending,
    /// <summary>Approved; the token (once claimed) works.</summary>
    Approved,
    /// <summary>The user said no, or didn't answer in time.</summary>
    Denied,
    /// <summary>Was approved; the user withdrew it.</summary>
    Revoked,
    /// <summary>Was approved; a newer approved registration for the same app replaced it.</summary>
    Superseded,
}

/// <summary>
/// One app's access, as approved by the user. Identity is name + exe path (+ signer when the exe is signed).
/// Postern keeps only hashes of the claim code and the app token.
/// </summary>
public sealed record AppRegistration
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ExePath { get; init; }
    public string? Signer { get; init; }
    public required IReadOnlyList<Scope> Scopes { get; init; }
    public required RegistrationStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>SHA-256 of the one-time claim code the registering app received.</summary>
    public required string ClaimHash { get; init; }

    /// <summary>SHA-256 of the app token; null until the app claims it.</summary>
    public string? TokenHash { get; init; }
}
