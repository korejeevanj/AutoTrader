using Trading.Data;
using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

public sealed record ReplayInstrumentProgress(string Id, long Token, string Role, string State, int Rows, bool Cached, string? Error);

/// <summary>A replay job as persisted in job.json; field names and state values are the wire shape.</summary>
public sealed record ReplayJob(string Id, DateOnly Date, bool Refresh, string RequestFingerprint,
    ReplayJobState State, string Message, string CalendarStatus, string? CatalogSource, IReadOnlyList<string> Warnings,
    IReadOnlyList<ReplayInstrumentProgress> Instruments, int Total, int Finished, bool CanReplay,
    string Mode, DateTimeOffset UpdatedAtUtc, int Attempt = 0, int Run = 0, bool ReportsAvailable = false,
    bool PreparingData = false, string? ActivePresetId = null, bool PreparationComplete = false, bool AllPresets = false,
    ProfitExitConfiguration? ProfitExitOverride = null, bool StartNewRunAfterPreparation = false);

/// <summary>
/// An immutable, durable-after-the-fact view of one job: everything a host needs to present it.
/// A snapshot is published only after the change that produced it was checkpointed, and its
/// Revision rises on every publication.
/// </summary>
public sealed record ReplayJobSnapshot(ReplayJob Job, ReplayJobPhase Phase, bool RetryPending, long Revision, bool PriceOnly,
    DateTimeOffset? SimulatedTimeUtc, bool Paused, bool Complete,
    HistoricalDataset? Dataset, ReplayConfiguration? Replay, EngineConfiguration? Engine, int PresetIndex, bool AllPresets,
    ReplayStep? LastStep, IReadOnlyList<ReplayDecisionTrace> Decisions, IReadOnlyList<ResearchTrade> Trades,
    IReadOnlyList<ReplayConstituentView> Constituents, PaperStateView? Paper, string? PaperRunId, ReplayTiming? Timing,
    bool JournalPending, string? FailureNotice, IReadOnlyList<string> ReportFiles,
    AcquisitionAttemptEvidence AttemptEvidence, HistoricalReadinessSummary? Qualification)
{
    /// <summary>The acquisition worker still owns the job, even if its state already reads Failed or Cancelled.</summary>
    public bool Acquiring => Phase.HasFlag(ReplayJobPhase.Acquiring);
}

public abstract record ReplayCommandResult
{
    public sealed record Applied(ReplayJobSnapshot Snapshot) : ReplayCommandResult;
    public sealed record NoOp(ReplayJobSnapshot Snapshot) : ReplayCommandResult;
    /// <summary>Accepted, and will take effect when the acquisition worker releases the job; the snapshot says so via RetryPending.</summary>
    public sealed record Deferred(ReplayJobSnapshot Snapshot) : ReplayCommandResult;
    public sealed record Rejected(string Message) : ReplayCommandResult;
    /// <summary>Local storage failed while applying the command; the job is paused or failed and visible as such.</summary>
    public sealed record StorageUnavailable : ReplayCommandResult;
    public sealed record UnknownJob : ReplayCommandResult;
}
