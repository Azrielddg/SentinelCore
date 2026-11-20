using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SentinelCore.Demon1.Learning;

/// <summary>SQLite learning database. Feature values and profiles are DPAPI-protected per row.</summary>
public sealed class SqliteLearningProfileRepository : IAtomicLearningRepository, IDisposable
{
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private bool _disposed;

    public SqliteLearningProfileRepository(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS learning_profiles (
                sid_hash TEXT PRIMARY KEY NOT NULL,
                updated_utc TEXT NOT NULL,
                protected_profile BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS learning_observations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sid_hash TEXT NOT NULL,
                observed_utc TEXT NOT NULL,
                context_key TEXT NOT NULL,
                disposition TEXT NOT NULL,
                state TEXT NOT NULL,
                accepted_windows INTEGER NOT NULL,
                quality REAL NOT NULL,
                reason TEXT NOT NULL,
                protected_features BLOB NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_learning_observations_owner_date
                ON learning_observations(sid_hash, observed_utc DESC);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    public LearningProfile? Load(string sid)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_profile FROM learning_profiles WHERE sid_hash=$sid";
        command.Parameters.AddWithValue("$sid", HashSid(sid));
        var value = command.ExecuteScalar();
        if (value is not byte[] protectedBytes) return null;
        var json = CurrentUserDpapi.Unprotect(protectedBytes, sid);
        var profile = JsonSerializer.Deserialize<LearningProfile>(json, JsonOptions)
            ?? throw new InvalidDataException("Profil d’apprentissage vide ou illisible.");
        if (!StringComparer.Ordinal.Equals(profile.Sid, sid)) throw new InvalidDataException("Le SID du profil ne correspond pas au SID demandé.");
        return profile;
    }

    public void Save(string sid, LearningProfile profile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!StringComparer.Ordinal.Equals(sid, profile.Sid)) throw new ArgumentException("Le SID du profil est incohérent.", nameof(profile));
        var payload = CurrentUserDpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions), sid);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO learning_profiles(sid_hash,updated_utc,protected_profile) VALUES($sid,$updated,$payload)
            ON CONFLICT(sid_hash) DO UPDATE SET updated_utc=excluded.updated_utc, protected_profile=excluded.protected_profile;
            """;
        command.Parameters.AddWithValue("$sid", HashSid(sid));
        command.Parameters.AddWithValue("$updated", profile.UpdatedAtUtc.ToString("O"));
        command.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
        command.ExecuteNonQuery();
    }

    public void Append(LearningWindow window, LearningResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var protectedFeatures = CurrentUserDpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(window.Features, JsonOptions), window.Sid);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO learning_observations(sid_hash,observed_utc,context_key,disposition,state,accepted_windows,quality,reason,protected_features)
            VALUES($sid,$at,$context,$disposition,$state,$count,$quality,$reason,$features);
            """;
        command.Parameters.AddWithValue("$sid", HashSid(window.Sid));
        command.Parameters.AddWithValue("$at", window.ObservedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$context", window.ContextKey);
        command.Parameters.AddWithValue("$disposition", result.Disposition.ToString());
        command.Parameters.AddWithValue("$state", result.Report.State.ToString());
        command.Parameters.AddWithValue("$count", result.Report.AcceptedWindows);
        command.Parameters.AddWithValue("$quality", window.Quality);
        command.Parameters.AddWithValue("$reason", result.Reason);
        command.Parameters.Add("$features", SqliteType.Blob).Value = protectedFeatures;
        command.ExecuteNonQuery();
    }

    public void Record(LearningWindow window, LearningResult result, LearningProfile? updatedProfile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var sidHash = HashSid(window.Sid);
        if (updatedProfile is not null)
        {
            var protectedProfile = CurrentUserDpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(updatedProfile, JsonOptions), window.Sid);
            using var profileCommand = connection.CreateCommand();
            profileCommand.Transaction = transaction;
            profileCommand.CommandText = """
                INSERT INTO learning_profiles(sid_hash,updated_utc,protected_profile) VALUES($sid,$updated,$payload)
                ON CONFLICT(sid_hash) DO UPDATE SET updated_utc=excluded.updated_utc, protected_profile=excluded.protected_profile;
                """;
            profileCommand.Parameters.AddWithValue("$sid", sidHash);
            profileCommand.Parameters.AddWithValue("$updated", updatedProfile.UpdatedAtUtc.ToString("O"));
            profileCommand.Parameters.Add("$payload", SqliteType.Blob).Value = protectedProfile;
            profileCommand.ExecuteNonQuery();
        }

        var protectedFeatures = CurrentUserDpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(window.Features, JsonOptions), window.Sid);
        using var observationCommand = connection.CreateCommand();
        observationCommand.Transaction = transaction;
        observationCommand.CommandText = """
            INSERT INTO learning_observations(sid_hash,observed_utc,context_key,disposition,state,accepted_windows,quality,reason,protected_features)
            VALUES($sid,$at,$context,$disposition,$state,$count,$quality,$reason,$features);
            """;
        observationCommand.Parameters.AddWithValue("$sid", sidHash);
        observationCommand.Parameters.AddWithValue("$at", window.ObservedAtUtc.ToString("O"));
        observationCommand.Parameters.AddWithValue("$context", window.ContextKey);
        observationCommand.Parameters.AddWithValue("$disposition", result.Disposition.ToString());
        observationCommand.Parameters.AddWithValue("$state", result.Report.State.ToString());
        observationCommand.Parameters.AddWithValue("$count", result.Report.AcceptedWindows);
        observationCommand.Parameters.AddWithValue("$quality", window.Quality);
        observationCommand.Parameters.AddWithValue("$reason", result.Reason);
        observationCommand.Parameters.Add("$features", SqliteType.Blob).Value = protectedFeatures;
        observationCommand.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<LearningHistoryEntry> ReadRecent(string sid, int take = 100)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        take = Math.Clamp(take, 1, 1000);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,observed_utc,context_key,disposition,reason,state,accepted_windows,quality,protected_features
            FROM learning_observations WHERE sid_hash=$sid ORDER BY id DESC LIMIT $take;
            """;
        command.Parameters.AddWithValue("$sid", HashSid(sid));
        command.Parameters.AddWithValue("$take", take);
        using var reader = command.ExecuteReader();
        var rows = new List<LearningHistoryEntry>();
        while (reader.Read())
        {
            var protectedFeatures = (byte[])reader[8];
            var json = CurrentUserDpapi.Unprotect(protectedFeatures, sid);
            var features = JsonSerializer.Deserialize<Dictionary<string, double>>(json, JsonOptions) ?? new(StringComparer.Ordinal);
            rows.Add(new(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2),
                Enum.Parse<SampleDisposition>(reader.GetString(3)), reader.GetString(4), Enum.Parse<LearningState>(reader.GetString(5)),
                reader.GetInt32(6), reader.GetDouble(7), features));
        }
        return rows;
    }

    public long CountObservations(string sid)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM learning_observations WHERE sid_hash=$sid";
        command.Parameters.AddWithValue("$sid", HashSid(sid));
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private SqliteConnection Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static string HashSid(string sid) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
    public void Dispose() => _disposed = true;
}
