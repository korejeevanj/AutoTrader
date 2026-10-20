namespace Trading.Api;

public sealed class HistoricalReplayOptions
{
    public string RootDirectory { get; set; } = "../../data/replay";
    public string ReplayConfigurationPath { get; set; } = "../Trading.Replay/replay.example.json";
    public int MinimumRequestSpacingMilliseconds { get; set; } = 400;
    public int MaximumProviderAttempts { get; set; } = 3;
    public bool ArchiveCurrentMaster { get; set; } = true;
    public bool AcquirePublishedBankInputs { get; set; } = true;
}
public sealed class KiteCredentialOptions
{
    public string ApiKey { get; set; } = "";
    public string AccessToken { get; set; } = "";
}
public sealed record PrepareHistoricalReplayRequest(DateOnly Date, bool Refresh = false);
public sealed record HistoricalReplayCommand(string Action, double Speed = 60, Trading.Engine.ProfitExitConfiguration? ProfitExit = null);
/// <summary>The wire shape of a replay job: states are their names, as clients and saved jobs have always had them.</summary>
public sealed record HistoricalReplayJob(string Id, DateOnly Date, bool Refresh, string RequestFingerprint,
    string State, string Message, string CalendarStatus, string? CatalogSource, IReadOnlyList<string> Warnings,
    IReadOnlyList<Trading.Replay.ReplayInstrumentProgress> Instruments, int Total, int Finished, bool CanReplay,
    string Mode, DateTimeOffset UpdatedAtUtc, int Attempt = 0, int Run = 0, bool ReportsAvailable = false,
    bool PreparingData = false, string? ActivePresetId = null, bool PreparationComplete = false, bool AllPresets = false,
    Trading.Engine.ProfitExitConfiguration? ProfitExitOverride = null, bool StartNewRunAfterPreparation = false)
{
    public static HistoricalReplayJob From(Trading.Replay.ReplayJob job, bool preparingData) => new(job.Id, job.Date, job.Refresh,
        job.RequestFingerprint, job.State.ToString(), job.Message, job.CalendarStatus, job.CatalogSource, job.Warnings, job.Instruments,
        job.Total, job.Finished, job.CanReplay, job.Mode, job.UpdatedAtUtc, job.Attempt, job.Run, job.ReportsAvailable, preparingData,
        job.ActivePresetId, job.PreparationComplete, job.AllPresets, job.ProfitExitOverride, job.StartNewRunAfterPreparation);
}
public sealed record ReplayConnectionStatus(string CredentialStatus, DateOnly LatestCompletedDate,
    string GuideUrl, string MetadataMessage);
public sealed record ReplayUiBar(string Id, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset AvailableAtUtc,
    decimal Open, decimal High, decimal Low, decimal Close);
public sealed record ReplayUiDecision(string ContractId, string Class, string Action, IReadOnlyList<string> Reasons,
    DateTimeOffset? TimeUtc = null, string IndexId = "", string? SetupId = null, IReadOnlyList<Trading.Engine.GateResult>? Gates = null,
    int? Rank = null, Trading.Replay.ReplayRankingMetrics? RankingMetrics = null);
public sealed record ReplayUiTrade(string Id, string ContractId, int Units, DateTimeOffset EntryTimeUtc, decimal EntryPrice,
    DateTimeOffset? ExitKnownAtUtc, decimal? ExitPrice, decimal? NetPnl, string? ExitReason, string EvidenceClass,
    Trading.Risk.ResearchFeeBreakdown? EntryFees = null, Trading.Risk.ResearchFeeBreakdown? ExitFees = null, decimal? GrossPnl = null,
    Trading.Replay.ReplayProfitExitEvidence? ProfitExit = null);
public sealed record ReplayUiFeature(decimal? OpeningHigh, decimal? OpeningLow, decimal? Vwap,
    decimal? AboveWeight, decimal? BelowWeight, decimal? MissingWeight, string? Direction, string? SetupId,
    ReplayUiDirectionalEvidence? DirectionalEvidence = null);
public sealed record ReplayUiDirectionalEvidence(string Version, string Status, DateTimeOffset AsOfUtc,
    DateTimeOffset? BarEndUtc, DateTimeOffset? EvidenceAvailableAtUtc, bool MeetsDirectionalRules,
    decimal? CloseMinusOpeningHigh, decimal? CloseMinusOpeningLow, decimal? CloseMinusVwap,
    decimal? Return1MinutePercent, decimal? Return5MinutePercent, int? ConfirmingCount,
    int? OpposingCount, int? NeutralCount, decimal? TopThreeConfirmingWeightPercent,
    int? MinimumConfirmingConstituents, IReadOnlyList<string> Reasons);
public sealed record ReplayUiSettings(string Version, string Preset, decimal GrossStop, decimal MaximumNetRisk,
    int CandidateBatchSize, int SpreadTicks, int SlippageTicks, Trading.Engine.ProfitExitConfiguration? ProfitExit = null);
public sealed record ReplayChartInstrument(string Id, string Role);
public sealed record ReplayPresetProgress(string Id, int Number, int Total, bool AllPresets);
public sealed record HistoricalReplayView(HistoricalReplayJob Job, DateTimeOffset? SimulatedTimeUtc,
    bool Paused, bool Complete, IReadOnlyList<ReplayUiBar> Bars, ReplayUiFeature? Feature,
    IReadOnlyList<Trading.Replay.ReplayConstituentView> Constituents,
    IReadOnlyList<ReplayUiDecision> Decisions, IReadOnlyList<ReplayUiTrade> Trades,
    int OpenPositions, int PendingEntries, decimal? NetSessionPnl, ReplayUiSettings? Settings,
    IReadOnlyList<string> ReportFiles, string PlaybackKind = "Strategy", string ChartLabel = "Historical futures context",
    string? ChartInstrumentId = null, IReadOnlyList<ReplayChartInstrument>? ChartInstruments = null,
    Trading.Replay.PaperStateView? Paper = null, string? PaperRunId = null, Trading.Replay.ReplayTiming? Timing = null, long Revision = 0, bool JournalPending = false, string? DiagnosticError = null,
    ReplayPresetProgress? PresetProgress = null, string? ServerInstanceId = null)
{
    internal IReadOnlyDictionary<string, ReplayUiBar[]> Series { get; init; } = new Dictionary<string, ReplayUiBar[]>();
}
