using Trading.Data;
using Trading.Engine;

namespace Trading.Replay;

public sealed partial class ReplayJobs
{
    /// <summary>
    /// Applies one command under the job's gate: the policy decides, then a handler makes the transition.
    /// Domain illegality is a typed result, never an exception; only a genuine programming error throws.
    /// </summary>
    public async Task<ReplayCommandResult> SendAsync(string id, ReplayCommand command)
    {
        if (!jobs.TryGetValue(id, out var runtime)) return new ReplayCommandResult.UnknownJob();
        if (ReplayCommandPolicy.InvalidSpeed(command) is { } invalid) return new ReplayCommandResult.Rejected(invalid.Message);
        try { return await Apply(runtime, command); }
        catch (ArgumentException error) { return new ReplayCommandResult.Rejected(error.Message); }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException) { return new ReplayCommandResult.StorageUnavailable(); }
    }

    private async Task<ReplayCommandResult> Apply(RuntimeJob runtime, ReplayCommand command)
    {
        var id = runtime.Job.Id;
        if (command is ReplayCommand.Retry or ReplayCommand.Publish)
        {
            Task? finishing = null;
            await runtime.Gate.WaitAsync();
            try { if (runtime.Publishing && runtime.Job.State == ReplayJobState.Failed) finishing = runtime.Playback; }
            finally { runtime.Gate.Release(); }
            if (finishing is not null)
                try { await finishing; }
                catch (Exception error) when (error is IOException or System.Data.Common.DbException) { /* Retry resumes from the already exposed storage failure. */ }
        }
        await runtime.Gate.WaitAsync();
        try
        {
            runtime.Pacer ??= new(clock);
            Log(runtime, "COMMAND_" + command.GetType().Name.ToUpperInvariant(), new { command.Speed });
            switch (ReplayCommandPolicy.Decide(Facts(runtime), command))
            {
                case ReplayCommandDecision.NoOp: return new ReplayCommandResult.NoOp(Snapshot(id)!);
                case ReplayCommandDecision.Rejected rejected: return new ReplayCommandResult.Rejected(rejected.Message);
                case ReplayCommandDecision.StorageUnavailable: return new ReplayCommandResult.StorageUnavailable();
            }
            await Transition(runtime, command);
            return new ReplayCommandResult.Applied(Snapshot(id)!);
        }
        finally { runtime.Gate.Release(); }
    }

    private static ReplayJobFacts Facts(RuntimeJob runtime)
    {
        var paper = runtime.Session?.PaperState;
        return new()
        {
            State = runtime.Job.State, Acquiring = runtime.Acquiring, Publishing = runtime.Publishing, JournalPending = runtime.JournalPending,
            LogUnavailable = runtime.DiagnosticError is not null, ReportsAvailable = runtime.Job.ReportsAvailable, CanReplay = runtime.Job.CanReplay,
            HasSession = runtime.Session is not null, PriceOnly = runtime.PriceOnly, SessionStarted = runtime.Session?.CurrentTimeUtc is not null,
            CanPublish = runtime.Lifecycle?.CanPublish == true, HasPaperState = paper is not null,
            HasPaperExposure = paper is not null && (paper.Positions.Count != 0 || paper.PendingIntents.Count != 0),
            HasJournalRun = runtime.JournalRunId is not null,
            FailedTargets = runtime.AttemptEvidence.RefetchTargets(failedOnly: true).Count, GapTargets = runtime.AttemptEvidence.RefetchTargets(failedOnly: false).Count
        };
    }

    // The policy already established that the command is legal for the job's current facts.
    private async Task Transition(RuntimeJob runtime, ReplayCommand command)
    {
        var id = runtime.Job.Id;
        switch (command)
        {
            case ReplayCommand.SetProfitExit setting: await SetProfitExit(runtime, setting.Policy!); return;
            case ReplayCommand.SetSpeed:
                runtime.Speed = command.Speed; runtime.Pacer!.Reanchor(runtime.Session?.CurrentTimeUtc, command.Speed);
                WakePlayback(runtime); await Save(runtime); return;
            case ReplayCommand.Retry:
                if (runtime.Acquiring)
                {
                    // Failed state can be visible before worker cleanup finishes. Retain
                    // that retry just like a retry received during cancellation.
                    if (runtime.Cancellation.IsCancellationRequested || runtime.Job.State is ReplayJobState.Failed or ReplayJobState.AuthenticationRequired or ReplayJobState.Interrupted)
                    { runtime.RetryAfterAcquisition = true; runtime.Job = runtime.Job with { Message = "Retry will resume once the current acquisition stops." }; await Save(runtime); }
                    return;
                }
                runtime.Session?.Pause();
                runtime.Qualification = null;
                runtime.Job = runtime.Job with { State = ReplayJobState.Queued, Message = "Retry queued; valid cached responses will be reused.", CanReplay = false };
                await Save(runtime); Enqueue(id); return;
            case ReplayCommand.RetryFailed or ReplayCommand.RefreshGaps:
                runtime.RetryOnlyFailed = runtime.AttemptEvidence.RefetchTargets(command is ReplayCommand.RetryFailed); runtime.Session?.Pause();
                runtime.Qualification = null;
                runtime.Job = runtime.Job with { State = ReplayJobState.Queued, CanReplay = false, PreparationComplete = false,
                    StartNewRunAfterPreparation = true,
                    Message = "Requesting a new revision for selected acquisition gaps; other pinned inputs and earlier run evidence remain preserved." };
                await Save(runtime); Enqueue(id); return;
            case ReplayCommand.CancelPreparation or ReplayCommand.Cancel:
                if (command is ReplayCommand.CancelPreparation && runtime.Acquiring && runtime.PriceOnly && runtime.Session?.CurrentTimeUtc is not null)
                { runtime.Cancellation.Cancel(); return; }
                WakePlayback(runtime);
                runtime.Cancellation.Cancel(); runtime.Session?.Pause();
                runtime.Job = runtime.Job with { State = ReplayJobState.Cancelled, Message = "Canceled. Completed cached files and finished reports remain available." };
                await Save(runtime); return;
            case ReplayCommand.Pause:
                WakePlayback(runtime);
                runtime.Session?.Pause(); runtime.Job = runtime.Job with { State = runtime.Job.CanReplay ? ReplayJobState.Paused : runtime.Job.State, Message = "Replay paused at the current simulated timestamp." };
                await Save(runtime); return;
            case ReplayCommand.PauseEntries or ReplayCommand.EnableEntries:
                SetPaperEntries(runtime, command is ReplayCommand.PauseEntries); await Save(runtime); return;
            case ReplayCommand.Publish:
                runtime.Job = runtime.Job with { State = ReplayJobState.Reporting }; runtime.Publishing = true;
                await Save(runtime); work.Begin(); runtime.Playback = Task.Run(() => Publish(runtime)); return;
            case ReplayCommand.Restart:
                runtime.Session!.Pause(); await ResetSession(runtime); runtime.Job = runtime.Job with { State = ReplayJobState.Paused, ReportsAvailable = false };
                await Save(runtime); return;
        }
        await Play(runtime, command);
    }

    // Step, Play, Run and RunAllPresets: open the playback scope, then advance once or start the loop.
    private async Task Play(RuntimeJob runtime, ReplayCommand command)
    {
        var before = (runtime.Job.Run, runtime.PresetIndex);
        var failedTransition = false;
        try { runtime.Lifecycle!.PreparePlayback(command switch
        { ReplayCommand.Step => ReplayPlaybackRequest.Step, ReplayCommand.Play => ReplayPlaybackRequest.Resume, ReplayCommand.Run => ReplayPlaybackRequest.CurrentPreset, _ => ReplayPlaybackRequest.AllPresets }); }
        catch { failedTransition = true; runtime.Job = runtime.Job with { State = ReplayJobState.Paused }; throw; }
        finally { SyncRunPresentation(runtime, before); if (failedTransition) PublishSnapshot(runtime); }
        if (command is ReplayCommand.Step)
        {
            runtime.Session!.Pause(); Advance(runtime);
            var finished = runtime.Session.IsComplete && !CompletePreset(runtime);
            runtime.Job = runtime.Job with { State = finished ? ReplayJobState.Reporting : ReplayJobState.Paused };
            await Save(runtime);
            if (finished) { runtime.Publishing = true; work.Begin(); runtime.Playback = Publish(runtime); }
            return;
        }
        runtime.Speed = command.Speed;
        runtime.Batch = command is ReplayCommand.Run or ReplayCommand.RunAllPresets; runtime.Session!.Resume();
        runtime.Pacer!.Reanchor(runtime.Session.CurrentTimeUtc, runtime.Speed); WakePlayback(runtime);
        runtime.Job = runtime.Job with { State = ReplayJobState.Playing, ReportsAvailable = false,
            Message = runtime.AllPresets ? $"Explicit preset comparison: {runtime.PresetIndex + 1}/{runtime.Replay!.Presets.Count} ({runtime.Replay.Presets[runtime.PresetIndex].Id}). The clock restarts for each preset."
                : runtime.Batch ? $"Running the current preset ({runtime.Replay!.Presets[runtime.PresetIndex].Id}) to the end once at maximum throughput."
                : $"Paced replay is running at requested {runtime.Speed}×; achieved speed is measured separately." };
        await Save(runtime);
        if (!runtime.PlayLoopActive)
        {
            runtime.PlayLoopActive = true; var loop = ++runtime.PlayLoopId;
            work.Begin(); runtime.Playback = Task.Run(() => PlayLoop(runtime, loop));
        }
    }

    private static EngineConfiguration ApplyProfitExit(EngineConfiguration baseline, ProfitExitConfiguration? policy)
    {
        if (policy is null) return baseline;
        var errors = policy.Validate();
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors));
        return EngineConfiguration.FromJson((baseline with
        {
            Version = $"{baseline.Version}|{policy.Version}:{policy.Mode}",
            Strategies = baseline.Strategies.Select(profile => profile with
                { Position = profile.Position with { ProfitExit = policy } }).ToArray()
        }).ToJson());
    }

    // Called under the job gate. Selection is run configuration, never a mutation of
    // an existing position's frozen strategy or of acquisition/source evidence.
    private async Task SetProfitExit(RuntimeJob runtime, ProfitExitConfiguration policy)
    {
        var frozen = acquisition.FrozenFor(runtime.Job.Id, runtime.Job.Date)
            ?? throw new ArgumentException("Recover the frozen acquisition before selecting a profit policy.");
        var effective = runtime.Engine ?? throw new ArgumentException("Recover effective Engine settings before selecting a profit policy.");
        // Preserve session qualification and all other effective non-profit settings.
        // Only take the original version label from acquisition, avoiding suffix chains.
        var baseline = effective with { Version = EngineConfiguration.FromJson(frozen.EngineJson).Version };
        var engine = ApplyProfitExit(baseline, policy);
        try
        {
            runtime.Session!.Pause(); runtime.RecoverSavedRun = false; runtime.Engine = engine;
            runtime.Job = runtime.Job with { ProfitExitOverride = policy };
            await ResetSession(runtime);
            runtime.Qualification = new HistoricalReadiness(runtime.Dataset!, runtime.Replay!, engine).Summary(runtime.Job.Date);
            runtime.Job = runtime.Job with { State = ReplayJobState.Paused, CanReplay = true, ReportsAvailable = false,
                Message = $"New {policy.Mode} profit-exit variant opened on the same frozen dataset. Playback is paused; earlier run evidence remains preserved." };
            Log(runtime, "PROFIT_EXIT_POLICY_SELECTED", new { policy, runtime.Job.Run });
            if (runtime.DiagnosticError is not null) throw new IOException("Profit-policy operation could not be journaled.");
            await Save(runtime);
        }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException)
        {
            runtime.Session?.Pause(); runtime.Qualification = null;
            runtime.Job = runtime.Job with { State = ReplayJobState.Failed, CanReplay = false,
                Message = "Profit-policy variant could not commit. Restore storage and retry preparation before playback.",
                Warnings = runtime.Job.Warnings.Append("PROFIT_EXIT_POLICY_COMMIT_FAILED").Distinct().ToArray() };
            PublishSnapshot(runtime);
            throw;
        }
    }

    private void SetPaperEntries(RuntimeJob runtime, bool paused)
    {
        try
        {
            runtime.Lifecycle!.SetPaperEntriesPaused(paused);
            runtime.Job = runtime.Job with { Message = paused ? "Paper entries paused. Existing positions remain monitored as replay advances." : "Paper entries enabled; all strategy and evidence gates still apply." };
        }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException)
        {
            runtime.Job = runtime.Job with { State = ReplayJobState.Paused, Message = "Entry-control commit failed. Playback is paused and the previous entry setting was restored; retry the control after restoring storage." };
            WakePlayback(runtime);
            PublishSnapshot(runtime); throw;
        }
    }

    private static void CacheDecisionRows(RuntimeJob runtime, ReplayStep step)
    {
        foreach (var decision in step.Decisions)
        {
            runtime.Decisions.Enqueue(decision);
            while (runtime.Decisions.Count > 200) runtime.Decisions.Dequeue();
        }
    }
}
