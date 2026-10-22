using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Postern.Core.Apps;
using Postern.Core.Scopes;

namespace Postern.Broker.Storage;

/// <summary>
/// Registrations are few and read on every request, so they live in memory and every save writes through to SQLite.
/// </summary>
internal sealed class SqliteRegistrationStore : IRegistrationStore
{
    readonly PosternDatabase _database;
    readonly ConcurrentDictionary<string, AppRegistration> _byId = new();

    public SqliteRegistrationStore(PosternDatabase database)
    {
        _database = database;
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, exe_path, signer, scopes, status, created_at, decided_at, last_used_at, claim_hash, token_hash FROM registrations";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var registration = new AppRegistration
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                ExePath = reader.GetString(2),
                Signer = reader.IsDBNull(3) ? null : reader.GetString(3),
                Scopes = [.. JsonSerializer.Deserialize<string[]>(reader.GetString(4))!.Select(Scope.Parse)],
                Status = Enum.Parse<RegistrationStatus>(reader.GetString(5)),
                CreatedAt = Date(reader.GetString(6)),
                DecidedAt = reader.IsDBNull(7) ? null : Date(reader.GetString(7)),
                LastUsedAt = reader.IsDBNull(8) ? null : Date(reader.GetString(8)),
                ClaimHash = reader.GetString(9),
                TokenHash = reader.IsDBNull(10) ? null : reader.GetString(10),
            };
            // A pending registration can't outlive the process that was waiting on its prompt.
            if (registration.Status == RegistrationStatus.Pending)
                registration = registration with { Status = RegistrationStatus.Denied };
            _byId[registration.Id] = registration;
        }
    }

    public AppRegistration? Find(string id) => _byId.GetValueOrDefault(id);

    public AppRegistration? FindByTokenHash(string tokenHash) =>
        _byId.Values.FirstOrDefault(r => r.TokenHash is not null && string.Equals(r.TokenHash, tokenHash, StringComparison.Ordinal));

    public IReadOnlyList<AppRegistration> All() => [.. _byId.Values.OrderBy(r => r.CreatedAt)];

    public void Save(AppRegistration r)
    {
        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO registrations (id, name, exe_path, signer, scopes, status, created_at, decided_at, last_used_at, claim_hash, token_hash)
                VALUES ($id, $name, $exe, $signer, $scopes, $status, $created, $decided, $used, $claim, $token)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name, exe_path = excluded.exe_path, signer = excluded.signer, scopes = excluded.scopes,
                    status = excluded.status, decided_at = excluded.decided_at, last_used_at = excluded.last_used_at,
                    claim_hash = excluded.claim_hash, token_hash = excluded.token_hash
                """;
            command.Parameters.AddWithValue("$id", r.Id);
            command.Parameters.AddWithValue("$name", r.Name);
            command.Parameters.AddWithValue("$exe", r.ExePath);
            command.Parameters.AddWithValue("$signer", (object?)r.Signer ?? DBNull.Value);
            command.Parameters.AddWithValue("$scopes", JsonSerializer.Serialize(r.Scopes.Select(s => s.ToString())));
            command.Parameters.AddWithValue("$status", r.Status.ToString());
            command.Parameters.AddWithValue("$created", r.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$decided", (object?)r.DecidedAt?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$used", (object?)r.LastUsedAt?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$claim", r.ClaimHash);
            command.Parameters.AddWithValue("$token", (object?)r.TokenHash ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        _byId[r.Id] = r;
    }

    static DateTimeOffset Date(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
