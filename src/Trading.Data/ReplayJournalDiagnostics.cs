using System.IO.Compression;
using System.Text.Json;

namespace Trading.Data;

public sealed record ReplayOperation(long Sequence, int Run, string? Preset, string WallTimeUtc, string? SimulatedTimeUtc, string Severity, string Code, string DetailJson);
public sealed record ReplayStoredRun(string RunId, string ManifestJson, string SnapshotJson);

public sealed partial class ReplayPaperJournal
{
    public int NextRunNumber(string jobId, int currentRun)
    {
        if (!Guid.TryParseExact(jobId, "N", out _) || currentRun < 0) throw new ArgumentException("Supply a valid replay job and run number.");
        if (!File.Exists(path)) return checked(currentRun + 1);
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(CAST(substr(run_id,length($prefix)+1) AS INTEGER)),0) FROM paper_runs WHERE substr(run_id,1,length($prefix))=$prefix";
        command.Parameters.AddWithValue("$prefix", jobId + ":run-");
        return checked((int)Math.Max(currentRun, Convert.ToInt64(command.ExecuteScalar())) + 1);
    }

    public void RecordManifest(string runId, string jobId, string json)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO paper_run_manifests VALUES($run,$job,$json)";
        command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$job", jobId); command.Parameters.AddWithValue("$json", json); command.ExecuteNonQuery();
    }
    public IReadOnlyList<ReplayStoredRun> Runs(string jobId)
    {
        if (!File.Exists(path)) return [];
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT m.run_id,m.manifest_json,r.snapshot_json FROM paper_run_manifests m JOIN paper_runs r USING(run_id) WHERE m.job_id=$job ORDER BY m.rowid DESC";
        command.Parameters.AddWithValue("$job", jobId); using var reader = command.ExecuteReader(); var rows = new List<ReplayStoredRun>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }
    public ReplayStoredRun? Run(string jobId, string runId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT m.manifest_json,r.snapshot_json FROM paper_run_manifests m JOIN paper_runs r USING(run_id) WHERE m.job_id=$job AND m.run_id=$run";
        command.Parameters.AddWithValue("$job", jobId); command.Parameters.AddWithValue("$run", runId); using var reader = command.ExecuteReader();
        return reader.Read() ? new(runId, reader.GetString(0), reader.GetString(1)) : null;
    }
    public void Operation(string jobId, int run, string? preset, DateTimeOffset wall, DateTimeOffset? simulated, string severity, string code, string detailJson)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO replay_operations(job_id,run_number,preset,wall_time_utc,simulated_time_utc,severity,code,detail_json) VALUES($job,$run,$preset,$wall,$sim,$severity,$code,$detail)";
        command.Parameters.AddWithValue("$job", jobId); command.Parameters.AddWithValue("$run", run); command.Parameters.AddWithValue("$preset", (object?)preset ?? DBNull.Value);
        command.Parameters.AddWithValue("$wall", wall.ToString("O")); command.Parameters.AddWithValue("$sim", simulated is { } time ? time.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$severity", severity); command.Parameters.AddWithValue("$code", code); command.Parameters.AddWithValue("$detail", detailJson); command.ExecuteNonQuery();
    }
    public IReadOnlyList<ReplayOperation> Operations(string jobId, int limit = 100)
    {
        if (!File.Exists(path)) return [];
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence,run_number,preset,wall_time_utc,simulated_time_utc,severity,code,detail_json FROM replay_operations WHERE job_id=$job ORDER BY sequence DESC LIMIT $limit";
        command.Parameters.AddWithValue("$job", jobId); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader(); var rows = new List<ReplayOperation>();
        while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        return rows;
    }
    public bool HasRun(string jobId, string runId)
    {
        if (!File.Exists(path)) return false;
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM paper_run_manifests WHERE job_id=$job AND run_id=$run";
        command.Parameters.AddWithValue("$job", jobId); command.Parameters.AddWithValue("$run", runId); return command.ExecuteScalar() is not null;
    }
    public IEnumerable<string> BatchPayloads(string runId, int? maximumStep = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_gzip FROM paper_batches WHERE run_id=$run AND batch_id LIKE 'step-%' AND ($max IS NULL OR CAST(substr(batch_id,6) AS INTEGER)<=$max) ORDER BY CAST(substr(batch_id,6) AS INTEGER)";
        command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$max", (object?)maximumStep ?? DBNull.Value); using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            using var bytes = new MemoryStream((byte[])reader[0]); using var gzip = new GZipStream(bytes, CompressionMode.Decompress);
            using var text = new StreamReader(gzip); yield return text.ReadToEnd();
        }
    }
    public string? DecisionEvidence(string runId, long sequence)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT b.payload_gzip,$seq-r.first_sequence FROM paper_batch_ranges r JOIN paper_batches b USING(run_id,batch_id) WHERE r.run_id=$run AND $seq BETWEEN r.first_sequence AND r.last_sequence";
        command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$seq", sequence); using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        using var bytes = new MemoryStream((byte[])reader[0]); using var gzip = new GZipStream(bytes, CompressionMode.Decompress);
        using var json = JsonDocument.Parse(gzip);
        return json.RootElement.TryGetProperty("Decisions", out var decisions) ? decisions[(int)reader.GetInt64(1)].GetRawText() : null;
    }
    public async Task Export(Stream output, string jobId, string runId, CancellationToken cancellationToken)
    {
        await using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
        await using var writer = new StreamWriter(gzip, leaveOpen: true);
        var run = Run(jobId, runId) ?? throw new InvalidDataException("Run manifest unavailable.");
        PaperJournalCheckpoint checkpoint; int maximumStep; long maximumOperation;
        using (var connection = Open())
        using (var transaction = connection.BeginTransaction(deferred: true))
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT input_hash,time_utc,snapshot_json,controls_json,
                    (SELECT COALESCE(MAX(CAST(substr(batch_id,6) AS INTEGER)),0) FROM paper_batches WHERE run_id=$run AND batch_id LIKE 'step-%'),
                    (SELECT COALESCE(MAX(sequence),0) FROM replay_operations WHERE job_id=$job)
                FROM paper_runs WHERE run_id=$run
                """;
            command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$job", jobId);
            using (var reader = command.ExecuteReader())
            {
                reader.Read(); checkpoint = new(runId, reader.GetString(0), reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture), reader.GetString(2), reader.GetString(3));
                maximumStep = reader.GetInt32(4); maximumOperation = reader.GetInt64(5);
            }
            transaction.Commit();
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "manifest", run.RunId, manifest = JsonSerializer.Deserialize<JsonElement>(run.ManifestJson), checkpoint, maximumStep, maximumOperation }));
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT wall_time_utc,simulated_time_utc,severity,code,detail_json FROM replay_operations WHERE job_id=$job AND sequence<=$max AND (run_number=0 OR run_number=$run) ORDER BY sequence";
            using var manifest = JsonDocument.Parse(run.ManifestJson);
            command.Parameters.AddWithValue("$job", jobId); command.Parameters.AddWithValue("$run", manifest.RootElement.GetProperty("run").GetInt32());
            command.Parameters.AddWithValue("$max", maximumOperation);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "operation", wallTimeUtc = reader.GetString(0), simulatedTimeUtc = reader.IsDBNull(1) ? null : reader.GetString(1), severity = reader.GetString(2), code = reader.GetString(3), detail = JsonSerializer.Deserialize<JsonElement>(reader.GetString(4)) }));
            }
        }
        foreach (var payload in BatchPayloads(runId, maximumStep))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "evaluationBatch", evidence = JsonSerializer.Deserialize<JsonElement>(payload) }));
        }
    }
    public async Task ExportOperations(Stream output, string jobId, CancellationToken cancellationToken, int? run = null)
    {
        await using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
        await using var writer = new StreamWriter(gzip, leaveOpen: true);
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_number,preset,wall_time_utc,simulated_time_utc,severity,code,detail_json FROM replay_operations WHERE job_id=$job AND ($run IS NULL OR run_number=0 OR run_number=$run) ORDER BY sequence";
        command.Parameters.AddWithValue("$job", jobId); command.Parameters.AddWithValue("$run", (object?)run ?? DBNull.Value); using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { run = reader.GetInt32(0), preset = reader.IsDBNull(1) ? null : reader.GetString(1),
                wallTimeUtc = reader.GetString(2), simulatedTimeUtc = reader.IsDBNull(3) ? null : reader.GetString(3), severity = reader.GetString(4), code = reader.GetString(5), detail = JsonSerializer.Deserialize<JsonElement>(reader.GetString(6)) }));
        }
    }
}
