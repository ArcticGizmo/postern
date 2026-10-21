using Postern.Broker;
using Postern.Broker.Credentials;
using Postern.Broker.Storage;
using Postern.Core.Apps;
using Postern.Core.Audit;
using Postern.Core.Providers;
using Postern.Core.Scopes;

namespace Postern.Tests.Broker;

public sealed class StorageTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "postern-tests-" + Guid.NewGuid().ToString("N")[..8]);

    PosternDatabase Database()
    {
        var database = new PosternDatabase(Path.Combine(_dir, "postern.db"));
        database.Migrate();
        return database;
    }

    static AppRegistration Registration(RegistrationStatus status) => new()
    {
        Id = Secrets.NewId(),
        Name = "perch",
        ExePath = @"C:\Apps\Perch.exe",
        Signer = "CN=Perch",
        Scopes = [Scope.Parse("GET github/repos/*/*/pulls/**"), Scope.Parse("POST github/graphql")],
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        DecidedAt = DateTimeOffset.UtcNow,
        ClaimHash = Secrets.Hash(Secrets.NewClaim()),
        TokenHash = Secrets.Hash(Secrets.NewToken()),
    };

    [Fact]
    public void RegistrationsSurviveARestart()
    {
        var database = Database();
        var saved = Registration(RegistrationStatus.Approved);
        new SqliteRegistrationStore(database).Save(saved);

        var loaded = new SqliteRegistrationStore(database).Find(saved.Id)!;
        Assert.Equal(saved.Name, loaded.Name);
        Assert.Equal(saved.Signer, loaded.Signer);
        Assert.Equal(saved.TokenHash, loaded.TokenHash);
        Assert.Equal(saved.Scopes.Select(s => s.ToString()), loaded.Scopes.Select(s => s.ToString()));
        Assert.Equal(saved.Id, new SqliteRegistrationStore(database).FindByTokenHash(saved.TokenHash!)?.Id);
    }

    [Fact]
    public void PendingRegistrationsDoNotOutliveTheProcess()
    {
        var database = Database();
        var pending = Registration(RegistrationStatus.Pending);
        new SqliteRegistrationStore(database).Save(pending);
        Assert.Equal(RegistrationStatus.Denied, new SqliteRegistrationStore(database).Find(pending.Id)!.Status);
    }

    [Fact]
    public async Task AuditIsWrittenAndRead()
    {
        var database = Database();
        await using var audit = new SqliteAuditLog(database, TimeProvider.System);
        for (var i = 0; i < 5; i++)
            audit.Write(new AuditEntry(DateTimeOffset.UtcNow, "id", "perch", 42, @"C:\Apps\Perch.exe", "GET", "github", $"/n/{i}", 0, 200, "allowed", 12.5));
        await audit.FlushAsync();

        var recent = audit.Recent(3);
        Assert.Equal(["/n/4", "/n/3", "/n/2"], recent.Select(e => e.Path));
        Assert.Equal(12.5, recent[0].UpstreamMs);
        Assert.Equal(42u, recent[0].ProcessId);
    }

    [Fact]
    public void ProvidersFileAddsAndOverrides()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "providers.json"), """
            { "providers": [
                { "name": "octopus", "baseUrl": "https://octo.example.com", "auth": "header", "authHeader": "X-Octopus-ApiKey" },
                { "name": "github", "baseUrl": "https://github.example.com/api/v3" }
            ] }
            """);
        var providers = BrokerOptions.LoadProviders(_dir).ToDictionary(p => p.Name);
        Assert.Equal(AuthStyle.Header, providers["octopus"].Auth);
        Assert.Equal(("X-Octopus-ApiKey", "k"), providers["octopus"].AuthorizationFor("k"));
        Assert.Equal("github.example.com", providers["github"].BaseUrl.Host);
    }

    [Fact]
    public void PlainHttpIsOnlyForLoopback()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "providers.json"), """{ "providers": [ { "name": "x", "baseUrl": "http://example.com" } ] }""");
        Assert.Throws<ArgumentException>(() => BrokerOptions.LoadProviders(_dir));
    }

    [Fact]
    public void WindowsCredentialManagerRoundTrip()
    {
        var store = new WindowsCredentialStore();
        var target = $"postern-test:{Guid.NewGuid():N}";
        try
        {
            Assert.Null(store.Read(target));
            store.Write(target, "s3cret-ü");
            Assert.Equal("s3cret-ü", store.Read(target));
        }
        finally
        {
            store.Delete(target);
        }
        Assert.Null(store.Read(target));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
