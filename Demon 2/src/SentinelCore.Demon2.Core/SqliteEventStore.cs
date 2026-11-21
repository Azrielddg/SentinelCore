using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Demon2.Core;

public sealed class SqliteEventStore : IEventStore, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly TimeSpan _eventRetention;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SqliteEventStore(string databasePath, TimeSpan? eventRetention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        _eventRetention = eventRetention ?? TimeSpan.FromDays(7);
        if (_eventRetention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(eventRetention));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                PRAGMA secure_delete=ON;
                CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS events(
                    event_id TEXT PRIMARY KEY, source TEXT NOT NULL, source_event_id TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL, process_guid TEXT NULL, process_id INTEGER NULL,
                    payload_json TEXT NOT NULL, UNIQUE(source, source_event_id));
                CREATE INDEX IF NOT EXISTS ix_events_process_time ON events(process_guid, process_id, timestamp_utc);
                CREATE TABLE IF NOT EXISTS source_cursors(source TEXT PRIMARY KEY, cursor TEXT NOT NULL, updated_utc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS findings(
                    finding_id TEXT PRIMARY KEY, deduplication_key TEXT NOT NULL UNIQUE,
                    timestamp_utc TEXT NOT NULL, expires_utc TEXT NOT NULL, payload_json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS telemetry_gaps(
                    gap_id TEXT PRIMARY KEY, observed_utc TEXT NOT NULL, payload_json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS outbox(
                    message_id TEXT PRIMARY KEY, created_utc TEXT NOT NULL, expires_utc TEXT NOT NULL,
                    next_attempt_utc TEXT NOT NULL, attempt_count INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'pending', last_error TEXT NULL, payload_json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_outbox_pending ON outbox(status,next_attempt_utc,created_utc);
                INSERT OR IGNORE INTO schema_migrations(version,applied_utc) VALUES(1,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    public async Task<bool> SaveEventAsync(CanonicalEvent item, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO events(event_id,source,source_event_id,timestamp_utc,process_guid,process_id,payload_json) VALUES($id,$source,$sourceId,$time,$guid,$pid,$json);";
        command.Parameters.AddWithValue("$id", item.EventId);
        command.Parameters.AddWithValue("$source", item.Source);
        command.Parameters.AddWithValue("$sourceId", item.SourceEventId);
        command.Parameters.AddWithValue("$time", item.TimestampUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$guid", (object?)item.ProcessGuid ?? DBNull.Value);
        command.Parameters.AddWithValue("$pid", (object?)item.ProcessId ?? DBNull.Value);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item, JsonOptions));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> SaveFindingAsync(ThreatFinding finding, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var key = MakeDeduplicationKey(finding);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO findings(finding_id,deduplication_key,timestamp_utc,expires_utc,payload_json) VALUES($id,$key,$time,$expires,$json);";
        command.Parameters.AddWithValue("$id", finding.FindingId);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$time", finding.FirstSeenUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$expires", finding.ExpiresAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(finding, JsonOptions));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<ThreatFinding>> LoadFindingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM findings ORDER BY timestamp_utc,finding_id;";
        var result = new List<ThreatFinding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (JsonSerializer.Deserialize<ThreatFinding>(reader.GetString(0), JsonOptions) is { } finding) result.Add(finding);
        return result;
    }

    public async Task<bool> SaveTelemetryGapAsync(TelemetryGap gap, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO telemetry_gaps(gap_id,observed_utc,payload_json) VALUES($id,$observed,$json);";
        command.Parameters.AddWithValue("$id", gap.GapId);
        command.Parameters.AddWithValue("$observed", gap.ObservedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(gap, JsonOptions));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<TelemetryGap>> LoadTelemetryGapsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM telemetry_gaps ORDER BY observed_utc,gap_id;";
        var result = new List<TelemetryGap>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (JsonSerializer.Deserialize<TelemetryGap>(reader.GetString(0), JsonOptions) is { } gap) result.Add(gap);
        return result;
    }

    public async Task<bool> HasFindingAsync(string deduplicationKey, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM findings WHERE deduplication_key=$key);";
        command.Parameters.AddWithValue("$key", deduplicationKey);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    public async Task SaveCursorAsync(string source, string cursor, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO source_cursors(source,cursor,updated_utc) VALUES($source,$cursor,$time) ON CONFLICT(source) DO UPDATE SET cursor=excluded.cursor,updated_utc=excluded.updated_utc;";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$cursor", cursor);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> LoadCursorAsync(string source, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cursor FROM source_cursors WHERE source=$source;";
        command.Parameters.AddWithValue("$source", source);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CanonicalEvent>> LoadEventsAsync(string? processGuid, int? processId, DateTimeOffset fromUtc, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM events WHERE timestamp_utc >= $from AND (($guid IS NOT NULL AND process_guid=$guid) OR ($guid IS NULL AND $pid IS NOT NULL AND process_id=$pid) OR ($guid IS NULL AND $pid IS NULL)) ORDER BY timestamp_utc;";
        command.Parameters.AddWithValue("$from", fromUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$guid", (object?)processGuid ?? DBNull.Value);
        command.Parameters.AddWithValue("$pid", (object?)processId ?? DBNull.Value);
        var result = new List<CanonicalEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (JsonSerializer.Deserialize<CanonicalEvent>(reader.GetString(0), JsonOptions) is { } item) result.Add(item);
        return result;
    }

    public async Task<IReadOnlyList<CanonicalEvent>> LoadEventsByIdsAsync(IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken = default)
    {
        if (eventIds.Count == 0) return [];
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var result = new List<CanonicalEvent>();
        foreach (var batch in eventIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Chunk(500))
        {
            await using var command = connection.CreateCommand();
            var names = new List<string>(batch.Length);
            for (var i = 0; i < batch.Length; i++) { var name = "$id" + i; names.Add(name); command.Parameters.AddWithValue(name, batch[i]); }
            command.CommandText = $"SELECT payload_json FROM events WHERE event_id IN ({string.Join(',', names)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (JsonSerializer.Deserialize<CanonicalEvent>(reader.GetString(0), JsonOptions) is { } item) result.Add(item);
        }
        return result;
    }

    public async Task<IReadOnlyList<CanonicalEvent>> LoadRecentEventsAsync(DateTimeOffset fromUtc, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM events WHERE timestamp_utc >= $from ORDER BY timestamp_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$from", fromUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<CanonicalEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (JsonSerializer.Deserialize<CanonicalEvent>(reader.GetString(0), JsonOptions) is { } item) result.Add(item);
        result.Reverse();
        return result;
    }

    public async Task PruneExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM events WHERE timestamp_utc < $cutoff; DELETE FROM findings WHERE expires_utc <= $now; DELETE FROM telemetry_gaps WHERE observed_utc < $cutoff; DELETE FROM outbox WHERE status IN ('delivered','expired','failed') AND expires_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", nowUtc.Subtract(_eventRetention).ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await checkpoint.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> EnqueueMessageAsync(InterDaemonEnvelope message, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var limit = message.MessageType == "HEARTBEAT" ? 4064 : 4096;
        command.CommandText = """
            INSERT OR IGNORE INTO outbox(message_id,created_utc,expires_utc,next_attempt_utc,payload_json)
            SELECT $id,$created,$expires,$created,$json
            WHERE (SELECT COUNT(*) FROM outbox WHERE status='pending') < $limit;
            """;
        command.Parameters.AddWithValue("$id", message.MessageId.ToString("D"));
        command.Parameters.AddWithValue("$created", message.TimestampUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$expires", message.ExpiresAtUtc.ToUniversalTime().ToString("O"));
        var json = JsonSerializer.Serialize(message, JsonOptions);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$limit", limit);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1) return true;
        await using var existing = connection.CreateCommand();
        existing.CommandText = "SELECT payload_json FROM outbox WHERE message_id=$id;";
        existing.Parameters.AddWithValue("$id", message.MessageId.ToString("D"));
        var storedJson = (string?)await existing.ExecuteScalarAsync(cancellationToken);
        if (storedJson is not null)
        {
            if (string.Equals(storedJson, json, StringComparison.Ordinal)) return false;
            throw new InvalidOperationException($"Outbox messageId collision with different content: {message.MessageId}.");
        }
        return false;
    }

    public async Task<int> GetPendingMessageCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM outbox WHERE status='pending';";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<InterDaemonEnvelope>> LoadPendingMessagesAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM outbox WHERE status='pending' AND next_attempt_utc <= $now AND expires_utc > $now ORDER BY created_utc LIMIT $limit;";
        command.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<InterDaemonEnvelope>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (JsonSerializer.Deserialize<InterDaemonEnvelope>(reader.GetString(0), JsonOptions) is { } item) result.Add(item);
        return result;
    }

    public async Task MarkMessageDeliveredAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE outbox SET status='delivered',last_error=NULL WHERE message_id=$id;";
        command.Parameters.AddWithValue("$id", messageId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ScheduleMessageRetryAsync(Guid messageId, string error, DateTimeOffset nextAttemptUtc, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE outbox SET attempt_count=attempt_count+1,next_attempt_utc=$next,last_error=$error,status=CASE WHEN attempt_count+1 >= 3 THEN 'failed' ELSE 'pending' END WHERE message_id=$id AND status='pending';";
        command.Parameters.AddWithValue("$next", nextAttemptUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$error", error.Length > 512 ? error[..512] : error);
        command.Parameters.AddWithValue("$id", messageId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InterDaemonEnvelope>> ExpireMessagesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var messages = new List<InterDaemonEnvelope>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText = "SELECT payload_json FROM outbox WHERE status IN ('pending','failed') AND expires_utc <= $now;";
            select.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (JsonSerializer.Deserialize<InterDaemonEnvelope>(reader.GetString(0), JsonOptions) is { } item) messages.Add(item);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE outbox SET status='expired' WHERE status IN ('pending','failed') AND expires_utc <= $now;";
            update.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return messages;
    }

    public static string MakeDeduplicationKey(ThreatFinding finding) =>
        finding.RuleId + ":" + string.Join(',', finding.EvidenceRefs.Order(StringComparer.Ordinal));

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete=ON; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private Task EnsureInitializedAsync(CancellationToken ct) => _initialized ? Task.CompletedTask : InitializeAsync(ct);
    public ValueTask DisposeAsync() { _initializeLock.Dispose(); return ValueTask.CompletedTask; }
}
