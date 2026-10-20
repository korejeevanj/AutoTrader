using Trading.Data;
using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

/// <summary>
/// The mutable state of one job, touched only under its gate by ReplayJobs. It also owns the read-side
/// projections of that state: what phase the job is in, the facts its command rules need and its snapshot.
/// </summary>
internal sealed class ReplayJobRuntime(ReplayJob job)
{
    public ReplayJob Job = job;
    public readonly SemaphoreSlim Gate = new(1, 1);
    public CancellationTokenSource Cancellation = new();
    public HistoricalDataset? Dataset;
    public ReplayConfiguration? Replay;
    public EngineConfiguration? Engine;
    public ReplayRunLifecycle? Lifecycle;
    public ReplayRunLifecycle? Session => Lifecycle?.HasSession == true ? Lifecycle : null;
    public ReplayStep? LastStep => Lifecycle?.LastStep;
    public readonly Queue<ReplayDecisionTrace> Decisions = new();
    public Task? Playback;
    public bool Batch;
    public bool AllPresets => Lifecycle?.HasSession == true ? Lifecycle.AllPresets : Job.AllPresets;
    public double Speed = 60;
    public int PresetIndex => Lifecycle?.PresetIndex ?? 0;
    public bool Acquiring;
    public bool RetryAfterAcquisition;
    public bool Publishing;
    public bool PlayLoopActive;
    public int PlayLoopId;
    public bool PriceOnly => Lifecycle?.PriceOnly ?? false;
    public HistoricalDataset? EnrichedDataset;
    public ReplayConfiguration? EnrichedReplay;
    public EngineConfiguration? EnrichedEngine;
    public string? JournalRunId => Lifecycle?.JournalRunId;
    public int StepNumber => Lifecycle?.StepNumber ?? 0;
    public bool JournalPending => Lifecycle?.HasPendingCommit == true;
    public bool RecoverSavedRun;
    public ReplayPacingClock? Pacer;
    public CancellationTokenSource Wake = new();
    public long Revision;
    public ReplayJobSnapshot? Published;
    public HistoricalReadinessSummary? Qualification;
    public AcquisitionAttemptEvidence AttemptEvidence = new(job.Attempt, job.Date,
        job.Instruments.Select(row => new HistoricalInstrumentAcquisition(row.Id, row.Token, row.Role, row.State, row.Rows, row.Cached, row.Error)));
    public HashSet<string>? RetryOnlyFailed;
    public string? DiagnosticError;
    public string? CheckpointError;

    /// <summary>
    /// A state can lag the work: Failed, Cancelled, AuthenticationRequired and Interrupted are shown while the
    /// worker is still stopping, which is the window in which a retry must be deferred rather than lost.
    /// </summary>
    public ReplayJobPhase Phase
    {
        get
        {
            var phase = ReplayJobPhase.Idle;
            if (Acquiring)
            {
                phase |= ReplayJobPhase.Acquiring;
                if (Cancellation.IsCancellationRequested || Job.State is ReplayJobState.Failed or ReplayJobState.AuthenticationRequired or ReplayJobState.Interrupted)
                    phase |= ReplayJobPhase.Stopping;
            }
            if (Publishing) phase |= ReplayJobPhase.Publishing;
            return phase;
        }
    }

    public ReplayJobFacts Facts()
    {
        var paper = Session?.PaperState;
        return new()
        {
            State = Job.State, Phase = Phase, JournalPending = JournalPending,
            LogUnavailable = DiagnosticError is not null, ReportsAvailable = Job.ReportsAvailable, CanReplay = Job.CanReplay,
            HasSession = Session is not null, PriceOnly = PriceOnly, SessionStarted = Session?.CurrentTimeUtc is not null,
            CanPublish = Lifecycle?.CanPublish == true, HasPaperState = paper is not null,
            HasPaperExposure = paper is not null && (paper.Positions.Count != 0 || paper.PendingIntents.Count != 0),
            HasJournalRun = JournalRunId is not null,
            FailedTargets = AttemptEvidence.RefetchTargets(failedOnly: true).Count, GapTargets = AttemptEvidence.RefetchTargets(failedOnly: false).Count
        };
    }

    public ReplayJobSnapshot Snapshot(IReadOnlyList<string> reportFiles)
    {
        var session = Session; var inputs = Lifecycle?.Inputs;
        return new(Job, Phase, RetryAfterAcquisition, Revision, PriceOnly,
            session?.CurrentTimeUtc, session?.IsPaused ?? true, session?.IsComplete ?? false,
            inputs?.Dataset ?? Dataset, inputs?.Replay ?? Replay, inputs?.Engine ?? Engine,
            PresetIndex, AllPresets, LastStep, Decisions.ToArray(),
            session?.VisibleTrades ?? [], session?.VisibleConstituents("BANKNIFTY") ?? [], session?.PaperState, JournalRunId,
            Pacer?.Snapshot(session?.IsPaused ?? true, Batch), JournalPending,
            CheckpointError ?? Lifecycle?.ManifestError ?? DiagnosticError, reportFiles, AttemptEvidence, Qualification);
    }
}
