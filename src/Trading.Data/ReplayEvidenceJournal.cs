using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Trading.Data;

public sealed record PaperJournalEvent(string EventId, DateTimeOffset TimeUtc, string IndexId, string ContractId,
    string Category, string Action, string? SetupId, string ReasonsJson);
public sealed record PaperJournalRow(long Sequence, string RunId, string EventId, DateTimeOffset TimeUtc,
    string IndexId, string ContractId, string Category, string Action, string? SetupId, string ReasonsJson);
public sealed record PaperJournalCheckpoint(string RunId, string InputHash, DateTimeOffset? TimeUtc,
    string SnapshotJson, string ControlsJson);

// Durable replay evidence, separate from the immutable synthetic plan journal. One
// transaction commits the complete evaluation batch, lifecycle events and recoverable
// paper checkpoint. The operational log (ReplayOperationLog) shares this file and schema.
public sealed partial class ReplayEvidenceJournal(string path)
{
    private readonly object schemaGate = new();
    private bool initialized;
    public bool IsInitialized => Volatile.Read(ref initialized);
    /// <summary>True when a journal file already exists, so storage initialization can start at once.</summary>
    public bool FileExists => File.Exists(path);
    public void Initialize() { using var connection = Open(); }
    internal SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString());
        connection.Open();
        lock (schemaGate)
        {
            if (!initialized)
            {
                using var schema = connection.CreateCommand();
                schema.CommandText = """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE IF NOT EXISTS paper_runs(run_id TEXT PRIMARY KEY,input_hash TEXT NOT NULL,time_utc TEXT,snapshot_json TEXT NOT NULL DEFAULT '{}',controls_json TEXT NOT NULL DEFAULT '[]');
                    CREATE TABLE IF NOT EXISTS paper_batches(run_id TEXT NOT NULL,batch_id TEXT NOT NULL,content_hash TEXT NOT NULL,payload_gzip BLOB NOT NULL,PRIMARY KEY(run_id,batch_id));
                    CREATE TABLE IF NOT EXISTS paper_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,run_id TEXT NOT NULL,event_id TEXT NOT NULL,time_utc TEXT NOT NULL,index_id TEXT NOT NULL,contract_id TEXT NOT NULL,category TEXT NOT NULL,action TEXT NOT NULL,setup_id TEXT,reasons_json TEXT NOT NULL,UNIQUE(run_id,event_id));
                    CREATE INDEX IF NOT EXISTS ix_paper_events_run ON paper_events(run_id,sequence);
                    CREATE INDEX IF NOT EXISTS ix_paper_events_category ON paper_events(run_id,category,sequence);
                    CREATE INDEX IF NOT EXISTS ix_paper_events_lifecycle ON paper_events(run_id,sequence) WHERE action IN ('HYPOTHETICAL_INTENT_ACCEPTED','HYPOTHETICAL_INTENT_CANCELED','HYPOTHETICAL_FILLED','HYPOTHETICAL_EXIT_FILLED','PAUSE_NEW_ENTRIES','ENABLE_NEW_ENTRIES');
                    CREATE TABLE IF NOT EXISTS paper_batch_ranges(run_id TEXT NOT NULL,batch_id TEXT NOT NULL,first_sequence INTEGER NOT NULL,last_sequence INTEGER NOT NULL,PRIMARY KEY(run_id,batch_id));
                    CREATE TABLE IF NOT EXISTS paper_run_manifests(run_id TEXT PRIMARY KEY,job_id TEXT NOT NULL,manifest_json TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS paper_run_openings(run_id TEXT PRIMARY KEY,job_id TEXT NOT NULL,manifest_json TEXT NOT NULL,all_presets INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS ix_paper_manifests_job ON paper_run_manifests(job_id);
                    CREATE TABLE IF NOT EXISTS replay_operations(sequence INTEGER PRIMARY KEY AUTOINCREMENT,job_id TEXT NOT NULL,run_number INTEGER NOT NULL,preset TEXT,wall_time_utc TEXT NOT NULL,simulated_time_utc TEXT,severity TEXT NOT NULL,code TEXT NOT NULL,detail_json TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS ix_replay_operations_job ON replay_operations(job_id,sequence);
                    """;
                schema.ExecuteNonQuery(); initialized = true;
            }
        }
        return connection;
    }
    public void Begin(string runId, string inputHash)
    {
        using var connection = Open(); EnsureRun(connection, null, runId, inputHash);
    }
    private static void EnsureRun(SqliteConnection connection, SqliteTransaction? transaction, string runId, string inputHash)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO paper_runs(run_id,input_hash) VALUES($run,$hash); SELECT input_hash FROM paper_runs WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$hash", inputHash);
        if ((string)command.ExecuteScalar()! != inputHash) throw new InvalidDataException("Paper run cannot mix frozen input/code versions.");
    }
    public void Append(string runId, string batchId, DateTimeOffset? time, string payloadJson, string snapshotJson,
        string controlsJson, IReadOnlyList<PaperJournalEvent> events)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var hash = "v2:" + KiteCandleImporter.Hash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { time, payloadJson, snapshotJson, controlsJson, events }, KiteCandleImporter.JsonOptions));
        using var batch = connection.CreateCommand(); batch.Transaction = transaction;
        batch.CommandText = "SELECT content_hash FROM paper_batches WHERE run_id=$run AND batch_id=$batch";
        batch.Parameters.AddWithValue("$run", runId); batch.Parameters.AddWithValue("$batch", batchId);
        var previous = batch.ExecuteScalar() as string;
        if (previous is not null)
        {
            if (previous != hash) throw new InvalidDataException("Duplicate paper batch has conflicting evidence.");
            transaction.Commit(); return;
        }
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(payloadJson));
        batch.CommandText = "INSERT INTO paper_batches VALUES($run,$batch,$hash,$payload)";
        batch.Parameters.AddWithValue("$hash", hash); batch.Parameters.AddWithValue("$payload", bytes.ToArray()); batch.ExecuteNonQuery();
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        using var bounds = connection.CreateCommand(); bounds.Transaction = transaction;
        bounds.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM paper_events";
        var before = (long)bounds.ExecuteScalar()!;
        insert.CommandText = "INSERT OR IGNORE INTO paper_events(run_id,event_id,time_utc,index_id,contract_id,category,action,setup_id,reasons_json) VALUES($run,$event,$time,$index,$contract,$category,$action,$setup,$reasons)";
        foreach (var name in new[] { "$run", "$event", "$time", "$index", "$contract", "$category", "$action", "$setup", "$reasons" }) insert.Parameters.Add(name, SqliteType.Text);
        foreach (var item in events)
        {
            insert.Parameters["$run"].Value = runId; insert.Parameters["$event"].Value = item.EventId; insert.Parameters["$time"].Value = item.TimeUtc.ToString("O");
            insert.Parameters["$index"].Value = item.IndexId; insert.Parameters["$contract"].Value = item.ContractId; insert.Parameters["$category"].Value = item.Category;
            insert.Parameters["$action"].Value = item.Action; insert.Parameters["$setup"].Value = (object?)item.SetupId ?? DBNull.Value; insert.Parameters["$reasons"].Value = item.ReasonsJson;
            if (insert.ExecuteNonQuery() != 1) throw new InvalidDataException("Paper event identity conflicts with an already committed event.");
        }
        if (events.Count > 0)
        {
            bounds.CommandText = "INSERT INTO paper_batch_ranges SELECT $run,$batch,MIN(sequence),MAX(sequence) FROM paper_events WHERE sequence>$before AND run_id=$run HAVING COUNT(*)>0";
            bounds.Parameters.AddWithValue("$run", runId); bounds.Parameters.AddWithValue("$batch", batchId); bounds.Parameters.AddWithValue("$before", before); bounds.ExecuteNonQuery();
        }
        using var checkpoint = connection.CreateCommand(); checkpoint.Transaction = transaction;
        checkpoint.CommandText = "UPDATE paper_runs SET time_utc=$time,snapshot_json=$snapshot,controls_json=$controls WHERE run_id=$run";
        checkpoint.Parameters.AddWithValue("$run", runId); checkpoint.Parameters.AddWithValue("$time", time is null ? DBNull.Value : time.Value.ToString("O"));
        checkpoint.Parameters.AddWithValue("$snapshot", snapshotJson); checkpoint.Parameters.AddWithValue("$controls", controlsJson); checkpoint.ExecuteNonQuery();
        transaction.Commit();
    }
    public PaperJournalCheckpoint? Checkpoint(string runId)
    {
        if (!File.Exists(path)) return null;
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT input_hash,time_utc,snapshot_json,controls_json FROM paper_runs WHERE run_id=$run"; command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(runId, reader.GetString(0), reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture), reader.GetString(2), reader.GetString(3)) : null;
    }
    public IReadOnlyList<PaperJournalRow> Read(string runId, int limit = 200, string? category = null, long? beforeSequence = null, string? search = null)
    {
        if (!File.Exists(path)) return [];
        if (string.IsNullOrWhiteSpace(category)) category = null;
        using var connection = Open(); using var command = connection.CreateCommand();
        var filter = category == "LIFECYCLE" ? " AND action IN ('HYPOTHETICAL_INTENT_ACCEPTED','HYPOTHETICAL_INTENT_CANCELED','HYPOTHETICAL_FILLED','HYPOTHETICAL_EXIT_FILLED','PAUSE_NEW_ENTRIES','ENABLE_NEW_ENTRIES')"
            : category is not null ? " AND category=$category" : "";
        command.CommandText = "SELECT sequence,event_id,time_utc,index_id,contract_id,category,action,setup_id,reasons_json FROM paper_events WHERE run_id=$run" + filter
            + " AND ($before IS NULL OR sequence<$before) AND ($search IS NULL OR instr(lower(contract_id||' '||action||' '||reasons_json),lower($search))>0) ORDER BY sequence DESC LIMIT $limit";
        command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$category", (object?)category ?? DBNull.Value); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1001));
        command.Parameters.AddWithValue("$before", (object?)beforeSequence ?? DBNull.Value); command.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search) ? DBNull.Value : search);
        using var reader = command.ExecuteReader(); var events = new List<PaperJournalRow>();
        while (reader.Read()) events.Add(new(reader.GetInt64(0), runId, reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8)));
        events.Reverse(); return events.AsReadOnly();
    }
}
