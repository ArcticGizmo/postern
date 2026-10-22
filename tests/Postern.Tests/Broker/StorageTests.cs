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
    public void DevCredentialsAreStoredApartFromTheRealOnes()
    {
        var real = new InMemoryCredentialStore();
        var dev = new NamespacedCredentialStore(real, "postern-dev");
        real.Write("postern:github:default", "real-pat");

        Assert.Null(dev.Read("postern:github:default"));
        dev.Write("postern:github:default", "dev-pat");
        Assert.Equal("dev-pat", real.Read("postern-dev:github:default"));
        Assert.Equal("real-pat", real.Read("postern:github:default"));

        Assert.True(dev.Delete("postern:github:default"));
        Assert.Equal("real-pat", real.Read("postern:github:default"));
    }

    [Fact]
    public void UninstallSweepDeletesOnlyItsOwnPrefix()
    {
        var store = new WindowsCredentialStore();
        var prefix = $"postern-test-{Guid.NewGuid():N}:";
        var neighbour = $"postern-test-{Guid.NewGuid():N}x:github:default"; // shares the start of the name, not the prefix
        try
        {
            store.Write(prefix + "github:default", "a");
            store.Write(prefix + "octopus:default", "b");
            store.Write(neighbour, "c");

            Assert.Equal(2, store.DeleteAll(prefix).Count);
            Assert.Null(store.Read(prefix + "github:default"));
            Assert.Equal("c", store.Read(neighbour));
            Assert.Empty(store.DeleteAll(prefix));
        }
        finally
        {
            store.Delete(prefix + "github:default");
            store.Delete(prefix + "octopus:default");
            store.Delete(neighbour);
        }
    }

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
        Assert.Equal([new Injected(false, "X-Octopus-ApiKey", "k")], providers["octopus"].Apply(new Dictionary<string, string> { ["secret"] = "k" }));
        Assert.Equal("github.example.com", providers["github"].BaseUrl.Host);
    }

    [Fact]
    public void ProvidersFileTakesTemplatesAndCustomConnections()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "providers.json"), """
            { "providers": [
                // comments and trailing commas are fine: people do edit this by hand
                { "name": "github-work", "template": "github" },
                { "name": "jira", "template": "jira", "baseUrl": "https://contoso.atlassian.net" },
                { "name": "trello", "baseUrl": "https://api.trello.com/1",
                  "fields": [ { "name": "key", "label": "API key" }, { "name": "token", "label": "Token" } ],
                  "inject": [ { "query": "key", "value": "{key}" }, { "query": "token", "value": "{token}" } ] },
            ] }
            """);
        var providers = BrokerOptions.LoadProviders(_dir).ToDictionary(p => p.Name);

        Assert.Equal(["github", "github-work", "jira", "trello"], providers.Keys.Order());
        Assert.Equal("https://api.github.com/", providers["github-work"].BaseUrl.ToString());
        Assert.Equal(["email", "token"], providers["jira"].Fields.Select(f => f.Name));
        Assert.Equal(
            [new Injected(true, "key", "k"), new Injected(true, "token", "t")],
            providers["trello"].Apply(new Dictionary<string, string> { ["key"] = "k", ["token"] = "t" }));
        Assert.Null(providers["trello"].Template);
    }

    [Theory]
    [InlineData("""{ "providers": [ { "name": "a", "template": "nope" } ] }""", "no template 'nope'")]
    [InlineData("""{ "providers": [ { "name": "a", "template": "jira" } ] }""", "Site is required")]
    [InlineData("""{ "providers": [ { "name": "a", "template": "github" }, { "name": "a", "template": "gitlab" } ] }""", "listed twice")]
    [InlineData("""{ "providers": [ { "name": "a" } ] }""", "needs a template or a baseUrl")]
    [InlineData("""{ "providers": [ { "name": "a", "baseUrl": "not a url" } ] }""", "Connection 'a'")]
    [InlineData("""{ "providers": [ """, "Not valid JSON")]
    public void ProvidersFileProblemsNameTheConnection(string json, string expected)
    {
        var error = Assert.Throws<ArgumentException>(() => ProvidersFile.Resolve(ProvidersFile.Parse(json)));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void ProvidersFileRoundTrips()
    {
        ProviderEntry[] entries =
        [
            new() { Name = "work", Template = "github" },
            new() { Name = "custom", BaseUrl = "https://api.example.com", Fields = [new("token", "Token")], Inject = [Injection.ToHeader("X-Key", "{token}")] },
        ];
        var json = ProvidersFile.Serialize(entries);
        Assert.DoesNotContain("authHeader", json); // nulls are left out, so the file stays readable
        var back = ProvidersFile.Resolve(ProvidersFile.Parse(json)).ToDictionary(p => p.Name);
        Assert.Equal([new Injected(false, "X-Key", "t")], back["custom"].Apply(new Dictionary<string, string> { ["token"] = "t" }));
        Assert.Equal("github", back["work"].Template);
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
