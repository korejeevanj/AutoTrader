namespace Trading.Data;

public sealed record ReplayRunOpening(string RunId, string ManifestJson, bool AllPresets);

public sealed partial class ReplayEvidenceJournal
{
    // The opening survives manifest failure and the interval between manifest commit
    // and the host checkpoint. Its frozen bytes move into the immutable manifest.
    public void StageRunOpening(string jobId, string runId, string inputHash, string manifestJson, bool allPresets)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        EnsureRun(connection, transaction, runId, inputHash);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO paper_run_openings VALUES($run,$job,$json,$all)";
        command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$json", manifestJson); command.Parameters.AddWithValue("$all", allPresets ? 1 : 0);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT manifest_json FROM paper_run_openings WHERE run_id=$run AND job_id=$job";
        if (command.ExecuteScalar() as string != manifestJson) throw new InvalidDataException("Replay opening cannot replace frozen manifest evidence.");
        command.CommandText = "UPDATE paper_run_openings SET all_presets=$all WHERE run_id=$run AND job_id=$job";
        command.ExecuteNonQuery(); transaction.Commit();
    }

    public IReadOnlyList<ReplayRunOpening> PendingRunOpenings(string jobId)
    {
        if (!File.Exists(path)) return [];
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,manifest_json,all_presets FROM paper_run_openings WHERE job_id=$job ORDER BY rowid DESC";
        command.Parameters.AddWithValue("$job", jobId); using var reader = command.ExecuteReader();
        var rows = new List<ReplayRunOpening>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0));
        return rows.AsReadOnly();
    }

    public void UpdateOpeningScope(string jobId, string runId, bool allPresets)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE paper_run_openings SET all_presets=$all WHERE job_id=$job AND run_id=$run";
        command.Parameters.AddWithValue("$job", jobId); command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$all", allPresets ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public void AcknowledgeRunOpening(string jobId, string runId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM paper_run_openings WHERE job_id=$job AND run_id=$run AND EXISTS(SELECT 1 FROM paper_run_manifests WHERE job_id=$job AND run_id=$run)";
        command.Parameters.AddWithValue("$job", jobId); command.Parameters.AddWithValue("$run", runId); command.ExecuteNonQuery();
    }
}
