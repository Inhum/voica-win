using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Voica;

/// <summary>One transcription history record (spec §7).</summary>
public sealed record Transcription(
    long Id,
    DateTimeOffset CreatedAt,
    string Text,
    string? Language,
    double? Duration,
    string? AudioFilename,
    string? Model,
    string? RawText = null)
{
    /// <summary>Full path to the audio file, or null if none is stored.</summary>
    public string? AudioPath => AudioFilename is null ? null : Path.Combine(Paths.AudioDir, AudioFilename);
}

/// <summary>
/// SQLite history (spec §7). All database access is serialized through a single connection and a
/// lock (the Windows equivalent of the macOS serial queue), so concurrent callers are safe.
/// </summary>
public sealed class Store
{
    public static Store Shared { get; } = new();

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;

    private Store()
    {
        Paths.EnsureCreated();
        _connection = new SqliteConnection($"Data Source={Paths.DatabaseFile}");
        _connection.Open();
        Initialize();
    }

    private void Initialize()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS transcriptions (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at     INTEGER NOT NULL,
                text           TEXT NOT NULL,
                language       TEXT,
                duration_sec   REAL,
                audio_filename TEXT,
                model          TEXT,
                raw_text       TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        AddRawTextColumn(_connection);
    }

    /// <summary>
    /// Databases created before raw_text existed keep their old shape — CREATE TABLE IF NOT EXISTS
    /// leaves them alone (spec §7). Adding the column throws "duplicate column name" on a fresh
    /// database, which is the normal outcome, not a failure.
    /// </summary>
    private static void AddRawTextColumn(SqliteConnection connection)
    {
        try
        {
            using var migrate = connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE transcriptions ADD COLUMN raw_text TEXT;";
            migrate.ExecuteNonQuery();
            Log.Info("history: added the raw_text column to an existing database");
        }
        catch (SqliteException)
        {
            // Already there.
        }
    }

    /// <summary>Runs the raw_text migration against a database file (self-test entry point).</summary>
    internal static bool MigrateRawText(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        AddRawTextColumn(connection);
        return HasRawTextColumn(connection);
    }

    /// <summary>True when the transcriptions table has the raw_text column.</summary>
    internal static bool HasRawTextColumn(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        return HasRawTextColumn(connection);
    }

