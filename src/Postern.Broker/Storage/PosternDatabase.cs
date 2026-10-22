using Microsoft.Data.Sqlite;

namespace Postern.Broker.Storage;

/// <summary>One SQLite file (WAL) for registrations and the audit log.</summary>
internal sealed class PosternDatabase(string path)
{
    readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Migrate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS registrations (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                exe_path TEXT NOT NULL,
                signer TEXT,
                scopes TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                decided_at TEXT,
                last_used_at TEXT,
                claim_hash TEXT NOT NULL,
                token_hash TEXT
            );
            CREATE TABLE IF NOT EXISTS audit (
                id INTEGER PRIMARY KEY,
                at TEXT NOT NULL,
                app_id TEXT,
                app_name TEXT,
                pid INTEGER NOT NULL,
                exe_path TEXT,
                method TEXT NOT NULL,
                provider TEXT NOT NULL,
                path TEXT NOT NULL,
                query_length INTEGER NOT NULL,
                status INTEGER NOT NULL,
                outcome TEXT NOT NULL,
                upstream_ms REAL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_at ON audit(at);
            """;
        command.ExecuteNonQuery();
    }
}
