using System.Text.RegularExpressions;
using Postern.Broker.Credentials;

namespace Postern.Broker;

/// <summary>
/// Postern (Sandbox): a Postern to test against that can't leak anything. It only sends requests to loopback (where
/// testbed-upstream stands in for the real services), its credentials are made up, and it refuses one that looks real.
/// A new sandbox starts with a connection per service testbed-upstream speaks, each with a fake credential, plus
/// <see cref="EmptyConnection"/>, which never gets one.
/// </summary>
public static class Sandbox
{
    public const int UpstreamPort = 5599;
    public const string EmptyConnection = "jira-empty";

    public static string UpstreamOrigin => $"http://127.0.0.1:{UpstreamPort}";

    public static IReadOnlyList<ProviderEntry> SeedConnections { get; } =
    [
        new() { Name = "anthropic", Template = "anthropic", BaseUrl = $"{UpstreamOrigin}/anthropic" },
        new() { Name = "github", Template = "github", BaseUrl = $"{UpstreamOrigin}/github" },
        new() { Name = "github-work", Template = "github", BaseUrl = $"{UpstreamOrigin}/github" },
        new() { Name = "jira", Template = "jira", BaseUrl = $"{UpstreamOrigin}/jira" },
        new() { Name = EmptyConnection, Template = "jira", BaseUrl = $"{UpstreamOrigin}/jira" },
        new() { Name = "octopus", Template = "octopus", BaseUrl = $"{UpstreamOrigin}/octopus" },
        new() { Name = "slack", Template = "slack", BaseUrl = $"{UpstreamOrigin}/slack" },
    ];

    /// <summary>
    /// Writes the starting connections and their fake credentials, unless the data folder already has a
    /// providers.json. Returns whether it seeded.
    /// </summary>
    public static bool Seed(string dataDirectory, ICredentialStore credentials)
    {
        if (File.Exists(ProvidersFile.PathIn(dataDirectory)))
            return false;
        foreach (var connection in ProvidersFile.Resolve(SeedConnections))
        {
            if (connection.Name == EmptyConnection)
            {
                credentials.Delete(connection.CredentialTarget);
                continue;
            }
            var values = connection.Fields.ToDictionary(f => f.Name, f => $"sandbox-not-a-secret-{connection.Name}-{f.Name}");
            credentials.Write(connection.CredentialTarget, connection.EncodeCredential(values));
        }
        // Last, so a failed credential write seeds everything again on the next start.
        ProvidersFile.Write(dataDirectory, ProvidersFile.Serialize(SeedConnections));
        return true;
    }

    /// <summary>
    /// Back to a new sandbox: deletes its database, providers.json and every postern-sandbox: credential. Only while
    /// no Postern is using the folder, and only from the sandbox profile, so it can never reach Live or Dev data.
    /// </summary>
    public static void Reset(string dataDirectory)
    {
        if (!Profile.IsSandbox || Profile.CredentialNamespace is not { } ns)
            throw new InvalidOperationException("Only Postern (Sandbox) can reset the sandbox.");
        new WindowsCredentialStore().DeleteAll(ns + ":");
        foreach (var name in new[] { "postern.db", "postern.db-wal", "postern.db-shm", ProvidersFile.FileName })
            File.Delete(Path.Combine(dataDirectory, name));
    }

    // Long enough to be the real thing, so a made-up "sk-test" or "ghp_x" is still fine.
    static readonly (Regex Shape, string Kind)[] RealShapes =
    [
        (new(@"github_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled), "GitHub token"),
        (new(@"(?<![A-Za-z0-9])gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled), "GitHub token"),
        (new(@"(?<![A-Za-z0-9])glpat-[A-Za-z0-9_-]{16,}", RegexOptions.Compiled), "GitLab token"),
        (new(@"(?<![A-Za-z0-9])xox[abposr]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled), "Slack token"),
        (new(@"(?<![A-Za-z0-9])sk-ant-[A-Za-z0-9_-]{16,}", RegexOptions.Compiled), "Anthropic key"),
        (new(@"(?<![A-Za-z0-9])sk-(proj-)?[A-Za-z0-9_-]{20,}", RegexOptions.Compiled), "OpenAI key"),
        (new(@"(?<![A-Za-z0-9])ATATT[A-Za-z0-9_=-]{20,}", RegexOptions.Compiled), "Atlassian API token"),
        (new(@"(?<![A-Za-z0-9])API-[A-Z0-9]{20,}", RegexOptions.Compiled), "Octopus API key"),
    ];

    /// <summary>What kind of real credential <paramref name="secret"/> looks like, or null when it doesn't look like one.</summary>
    public static string? LooksReal(string secret) => RealShapes.FirstOrDefault(s => s.Shape.IsMatch(secret)).Kind;
}
