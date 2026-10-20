using Trading.Domain;
using Trading.Engine;
using Trading.Risk;

namespace Trading.Replay;

public sealed record CandleOpening(string InstrumentId, string CandleId, DateTimeOffset StartUtc, decimal Open);
public sealed record ReplayBatch(DateTimeOffset TimeUtc, IReadOnlyList<HistoricalCandle> Completed,
    IReadOnlyList<CandleOpening> Openings);
public sealed record ReplayFeatureTrace(DateTimeOffset TimeUtc, string IndexId, string? BarId,
    decimal? OpeningHigh, decimal? OpeningLow, decimal? Vwap, decimal? LongStop, decimal? ShortStop,
    decimal? AboveWeight, decimal? BelowWeight, decimal? MissingWeight, string? SetupId,
    TradeDirection? Direction, IReadOnlyList<GateResult> Gates,
    DirectionalEvidence? DirectionalEvidence = null, bool IsEntryDecision = false);
public sealed record ReplayDecisionTrace(DateTimeOffset TimeUtc, string IndexId, string ContractId,
    string Class, string Action, string? SetupId, IReadOnlyList<string> Reasons, IReadOnlyList<GateResult> Gates,
    int? Rank = null, ReplayRankingMetrics? RankingMetrics = null);
public sealed record ReplayRankingMetrics(decimal SpreadPercent, decimal RoundTripFriction, decimal StrikeDistance);
public sealed record ReplayProfitExitEvidence(ProfitExitConfiguration Policy, decimal? PeakNetLiquidationPnl = null,
    DateTimeOffset? TriggeredAtUtc = null, decimal? TriggerNetLiquidationPnl = null,
    decimal? TriggerThreshold = null, decimal? RealizedShortfall = null);
public sealed record ResearchTrade(string Id, string IndexId, string ContractId, string SetupId,
    TradeDirection Direction, int Units, DateTimeOffset EntryTimeUtc, decimal EntryPrice,
    ResearchFeeBreakdown EntryFees, string EntryCandleId,
    DateTimeOffset? ExitKnownAtUtc = null, DateTimeOffset? ExitIntervalStartUtc = null,
    DateTimeOffset? ExitIntervalEndUtc = null, decimal? ExitPrice = null,
    ResearchFeeBreakdown? ExitFees = null, decimal? GrossPnl = null, decimal? NetPnl = null,
    string? ExitReason = null, decimal? GrossStopOverrun = null,
    string EvidenceClass = "HYPOTHETICAL_CANDLE_RESEARCH", string? ExitFeeAttribution = null,
    ReplayProfitExitEvidence? ProfitExit = null);
public sealed record ResearchEquityPoint(DateTimeOffset TimeUtc, decimal? NetSessionPnl);
public sealed record PaperIntentView(string Id, string ContractId, string SetupId, DateTimeOffset AcceptedAtUtc,
    DateTimeOffset ExpiresAtUtc, decimal ReservedCash);
public sealed record PaperPositionView(string Id, string ContractId, int Units, DateTimeOffset EntryTimeUtc, decimal EntryPrice,
    decimal? GrossPnl, decimal? NetPnl, string PolicyAction, IReadOnlyList<string> Reasons, string? PendingExitReason,
    ReplayProfitExitEvidence? ProfitExit = null, decimal? CurrentProfitExitThreshold = null);
public sealed record PaperStateView(decimal StartingCapital, decimal Cash, decimal AvailableCash, decimal ReservedCash,
    decimal RealizedNetPnl, decimal PaidFees, bool EntriesPaused, int AcceptedAttempts,
    IReadOnlyList<PaperIntentView> PendingIntents, IReadOnlyList<PaperPositionView> Positions);
public sealed record ReplayConstituentView(string InstrumentId, decimal? WeightPercent, decimal? Close,
    decimal? Vwap, DateTimeOffset? PeriodEndUtc, string State);
public sealed record ReplayStep(DateTimeOffset TimeUtc, int AvailableCandleCount,
    IReadOnlyList<ReplayFeatureTrace> Features, IReadOnlyList<ReplayDecisionTrace> Decisions,
    int OpenPositions, int PendingEntries, decimal? NetSessionPnl,
    IReadOnlyList<HistoricalDecisionReadiness>? Readiness = null);
public sealed record ReplaySessionResult(string DatasetId, DateOnly TradingDate, string PresetId,
    IReadOnlyList<ReplayFeatureTrace> Features, IReadOnlyList<ReplayDecisionTrace> Decisions,
    IReadOnlyList<ResearchTrade> Trades, IReadOnlyList<ResearchEquityPoint> Equity,
    decimal RealizedNetPnl, decimal PaidFees, decimal ObservedNetDrawdown,
    bool Complete, IReadOnlyList<string> CompletionIssues, int PendingEntries, int PendingExits,
    IReadOnlyList<HistoricalDecisionReadiness>? Readiness = null);
public sealed record ReplayRunResult(IReadOnlyList<ReplaySessionResult> Sessions,
    IReadOnlyList<HistoricalQualityIssue> QualityIssues, HistoricalDataset? CapturedDataset = null,
    string ReplayConfigurationJson = "", string EngineConfigurationJson = "");
