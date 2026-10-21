using System.Globalization;
using System.Threading.Channels;
using Postern.Core.Audit;

namespace Postern.Broker.Storage;

/// <summary>
/// Writes go into a bounded channel and a background task batches them into SQLite, so the request path never waits
/// on disk. If the writer falls 10,000 entries behind, the oldest unwritten entries are dropped (and counted).
/// </summary>
internal sealed class SqliteAuditLog : IAuditLog, IAsyncDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    readonly PosternDatabase _database;
    readonly Channel<AuditEntry> _queue = Channel.CreateBounded<AuditEntry>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    readonly Task _writer;

    public SqliteAuditLog(PosternDatabase database, TimeProvider time)
    {
        _database = database;
        Prune(time.GetUtcNow() - Retention);
        _writer = Task.Run(WriteLoop);
    }

    public void Write(AuditEntry entry) => _queue.Writer.TryWrite(entry);

    public IReadOnlyList<AuditEntry> Recent(int count)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT at, app_id, app_name, pid, exe_path, method, provider, path, query_length, status, outcome, upstream_ms
            FROM audit ORDER BY id DESC LIMIT $count
            """;
        command.Parameters.AddWithValue("$count", count);
        using var reader = command.ExecuteReader();
        var entries = new List<AuditEntry>();
        while (reader.Read())
        {
            entries.Add(new AuditEntry(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                (uint)reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetDouble(11)));
        }
        return entries;
    }

    /// <summary>Waits until everything queued so far is on disk (tests, shutdown).</summary>
    public async Task FlushAsync()
    {
        while (_queue.Reader.Count > 0)
            await Task.Delay(10);
        await Task.Delay(20);
    }

    async Task WriteLoop()
    {
        var batch = new List<AuditEntry>(256);
        while (await _queue.Reader.WaitToReadAsync())
        {
            batch.Clear();
            while (batch.Count < 256 && _queue.Reader.TryRead(out var entry))
                batch.Add(entry);
            try
            {
                Insert(batch);
            }
            catch
            {
                // Audit is best-effort: a locked or full disk must not take the broker down.
            }
        }
    }

    void Insert(List<AuditEntry> batch)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit (at, app_id, app_name, pid, exe_path, method, provider, path, query_length, status, outcome, upstream_ms)
            VALUES ($at, $appId, $appName, $pid, $exe, $method, $provider, $path, $query, $status, $outcome, $ms)
            """;
        var p = new[] { "$at", "$appId", "$appName", "$pid", "$exe", "$method", "$provider", "$path", "$query", "$status", "$outcome", "$ms" }
            .Select(name =>
            {
                var parameter = command.CreateParameter(); // type inferred from each value at bind time
                parameter.ParameterName = name;
                command.Parameters.Add(parameter);
                return parameter;
            }).ToArray();
        foreach (var e in batch)
        {
            p[0].Value = e.At.ToString("O");
            p[1].Value = (object?)e.AppId ?? DBNull.Value;
            p[2].Value = (object?)e.AppName ?? DBNull.Value;
            p[3].Value = (long)e.ProcessId;
            p[4].Value = (object?)e.ExePath ?? DBNull.Value;
            p[5].Value = e.Method;
            p[6].Value = e.Provider;
            p[7].Value = e.Path;
            p[8].Value = e.QueryLength;
            p[9].Value = e.Status;
            p[10].Value = e.Outcome;
            p[11].Value = (object?)e.UpstreamMs ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    void Prune(DateTimeOffset before)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM audit WHERE at < $before";
        command.Parameters.AddWithValue("$before", before.ToString("O"));
        command.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer;
    }
}
