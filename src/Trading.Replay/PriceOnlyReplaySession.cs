using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

public interface IHistoricalReplaySession
{
    bool IsPaused { get; }
    bool IsComplete { get; }
    DateTimeOffset? CurrentTimeUtc { get; }
    DateTimeOffset? NextTimeUtc { get; }
    IReadOnlyList<ResearchTrade> VisibleTrades { get; }
    IReadOnlyList<ReplayConstituentView> VisibleConstituents(string indexId);
    void Pause();
    void Resume();
    ReplayStep? Step();
    void ReleaseCommittedDecisionHistory();
    ReplaySessionResult Result { get; }
    bool PaperEntriesPaused { get; }
    void SetPaperEntriesPaused(bool paused);
    PaperStateView? PaperState { get; }
}

// An incomplete contract catalogue must not make already downloaded prices unplayable.
// This clock never evaluates trading policy or invents a futures/quote/fee/position.
public sealed class PriceOnlyReplaySession : IHistoricalReplaySession
{
    private readonly HistoricalDataset dataset;
    private readonly EngineSession session;
    private readonly IReadOnlyList<ReplayBatch> batches;
    private readonly List<ReplayDecisionTrace> decisions = [];
    private int position;
    private int admitted;
    public PriceOnlyReplaySession(HistoricalDataset dataset, DateOnly date, TimeSpan lag)
    {
        this.dataset = dataset;
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(dataset.Manifest.Indices[0].TimeZone);
        session = new(date.ToString("yyyy-MM-dd"), date, new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), timezone)));
        batches = ReplaySchedule.Build(dataset, session, lag);
    }
    public bool IsPaused { get; private set; } = true;
    public bool IsComplete => position == batches.Count;
    public DateTimeOffset? CurrentTimeUtc { get; private set; }
    public DateTimeOffset? NextTimeUtc => IsComplete ? null : batches[position].TimeUtc;
    public IReadOnlyList<ResearchTrade> VisibleTrades => [];
    public bool PaperEntriesPaused => true;
    public void SetPaperEntriesPaused(bool paused) { }
    public PaperStateView? PaperState => null;
    public IReadOnlyList<ReplayConstituentView> VisibleConstituents(string indexId) => [];
    public void Pause() => IsPaused = true;
    public void Resume() => IsPaused = false;
    public ReplayStep? Step()
    {
        if (IsComplete) return null;
        var batch = batches[position++]; CurrentTimeUtc = batch.TimeUtc; admitted += batch.Completed.Count;
        var diagnostic = new ReplayDecisionTrace(batch.TimeUtc, dataset.Manifest.Indices[0].Id, "", "PRICE_ONLY", "NOT_EVALUATED", null,
            ["STRATEGY_REQUIRES_VERIFIED_HISTORICAL_METADATA"], []);
        decisions.Add(diagnostic);
        return new(batch.TimeUtc, admitted, [], [diagnostic], 0, 0, null);
    }
    public void ReleaseCommittedDecisionHistory() => decisions.Clear();
    public ReplaySessionResult Result => new(dataset.Manifest.DatasetId, session.TradingDate, "price-only", [],
        Array.AsReadOnly(decisions.ToArray()), [], [], 0m, 0m, 0m, false,
        IsComplete ? ["PRICE_ONLY_NO_STRATEGY_EVALUATION"] : ["PRICE_ONLY_NO_STRATEGY_EVALUATION", "REPLAY_NOT_FINISHED"], 0, 0);
}
