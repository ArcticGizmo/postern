using System.Collections.Concurrent;

namespace Postern.Core.Apps;

public interface IRegistrationStore
{
    AppRegistration? Find(string id);
    AppRegistration? FindByTokenHash(string tokenHash);
    IReadOnlyList<AppRegistration> All();
    void Save(AppRegistration registration);
}

/// <summary>For tests and throwaway brokers.</summary>
public sealed class InMemoryRegistrationStore : IRegistrationStore
{
    readonly ConcurrentDictionary<string, AppRegistration> _byId = new();

    public AppRegistration? Find(string id) => _byId.GetValueOrDefault(id);

    public AppRegistration? FindByTokenHash(string tokenHash) =>
        _byId.Values.FirstOrDefault(r => r.TokenHash is not null && string.Equals(r.TokenHash, tokenHash, StringComparison.Ordinal));

    public IReadOnlyList<AppRegistration> All() => [.. _byId.Values.OrderBy(r => r.CreatedAt)];

    public void Save(AppRegistration registration) => _byId[registration.Id] = registration;
}
