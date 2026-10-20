using System.Text.Json;
using Trading.Data;
using Trading.Domain;
using Trading.Replay;

namespace Trading.Api;

public sealed record ReplayHealthRow(string Id, string Role, string State, int Rows, int? ExpectedMinutes, int? MissingMinutes,
    bool Cached, string? Error, string Impact, IReadOnlyList<HistoricalQualityIssue> Issues, IReadOnlyList<ReplayMissingInterval> MissingIntervals,
    string RecoveryAction = "None");
public sealed record ReplayMissingInterval(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
public sealed record ReplayDataHealth(int Total, int Processed, int Complete, int Sparse, int Empty, int Failed, int Pending,
    int Cached, int RequiredAffected, string Readiness, IReadOnlyList<ReplayHealthRow> Instruments, IReadOnlyList<string> Warnings, int Invalid = 0,
    HistoricalReadinessSummary? Qualification = null, string? JobId = null, int? Attempt = null, string? ServerInstanceId = null);
public sealed record ReplayEventPage(string RunId, IReadOnlyList<PaperJournalRow> Rows, long? NextBeforeSequence);
public sealed record ReplayRunDiagnostics(IReadOnlyList<ReplayOperation> Operations, IReadOnlyList<ReplayRunInfo> Runs, bool Initializing);
public sealed record ReplayRunComparison(ReplayRunInfo Left, ReplayRunInfo Right, IReadOnlyList<ReplayDecisionDifference> Differences, bool HasMore, bool ConfigurationChanged);

public sealed partial class HistoricalReplayService
{
    private ReplayDataHealth BuildHealth(ReplayJobSnapshot snapshot)
    {
        var evidence = snapshot.AttemptEvidence;
        var rows = evidence.Instruments.Select(row =>
        {
            var impact = row.Role switch { "Futures" => "Missing/invalid intervals block dependent opening-range and setup decisions.",
                "Constituent" => "Missing/invalid intervals remove bank-confirmation evidence; weight is not renormalized.",
                "CashIndex" => "Affects index-price context; no traded-volume VWAP is inferred.",
                _ => "Missing/invalid intervals leave this contract unknown and make selection evidence incomplete. Other contracts remain available for diagnostic research." };
            var recovery = row.State switch { "Failed" => row.Error == "HISTORICAL_KITE_MAPPING_UNVERIFIED" ? "Repair dated instrument mapping; create a new preparation revision." : "Retry failed requests.",
                "Unavailable" => "Refresh coverage gaps explicitly. An empty response is unknown, not proof of inactivity; the source may remain unavailable.",
                "Sparse" or "Partial" or "Invalid" => "Refresh coverage gaps into a new attempt; earlier inputs remain preserved.",
                "Waiting" => "Wait for acquisition, or resume interrupted preparation.",
                "AwaitingValidation" => "Response processed; wait for matching attempt normalization, or retry preparation if it was interrupted.",
                _ => "No candle recovery needed; reference and decision-time qualification still apply." };
            return new ReplayHealthRow(row.Id, row.Role, row.State, row.Rows, row.ExpectedMinutes, row.MissingMinutes,
                row.Cached, row.Error, impact, row.Issues, row.MissingIntervals.Select(gap => new ReplayMissingInterval(gap.StartUtc, gap.EndUtc)).ToArray(), recovery);
        }).ToArray();
        var totals = evidence.Totals;
        return new(totals.Total, totals.Processed, totals.Complete, totals.Sparse, totals.Empty, totals.Failed, totals.Pending, totals.Cached, totals.RequiredAffected,
            !snapshot.Job.CanReplay ? "Not ready" : snapshot.PriceOnly ? "Price preview only"
                : snapshot.Qualification?.Qualified != true ? "Incomplete — diagnostic replay only" : "Qualified historical inputs — candle model only",
            rows, snapshot.Job.Warnings, totals.Invalid, snapshot.Qualification, snapshot.Job.Id, evidence.Number, serverInstanceId);
    }

    // Health is rebuilt exactly when an input BuildHealth reads has changed.
    public ReplayDataHealth? Health(string id)
    {
        if (jobs.Snapshot(id) is not { } snapshot) return null;
        var inputs = (snapshot.AttemptEvidence, snapshot.Job.CanReplay, snapshot.PriceOnly, snapshot.Qualification, snapshot.Job.Warnings);
        if (health.TryGetValue(id, out var cached) && cached.Inputs == inputs) return cached.Health;
        var built = BuildHealth(snapshot);
        health[id] = (inputs, built);
        return built;
    }

    public async Task<IReadOnlyList<PaperJournalRow>?> PaperLog(string id, int limit, string? category)
    {
        if (jobs.Snapshot(id) is not { } snapshot) return null;
        var runId = snapshot.PaperRunId;
        return await Task.Run<IReadOnlyList<PaperJournalRow>>(() => runId is null ? [] : paperJournal.Read(runId, limit, category));
    }
    public ReplayRunDiagnostics? Diagnostics(string id) => jobs.Exists(id) ? new(operationLog.Recent(id), paperJournal.IsInitialized ? paperJournal.RunInfos(id) : [], !paperJournal.IsInitialized) : null;
    public IReadOnlyList<ReplayRunInfo> Runs(DateOnly? date) => paperJournal.IsInitialized ? paperJournal.RunInfos(date: date?.ToString("yyyy-MM-dd")) : [];
    public ReplayRunComparison Compare(string id, string left, string right)
    {
        if (!jobs.Exists(id)) throw new ArgumentException("Job not found.");
        var first = paperJournal.RunInfos(id).SingleOrDefault(run => run.RunId == left) ?? throw new ArgumentException("Select a run belonging to this job.");
        var second = paperJournal.RunInfos(date: first.Date).SingleOrDefault(run => run.RunId == right) ?? throw new ArgumentException("Comparison run not found.");
        if (first.DatasetHash != second.DatasetHash) throw new ArgumentException("Runs use different frozen datasets and cannot be compared as a strategy experiment.");
        if (!first.Complete || !second.Complete) throw new ArgumentException("Complete both runs before comparing their decisions.");
        var differences = paperJournal.Compare(left, right);
        return new(first, second, differences.Take(100).ToArray(), differences.Count > 100, first.InputHash != second.InputHash || first.Preset != second.Preset);
    }
    private bool OwnsRun(string id, string runId) => paperJournal.IsInitialized && jobs.Exists(id) && paperJournal.HasRun(id, runId);
    public ReplayEventPage? Events(string id, string runId, int limit, long? before, string? category, string? search)
    {
        if (!OwnsRun(id, runId)) return null;
        limit = Math.Clamp(limit, 1, 100); var rows = paperJournal.Read(runId, limit + 1, category, before, search).Reverse().ToArray();
        var page = rows.Take(limit).ToArray();
        return new(runId, page, rows.Length > limit ? page[^1].Sequence : null);
    }
    public string? Evidence(string id, string runId, long sequence) => OwnsRun(id, runId) ? paperJournal.DecisionEvidence(runId, sequence) : null;
    public bool HasEvidence(string id, string runId) => OwnsRun(id, runId);
    public Task ExportEvidence(Stream output, string id, string runId, CancellationToken token) => paperJournal.Export(output, id, runId, token);
    public Task ExportOperations(Stream output, string id, CancellationToken token) => operationLog.Export(output, id, token);
    public bool JournalReady => operationLog.Ready;
    public Task WaitForJournal(CancellationToken cancellationToken) => operationLog.WhenReady(cancellationToken);
}