    private static bool HasRawTextColumn(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('transcriptions') WHERE name = 'raw_text';";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    /// <summary>
    /// Inserts a transcription. If <paramref name="audioTempPath"/> is provided and "store audio"
    /// is enabled (spec §8), the file is kept and its name recorded; otherwise it is deleted and
    /// the record stores no audio. Returns the new row id, or null on failure.
    /// </summary>
    /// <param name="rawText">
    /// Engine text before term correction, stored ONLY when the correction changed something
    /// (spec §7); when it matches the final text this is null — two identical copies are useless.
    /// </param>
    public long? Insert(string text, string? language, double? duration, string? model, string? audioTempPath,
        string? rawText = null)
    {
        lock (_gate)
        {
            string? audioFilename = null;
            if (audioTempPath is not null && File.Exists(audioTempPath))
            {
                if (Prefs.StoreAudio)
                    audioFilename = Path.GetFileName(audioTempPath);   // already lives in AudioDir
                else
                    TryDelete(audioTempPath);
            }

            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO transcriptions (created_at, text, language, duration_sec, audio_filename, model, raw_text)
                    VALUES ($created_at, $text, $language, $duration, $audio, $model, $raw);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$text", text);
                cmd.Parameters.AddWithValue("$language", (object?)language ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$duration", (object?)duration ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$audio", (object?)audioFilename ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
                // Same text on both sides means the correction changed nothing — keep it null.
                var raw = string.IsNullOrEmpty(rawText) || rawText == text ? null : rawText;
                cmd.Parameters.AddWithValue("$raw", (object?)raw ?? DBNull.Value);
                var id = (long)(cmd.ExecuteScalar() ?? 0L);
                return id;
            }
            catch (SqliteException)
            {
                return null;
            }
        }
    }

    /// <summary>All records, newest first.</summary>
    public IReadOnlyList<Transcription> All()
    {
        lock (_gate)
        {
            var list = new List<Transcription>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, created_at, text, language, duration_sec, audio_filename, model, raw_text
                FROM transcriptions
                ORDER BY created_at DESC, id DESC;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(ReadRow(reader));
            return list;
        }
    }

    /// <summary>Deletes a record and its audio file (if any).</summary>
    public void Delete(long id)
    {
        lock (_gate)
        {
            string? audioFilename = null;
            using (var sel = _connection.CreateCommand())
            {
                sel.CommandText = "SELECT audio_filename FROM transcriptions WHERE id = $id;";
                sel.Parameters.AddWithValue("$id", id);
                var val = sel.ExecuteScalar();
                if (val is string s) audioFilename = s;
            }

            using (var del = _connection.CreateCommand())
            {
                del.CommandText = "DELETE FROM transcriptions WHERE id = $id;";
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
            }

            if (audioFilename is not null)
                TryDelete(Path.Combine(Paths.AudioDir, audioFilename));
        }
    }

    /// <summary>
    /// Deletes several records at once (spec §7): one table read for the whole batch and a single
    /// transaction, rather than a loop of single deletes. Their audio files go too.
    /// Returns how many rows were removed.
    /// </summary>
    public int DeleteMany(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return 0;

        lock (_gate)
        {
            var idList = string.Join(",", ids);   // ids are longs from our own rows — safe to inline
            var audioFiles = new List<string>();

            using (var sel = _connection.CreateCommand())
            {
                sel.CommandText = $"SELECT audio_filename FROM transcriptions WHERE id IN ({idList}) AND audio_filename IS NOT NULL;";
                using var reader = sel.ExecuteReader();
                while (reader.Read())
                    audioFiles.Add(reader.GetString(0));
            }

            int deleted;
            using (var tx = _connection.BeginTransaction())
            {
                using var del = _connection.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM transcriptions WHERE id IN ({idList});";
                deleted = del.ExecuteNonQuery();
                tx.Commit();
            }

            foreach (var filename in audioFiles)
                TryDelete(Path.Combine(Paths.AudioDir, filename));

            return deleted;
        }
    }

    /// <summary>Number of records.</summary>
    public int Count()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM transcriptions;";
            return (int)(long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>
    /// Deletes stored audio files older than the cutoff and clears their <c>audio_filename</c>,
    /// keeping the text (spec §8). Returns how many records were affected.
    /// </summary>
    public int PurgeAudioOlderThan(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            var toClear = new List<(long id, string filename)>();
            using (var sel = _connection.CreateCommand())
            {
                sel.CommandText = """
                    SELECT id, audio_filename FROM transcriptions
                    WHERE audio_filename IS NOT NULL AND created_at < $cutoff;
                    """;
                sel.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeSeconds());
                using var reader = sel.ExecuteReader();
                while (reader.Read())
                    toClear.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            foreach (var (id, filename) in toClear)
            {
                TryDelete(Path.Combine(Paths.AudioDir, filename));
                using var upd = _connection.CreateCommand();
                upd.CommandText = "UPDATE transcriptions SET audio_filename = NULL WHERE id = $id;";
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
            }

            return toClear.Count;
        }
    }

    /// <summary>Deletes every record and all stored audio (for Delete all data, spec §11).</summary>
    public void DeleteAll()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM transcriptions;";
            cmd.ExecuteNonQuery();
        }

        try
        {
            if (Directory.Exists(Paths.AudioDir))
                foreach (var file in Directory.EnumerateFiles(Paths.AudioDir))
                    TryDelete(file);
        }
        catch { /* best effort */ }
    }

    private static Transcription ReadRow(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        CreatedAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
        Text: reader.GetString(2),
        Language: reader.IsDBNull(3) ? null : reader.GetString(3),
        Duration: reader.IsDBNull(4) ? null : reader.GetDouble(4),
        AudioFilename: reader.IsDBNull(5) ? null : reader.GetString(5),
        Model: reader.IsDBNull(6) ? null : reader.GetString(6),
        RawText: reader.IsDBNull(7) ? null : reader.GetString(7));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
