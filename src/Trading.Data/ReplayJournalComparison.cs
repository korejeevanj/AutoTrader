using System.Text.Json;

namespace Trading.Data;

public sealed record ReplayRunProfitExit(string Version, string Mode, decimal NetTarget, decimal TrailActivation,
    decimal TrailGiveback, decimal MinimumLockedNetProfit);
public sealed record ReplayRunInfo(string RunId, string JobId, string Date, string Preset, string DatasetHash, string InputHash,
    string? SimulatedTimeUtc, decimal? RealizedNetPnl, int Trades, bool Complete, bool? Qualified = null, ReplayRunProfitExit? ProfitExit = null);
public sealed record ReplayDecisionDifference(string TimeUtc, string IndexId, string ContractId, string Category,
    string? LeftAction, string? RightAction, string? LeftReasons, string? RightReasons);

public sealed partial class ReplayEvidenceJournal
{
    public IReadOnlyList<ReplayRunInfo> RunInfos(string? jobId = null, string? date = null)
    {
        if (!File.Exists(path)) return [];
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.run_id,m.job_id,json_extract(manifest_json,'$.date'),json_extract(manifest_json,'$.preset.Id'),
                json_extract(manifest_json,'$.datasetHash'),json_extract(manifest_json,'$.inputHash'),r.time_utc,
                json_extract(r.snapshot_json,'$.Paper.RealizedNetPnl'),COALESCE(json_array_length(r.snapshot_json,'$.Trades'),0),COALESCE(json_extract(r.snapshot_json,'$.Complete'),0),
                json_extract(r.snapshot_json,'$.Qualified'),
                (SELECT json_extract(value,'$.Position.ProfitExit') FROM json_each(m.manifest_json,'$.engineConfiguration.Strategies')
                 WHERE json_extract(value,'$.IndexId')=json_extract(m.manifest_json,'$.dataset.Indices[0].Id') LIMIT 1)
            FROM paper_run_manifests m JOIN paper_runs r USING(run_id)
            WHERE ($job IS NULL OR m.job_id=$job) AND ($date IS NULL OR json_extract(manifest_json,'$.date')=$date)
            ORDER BY m.rowid DESC LIMIT 200
            """;
        command.Parameters.AddWithValue("$job", (object?)jobId ?? DBNull.Value); command.Parameters.AddWithValue("$date", (object?)date ?? DBNull.Value);
        using var reader = command.ExecuteReader(); var rows = new List<ReplayRunInfo>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetDecimal(7), reader.GetInt32(8), reader.GetInt32(9) != 0,
            reader.IsDBNull(10) ? null : reader.GetInt32(10) != 0, reader.IsDBNull(11) ? null : ProfitExitInfo(reader.GetString(11))));
        return rows;
    }
    private static ReplayRunProfitExit? ProfitExitInfo(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Mode", out var mode) || mode.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("NetTarget", out var target) || target.ValueKind != JsonValueKind.Number || !target.TryGetDecimal(out var netTarget)
            || !root.TryGetProperty("TrailActivation", out var activation) || activation.ValueKind != JsonValueKind.Number || !activation.TryGetDecimal(out var trailActivation)
            || !root.TryGetProperty("TrailGiveback", out var giveback) || giveback.ValueKind != JsonValueKind.Number || !giveback.TryGetDecimal(out var trailGiveback)
            || !root.TryGetProperty("MinimumLockedNetProfit", out var floor) || floor.ValueKind != JsonValueKind.Number || !floor.TryGetDecimal(out var lockedFloor)) return null;
        return new(version.GetString()!, mode.GetString()!, netTarget, trailActivation, trailGiveback, lockedFloor);
    }
    public IReadOnlyList<ReplayDecisionDifference> Compare(string left, string right, int limit = 101)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandTimeout = 120;
        command.CommandText = """
            WITH l AS (SELECT *,ROW_NUMBER() OVER(PARTITION BY time_utc,index_id,contract_id,category ORDER BY sequence) AS ordinal FROM paper_events WHERE run_id=$left),
                 r AS (SELECT *,ROW_NUMBER() OVER(PARTITION BY time_utc,index_id,contract_id,category ORDER BY sequence) AS ordinal FROM paper_events WHERE run_id=$right)
            SELECT COALESCE(l.time_utc,r.time_utc),COALESCE(l.index_id,r.index_id),COALESCE(l.contract_id,r.contract_id),COALESCE(l.category,r.category),l.action,r.action,l.reasons_json,r.reasons_json
            FROM l FULL OUTER JOIN r ON l.time_utc=r.time_utc AND l.index_id=r.index_id AND l.contract_id=r.contract_id AND l.category=r.category AND l.ordinal=r.ordinal
            WHERE l.action IS NULL OR r.action IS NULL OR l.action<>r.action OR l.reasons_json<>r.reasons_json
            ORDER BY 1,2,3,4 LIMIT $limit
            """;
        command.Parameters.AddWithValue("$left", left); command.Parameters.AddWithValue("$right", right); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 101));
        using var reader = command.ExecuteReader(); var rows = new List<ReplayDecisionDifference>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return rows;
    }
}
