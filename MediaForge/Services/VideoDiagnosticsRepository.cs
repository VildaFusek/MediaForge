using MediaForge.Models;
using MediaForge.Helpers;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace MediaForge.Services;

public sealed class VideoDiagnosticsRepository
{
    public sealed record PendingChange(string Id, string Source, string? Destination);
    public string DatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MediaForge", "Data", "media.db");

    // Microsoft.Data.Sqlite's I/O is synchronous; callers run repository work off the UI thread.
    public void Save(VideoDiagnosticRecord item, CancellationToken token)
    {
        using var connection = Open();
        token.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO video_diagnostics
                (path_key, file_path, file_size, modified_ticks, analyzed_utc, probe_version,
                 container, duration_seconds, video_codec, width, height, frame_rate, bit_rate,
                 pixel_format, bit_depth, audio_tracks, subtitle_tracks, metadata_json, probe_json)
            VALUES
                ($key, $path, $size, $modified, $analyzed, $probe, $container, $duration,
                 $codec, $width, $height, $fps, $rate, $pixel, $depth, $audio, $subtitles, $metadata, $raw)
            ON CONFLICT(path_key) DO UPDATE SET
                file_path=excluded.file_path, file_size=excluded.file_size,
                modified_ticks=excluded.modified_ticks, analyzed_utc=excluded.analyzed_utc,
                probe_version=excluded.probe_version, container=excluded.container,
                duration_seconds=excluded.duration_seconds, video_codec=excluded.video_codec,
                width=excluded.width, height=excluded.height, frame_rate=excluded.frame_rate,
                bit_rate=excluded.bit_rate, pixel_format=excluded.pixel_format, bit_depth=excluded.bit_depth,
                audio_tracks=excluded.audio_tracks, subtitle_tracks=excluded.subtitle_tracks,
                metadata_json=excluded.metadata_json, probe_json=excluded.probe_json;
            """;
        void Add(string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        Add("$key", Path.GetFullPath(item.FilePath).ToUpperInvariant());
        Add("$path", item.FilePath);
        Add("$size", item.FileSize);
        Add("$modified", item.ModifiedUtcTicks);
        Add("$analyzed", item.AnalyzedUtc.ToString("O"));
        Add("$probe", item.ProbeVersion);
        Add("$container", item.Container);
        Add("$duration", item.DurationSeconds);
        Add("$codec", item.VideoCodec);
        Add("$width", item.Width);
        Add("$height", item.Height);
        Add("$fps", item.FrameRate);
        Add("$rate", item.BitRate);
        Add("$pixel", item.PixelFormat);
        Add("$depth", item.BitDepth);
        Add("$audio", item.AudioTracks);
        Add("$subtitles", item.SubtitleTracks);
        Add("$metadata", JsonSerializer.Serialize(item));
        Add("$raw", item.RawJson);
        command.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public VideoDiagnosticRecord? TryReadCurrent(string path, CancellationToken token)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT metadata_json FROM video_diagnostics WHERE path_key=$key;";
        command.Parameters.AddWithValue("$key", Key(path));
        token.ThrowIfCancellationRequested();
        if (command.ExecuteScalar() is not string json) return null;
        var item = JsonSerializer.Deserialize<VideoDiagnosticRecord>(json)
            ?? throw new InvalidDataException("Neplatná metadata videa.");
        if (Matches(item, path)) return item;
        DeleteTree(connection, null, path);
        return null;
    }

    public List<VideoDiagnosticRecord> LoadDirectory(string folder, CancellationToken token)
    {
        RecoverPendingChanges();
        using var mutationLock = AcquireMutationLock();
        using var connection = Open();
        var items = ReadTree(connection, null, folder, directOnly: true);
        var valid = new List<VideoDiagnosticRecord>();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (Matches(item, item.FilePath)) valid.Add(item);
                else DeleteTree(connection, null, item.FilePath);
            }
            catch (FileNotFoundException) { DeleteIfMissing(connection, item.FilePath); }
            catch (DirectoryNotFoundException) { DeleteIfMissing(connection, item.FilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                AppLog.Error("Ověření uložených metadat " + item.FilePath, ex);
            }
        }
        return valid;
    }

    public PendingChange PrepareChange(string source, string? destination)
    {
        var change = new PendingChange(Guid.NewGuid().ToString("N"),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)),
            destination is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO pending_file_changes(id,source,destination) VALUES($id,$source,$destination);";
        command.Parameters.AddWithValue("$id", change.Id);
        command.Parameters.AddWithValue("$source", change.Source);
        command.Parameters.AddWithValue("$destination", (object?)change.Destination ?? DBNull.Value);
        command.ExecuteNonQuery();
        return change;
    }

    public FileStream AcquireMutationLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        return new FileStream(DatabasePath + ".operations.lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public void FinishChange(PendingChange change, bool succeeded)
    {
        using var connection = Open();
        bool sourceMissing = IsConfirmedMissing(change.Source);
        bool sameKey = change.Destination is not null && Key(change.Source) == Key(change.Destination);
        if (!succeeded && !sourceMissing && !sameKey)
        {
            // A failed operation must not retarget metadata to an unrelated existing destination.
            foreach (var item in ReadTree(connection, null, change.Source))
                DeleteIfMissing(connection, item.FilePath);
            RemovePending(connection, null, change.Id);
            return;
        }
        using var transaction = connection.BeginTransaction();
        if (change.Destination is null)
        {
            if (succeeded || sourceMissing) DeleteTree(connection, transaction, change.Source);
        }
        else if (succeeded || sourceMissing || sameKey)
        {
            foreach (var item in ReadTree(connection, transaction, change.Source))
            {
                string target = change.Destination + item.FilePath[change.Source.Length..];
                if (IsConfirmedMissing(target))
                {
                    DeleteTree(connection, transaction, item.FilePath);
                    continue;
                }
                if (!Matches(item, target))
                {
                    DeleteTree(connection, transaction, item.FilePath);
                    continue;
                }
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    DELETE FROM video_diagnostics WHERE path_key=$target AND path_key<>$source;
                    UPDATE video_diagnostics SET path_key=$target, file_path=$path, metadata_json=$json
                    WHERE path_key=$source;
                    """;
                update.Parameters.AddWithValue("$source", Key(item.FilePath));
                update.Parameters.AddWithValue("$target", Key(target));
                update.Parameters.AddWithValue("$path", target);
                update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item with { FilePath = target }));
                update.ExecuteNonQuery();
            }
        }
        RemovePending(connection, transaction, change.Id);
        transaction.Commit();
    }

    public void RecoverPendingChanges()
    {
        FileStream mutationLock;
        try { mutationLock = AcquireMutationLock(); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return; }
        using var lease = mutationLock;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,source,destination FROM pending_file_changes;";
        var changes = new List<PendingChange>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) changes.Add(new(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        foreach (var change in changes)
        {
            try { FinishChange(change, false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            { AppLog.Error("Obnova propojení metadat " + change.Source, ex); }
        }
    }

    public void ReconcileExternalRename(string source, string destination)
    {
        if (Key(source) != Key(destination) && !IsConfirmedMissing(source)) return;
        FileSystemService.EnsureNoReparsePoints(destination);
        var change = PrepareChange(source, destination);
        FinishChange(change, true);
    }

    public void RemoveMissingTree(string path)
    {
        using var connection = Open();
        DeleteIfMissing(connection, path);
    }

    private static bool Matches(VideoDiagnosticRecord item, string path)
    {
        FileSystemService.EnsureNoReparsePoints(path);
        var snapshot = VideoFileIdentity.Read(path);
        return !string.IsNullOrEmpty(item.FileIdentity) && item.FileIdentity == snapshot.Identity
            && item.FileSize == snapshot.Size && item.ModifiedUtcTicks == snapshot.ModifiedUtcTicks;
    }

    private static bool IsConfirmedMissing(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Missing is not the same as access denied or a disconnected drive.
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
            while (parent is not null)
            {
                try
                {
                    FileSystemService.EnsureNoReparsePoints(parent);
                    return (File.GetAttributes(parent) & FileAttributes.Directory) != 0;
                }
                catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
                { parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(parent)); }
            }
            throw new IOException("Nelze potvrdit dostupnost jednotky; metadata zůstala zachována.", ex);
        }
    }

    private static void DeleteIfMissing(SqliteConnection connection, string path)
    {
        if (IsConfirmedMissing(path)) DeleteTree(connection, null, path);
    }

    private static List<VideoDiagnosticRecord> ReadTree(
        SqliteConnection connection, SqliteTransaction? transaction, string path, bool directOnly = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT metadata_json FROM video_diagnostics
            WHERE (path_key=$key OR (path_key >= $prefix AND path_key < $end))
            AND ($direct=0 OR instr(substr(path_key,length($prefix)+1),$separator)=0);
            """;
        command.Parameters.AddWithValue("$key", Key(path));
        command.Parameters.AddWithValue("$prefix", Prefix(path));
        command.Parameters.AddWithValue("$end", Prefix(path)[..^1] + (char)(Path.DirectorySeparatorChar + 1));
        command.Parameters.AddWithValue("$direct", directOnly ? 1 : 0);
        command.Parameters.AddWithValue("$separator", Path.DirectorySeparatorChar.ToString());
        using var reader = command.ExecuteReader();
        var items = new List<VideoDiagnosticRecord>();
        while (reader.Read())
            items.Add(JsonSerializer.Deserialize<VideoDiagnosticRecord>(reader.GetString(0))
                ?? throw new InvalidDataException("Neplatná metadata videa."));
        return items;
    }

    private static void DeleteTree(SqliteConnection connection, SqliteTransaction? transaction, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM video_diagnostics WHERE path_key=$key OR (path_key >= $prefix AND path_key < $end);
            """;
        command.Parameters.AddWithValue("$key", Key(path));
        command.Parameters.AddWithValue("$prefix", Prefix(path));
        command.Parameters.AddWithValue("$end", Prefix(path)[..^1] + (char)(Path.DirectorySeparatorChar + 1));
        command.ExecuteNonQuery();
    }

    private static void RemovePending(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM pending_file_changes WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static string Key(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
    private static string Prefix(string path) => Key(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            long schemaVersion = (long)version.ExecuteScalar()!;
            if (schemaVersion > 2) throw new InvalidDataException("Databáze pochází z novější verze MediaForge.");
            if (schemaVersion == 2) return connection;
            using var transaction = connection.BeginTransaction();
            using var schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS video_diagnostics (
                    path_key TEXT PRIMARY KEY, file_path TEXT NOT NULL, file_size INTEGER NOT NULL,
                    modified_ticks INTEGER NOT NULL, analyzed_utc TEXT NOT NULL, probe_version TEXT NOT NULL,
                    container TEXT NOT NULL, duration_seconds REAL, video_codec TEXT NOT NULL,
                    width INTEGER, height INTEGER, frame_rate REAL, bit_rate INTEGER,
                    pixel_format TEXT NOT NULL, bit_depth INTEGER, audio_tracks INTEGER NOT NULL,
                    subtitle_tracks INTEGER NOT NULL, metadata_json TEXT NOT NULL, probe_json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_video_codec_resolution ON video_diagnostics(video_codec, height, width);
                CREATE INDEX IF NOT EXISTS ix_video_duration ON video_diagnostics(duration_seconds);
                CREATE INDEX IF NOT EXISTS ix_video_analyzed ON video_diagnostics(analyzed_utc);
                CREATE TABLE IF NOT EXISTS pending_file_changes (
                    id TEXT PRIMARY KEY, source TEXT NOT NULL, destination TEXT);
                PRAGMA user_version=2;
                """;
            schema.ExecuteNonQuery();
            transaction.Commit();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}
