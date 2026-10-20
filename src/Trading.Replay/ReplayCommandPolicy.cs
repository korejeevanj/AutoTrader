using Trading.Engine;

namespace Trading.Replay;

/// <summary>Every command a replay job accepts. Speed is validated for all of them, as the host always has.</summary>
public abstract record ReplayCommand
{
    public double Speed { get; init; } = 60;

    public sealed record Retry : ReplayCommand;
    public sealed record RetryFailed : ReplayCommand;
    public sealed record RefreshGaps : ReplayCommand;
    public sealed record Cancel : ReplayCommand;
    public sealed record CancelPreparation : ReplayCommand;
    public sealed record Pause : ReplayCommand;
    public sealed record Step : ReplayCommand;
    public sealed record Play : ReplayCommand;
    public sealed record Run : ReplayCommand;
    public sealed record RunAllPresets : ReplayCommand;
    public sealed record SetSpeed : ReplayCommand;
    public sealed record SetProfitExit(ProfitExitConfiguration? Policy) : ReplayCommand;
    public sealed record Publish : ReplayCommand;
    public sealed record Restart : ReplayCommand;
    public sealed record PauseEntries : ReplayCommand;
    public sealed record EnableEntries : ReplayCommand;
}

/// <summary>What a job's rules need to know to decide whether a command may run right now.</summary>
public sealed record ReplayJobFacts
{
    public ReplayJobState State { get; init; }
    public bool Acquiring { get; init; }
    public bool Publishing { get; init; }
    public bool JournalPending { get; init; }
    public bool LogUnavailable { get; init; }
    public bool ReportsAvailable { get; init; }
    public bool CanReplay { get; init; }
    public bool HasSession { get; init; }
    public bool PriceOnly { get; init; }
    public bool SessionStarted { get; init; }
    public bool CanPublish { get; init; }
    public bool HasPaperState { get; init; }
    public bool HasPaperExposure { get; init; }
    public bool HasJournalRun { get; init; }
    /// <summary>Instruments a Retry-failed would request again.</summary>
    public int FailedTargets { get; init; }
    /// <summary>Instruments a Refresh-gaps would request again.</summary>
    public int GapTargets { get; init; }
}

public abstract record ReplayCommandDecision
{
    /// <summary>The command may run; the job's handler performs the transition.</summary>
    public sealed record Proceed : ReplayCommandDecision;
    /// <summary>Already in the requested situation; answered as success without changing anything.</summary>
    public sealed record NoOp : ReplayCommandDecision;
    public sealed record Rejected(string Message) : ReplayCommandDecision;
    /// <summary>Operational log storage is down, so nothing but pause and cancel may run.</summary>
    public sealed record StorageUnavailable : ReplayCommandDecision;
}

/// <summary>
/// One decision per command, applied in a fixed precedence: speed, operational log, report
/// publication, pending journal commit, idempotent no-ops, then the command's own checks.
/// Pure: it reads facts and never touches a job, so every state and command is testable.
/// </summary>
public static class ReplayCommandPolicy
{
    private static readonly ReplayCommandDecision Go = new ReplayCommandDecision.Proceed();
    private static readonly ReplayCommandDecision Nothing = new ReplayCommandDecision.NoOp();
    private static ReplayCommandDecision Reject(string message) => new ReplayCommandDecision.Rejected(message);

    /// <summary>Speed applies to every command, even those that ignore it, so it is checked first and without a job.</summary>
    public static ReplayCommandDecision.Rejected? InvalidSpeed(ReplayCommand command) =>
        !double.IsFinite(command.Speed) || command.Speed <= 0 || command.Speed > 100000
            ? new ReplayCommandDecision.Rejected("Playback speed must be positive and at most 100000.") : null;

    public static ReplayCommandDecision Decide(ReplayJobFacts facts, ReplayCommand command)
    {
        if (InvalidSpeed(command) is { } invalid) return invalid;
        var cancelling = command is ReplayCommand.Cancel or ReplayCommand.CancelPreparation;
        if (facts.LogUnavailable && command is not (ReplayCommand.Pause or ReplayCommand.Cancel or ReplayCommand.CancelPreparation))
            return new ReplayCommandDecision.StorageUnavailable();
        if (facts.Publishing && !cancelling) return Reject("Wait for report publication before changing replay state.");
        if (facts.JournalPending && command is not (ReplayCommand.Step or ReplayCommand.Play or ReplayCommand.Run
            or ReplayCommand.Pause or ReplayCommand.Cancel or ReplayCommand.CancelPreparation))
            return Reject("A paper batch is awaiting durable commit. Retry Step before changing inputs or paper controls.");
        if (command is ReplayCommand.Cancel or ReplayCommand.Pause && facts.State == ReplayJobState.Completed && facts.ReportsAvailable && !facts.Acquiring)
            return Nothing;

        switch (command)
        {
            case ReplayCommand.SetProfitExit setting:
                if (setting.Policy is null) return Reject("Supply the profit-exit policy and net rupee settings.");
                if (!facts.CanReplay || facts.PriceOnly || !facts.HasSession || facts.Acquiring || facts.State == ReplayJobState.Playing)
                    return Reject("Pause a fully prepared strategy replay before changing its profit-exit policy.");
                if (!facts.HasPaperState) return Reject("Engine-backed paper state is required.");
                if (facts.HasPaperExposure) return Reject("Close open/pending paper positions before changing policy, or explicitly restart the replay first.");
                return Go;
            case ReplayCommand.SetSpeed: return Go;
            case ReplayCommand.Retry:
                if (facts.Acquiring) return Go;
                if (facts.State == ReplayJobState.Reporting) return Reject("Wait for report publication before retrying.");
                return facts.State.IsPreparing() ? Nothing : Go;
            case ReplayCommand.RetryFailed or ReplayCommand.RefreshGaps:
                if (facts.Acquiring || facts.State is ReplayJobState.Playing or ReplayJobState.Reporting)
                    return Reject("Pause playback and wait for acquisition/reporting before retrying failed requests.");
                return (command is ReplayCommand.RetryFailed ? facts.FailedTargets : facts.GapTargets) == 0
                    ? Reject("There are no matching acquisition gaps. Empty responses require Refresh coverage gaps, not Retry failed requests.") : Go;
            case ReplayCommand.CancelPreparation:
                if (facts.Acquiring && facts.PriceOnly && facts.SessionStarted) return Go;
                return !facts.Acquiring && !facts.State.IsPreparing() ? Nothing : Go;
            case ReplayCommand.Cancel or ReplayCommand.Pause: return Go;
            case ReplayCommand.PauseEntries or ReplayCommand.EnableEntries:
                return facts.PriceOnly || !facts.HasJournalRun ? Reject("Engine-backed replay is required.") : Go;
        }

        if (facts.State == ReplayJobState.Reporting) return Reject("Wait for this report to finish publishing.");
        if (!facts.CanReplay || !facts.HasSession) return Reject("Dataset is not ready for replay.");
        if (command is ReplayCommand.Publish)
            return facts.CanPublish ? Go : Reject("Commit and finish the requested replay scope before publishing reports.");
        if (command is ReplayCommand.RunAllPresets && (facts.PriceOnly || facts.State == ReplayJobState.Playing))
            return Reject("Pause an engine-backed replay before starting an explicit all-preset comparison.");
        return Go;
    }
}
