using Postern.Broker.Credentials;
using Postern.Core.Providers;

namespace Postern.Broker;

/// <summary>
/// The live set of connections the proxy routes by. Edits in the app replace it in place, so a new or changed
/// connection works on the next request without a restart. Requests read one snapshot, never a half-applied edit.
/// </summary>
public sealed class Connections
{
    readonly string _dataDirectory;
    readonly ICredentialStore _credentials;
    readonly Lock _gate = new();
    IReadOnlyDictionary<string, ProviderDefinition> _byName;

    internal Connections(IReadOnlyList<ProviderDefinition> initial, string dataDirectory, ICredentialStore credentials, string? loadError)
    {
        foreach (var connection in initial)
            connection.Validate();
        _byName = initial.ToDictionary(p => p.Name, StringComparer.Ordinal);
        _dataDirectory = dataDirectory;
        _credentials = credentials;
        LoadError = loadError;
    }

    public IReadOnlyList<ProviderDefinition> All => [.. _byName.Values.OrderBy(p => p.Name, StringComparer.Ordinal)];

    public ProviderDefinition? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Why providers.json couldn't be used at startup (Postern then runs with the built-ins only), or null.</summary>
    public string? LoadError { get; private set; }

    /// <summary>providers.json as it stands, for the in-app editor.</summary>
    public string ReadJson() => ProvidersFile.ReadText(_dataDirectory);

    /// <summary>
    /// Validates <paramref name="json"/>, writes it to providers.json and switches to it. Throws
    /// <see cref="ArgumentException"/> and changes nothing when it doesn't validate. Credentials of connections it
    /// drops are deleted, so no secret outlives its connection. One that moves host keeps its credential, but the
    /// proxy won't send it anywhere but the origin it was entered for (see <see cref="ProviderDefinition.EncodeCredential"/>).
    /// </summary>
    public void Save(string json)
    {
        var next = ProvidersFile.Resolve(ProvidersFile.Parse(json));
        lock (_gate)
        {
            ProvidersFile.Write(_dataDirectory, json);
            foreach (var dropped in _byName.Keys.Where(name => next.All(p => p.Name != name)))
                _credentials.Delete(ProviderDefinition.TargetFor(dropped));
            _byName = next.ToDictionary(p => p.Name, StringComparer.Ordinal);
            LoadError = null;
        }
    }

    /// <summary>Adds one connection to providers.json, keeping everything already there.</summary>
    public void Add(ProviderEntry entry)
    {
        if (Find(entry.Name) is not null)
            throw new ArgumentException($"There's already a connection called '{entry.Name}'.");
        var entries = ProvidersFile.Read(_dataDirectory).Append(entry);
        Save(ProvidersFile.Serialize(entries));
    }

    /// <summary>Takes a connection out of providers.json and deletes its credential. The built-in github can only be reset to its default.</summary>
    public void Remove(string name)
    {
        var entries = ProvidersFile.Read(_dataDirectory).Where(e => e.Name != name);
        Save(ProvidersFile.Serialize(entries));
    }
}
