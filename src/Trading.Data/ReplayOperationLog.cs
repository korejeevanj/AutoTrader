using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;

namespace Trading.Data;

public sealed record ReplayOperationEntry(string JobId, int Run, string? Preset, DateTimeOffset Wall, DateTimeOffset? Simulated,
    string Severity, string Code, string DetailJson);
public enum OperationLogStatus { Recorded, Queued, BacklogFull }

/// <summary>
/// Operational log for replay jobs, stored beside durable replay evidence in the same SQLite file.
/// Storage initializes lazily; operations recorded before it is ready are held (a bounded backlog
/// per job) and written in order once it is, so callers never see initialization order.
/// </summary>
public sealed class ReplayOperationLog
{
    public const int BacklogLimit = 128;
    private readonly ReplayEvidenceJournal journal;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ReplayOperationEntry>> backlog = new(StringComparer.Ordinal);
    private readonly Lazy<Task> initialization;

    public ReplayOperationLog(ReplayEvidenceJournal journal)
    {
        this.journal = journal;
        initialization = new(async () => { await Task.Run(journal.Initialize); lock (gate) Flush(); });
    }

    public bool Ready => journal.IsInitialized;

    /// <summary>Starts storage initialization without waiting for it.</summary>
    public void Begin() => _ = initialization.Value;
    public Task WhenReady(CancellationToken token) => initialization.Value.WaitAsync(token);

    /// <exception cref="IOException">Storage failed after it was ready.</exception>
    public OperationLogStatus Record(ReplayOperationEntry entry)
    {
        lock (gate)
        {
            if (!journal.IsInitialized)
            {
                var queue = backlog.GetOrAdd(entry.JobId, _ => new());
                if (queue.Count >= BacklogLimit) return OperationLogStatus.BacklogFull;
                queue.Enqueue(entry); return OperationLogStatus.Queued;
            }
            Flush();
        }
        Write(entry);
        return OperationLogStatus.Recorded;
    }

    private void Flush()
    {
        foreach (var queue in backlog.Values)
            while (queue.TryDequeue(out var entry)) Write(entry);
    }

    private void Write(ReplayOperationEntry entry)
    {
        using var connection = journal.Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO replay_operations(job_id,run_number,preset,wall_time_utc,simulated_time_utc,severity,code,detail_json) VALUES($job,$run,$preset,$wall,$sim,$severity,$code,$detail)";
        command.Parameters.AddWithValue("$job", entry.JobId); command.Parameters.AddWithValue("$run", entry.Run); command.Parameters.AddWithValue("$preset", (object?)entry.Preset ?? DBNull.Value);
        command.Parameters.AddWithValue("$wall", entry.Wall.ToString("O")); command.Parameters.AddWithValue("$sim", entry.Simulated is { } time ? time.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$severity", entry.Severity); command.Parameters.AddWithValue("$code", entry.Code); command.Parameters.AddWithValue("$detail", entry.DetailJson);
        command.ExecuteNonQuery();
    }

    /// <summary>The newest operations of a job, or none while storage is still initializing.</summary>
    public IReadOnlyList<ReplayOperation> Recent(string jobId, int limit = 100)
    {
        if (!journal.IsInitialized) return [];
        using var connection = journal.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence,run_number,preset,wall_time_utc,simulated_time_utc,severity,code,detail_json FROM replay_operations WHERE job_id=$job ORDER BY sequence DESC LIMIT $limit";
        command.Parameters.AddWithValue("$job", jobId); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader(); var rows = new List<ReplayOperation>();
        while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        return rows;
    }

    public async Task Export(Stream output, string jobId, CancellationToken cancellationToken, int? run = null)
    {
        await using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
        await using var writer = new StreamWriter(gzip, leaveOpen: true);
        using var connection = journal.Open(); using var command = connection.CreateCommand();
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
