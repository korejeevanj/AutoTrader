using Trading.Data;
using Trading.Replay;

namespace Trading.Api;

public sealed partial class HistoricalReplayService
{
    private void SetPaperEntries(RuntimeJob runtime, bool paused)
    {
        if (runtime.PriceOnly || runtime.JournalRunId is null) throw new ArgumentException("Engine-backed replay is required.");
        try
        {
            runtime.Lifecycle!.SetPaperEntriesPaused(paused);
            runtime.Job = runtime.Job with { Message = paused ? "Paper entries paused. Existing positions remain monitored as replay advances." : "Paper entries enabled; all strategy and evidence gates still apply." };
        }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException)
        {
            runtime.Job = runtime.Job with { State = "Paused", Message = "Entry-control commit failed. Playback is paused and the previous entry setting was restored; retry the control after restoring storage." };
            WakePlayback(runtime);
            PublishSnapshot(runtime); throw;
        }
    }

    private static void CacheDecisionRows(RuntimeJob runtime, ReplayStep step)
    {
        foreach (var decision in step.Decisions)
        {
            runtime.Decisions.Enqueue(new(decision.ContractId, decision.Class, decision.Action, decision.Reasons,
                decision.TimeUtc, decision.IndexId, decision.SetupId, decision.Gates, decision.Rank, decision.RankingMetrics));
            while (runtime.Decisions.Count > 200) runtime.Decisions.Dequeue();
        }
    }

    public async Task<IReadOnlyList<PaperJournalRow>?> PaperLog(string id, int limit, string? category)
    {
        if (!jobs.TryGetValue(id, out var runtime)) return null;
        var runId = Volatile.Read(ref runtime.PublishedView)?.PaperRunId;
        return await Task.Run<IReadOnlyList<PaperJournalRow>>(() => runId is null ? [] : paperJournal.Read(runId, limit, category));
    }
}
