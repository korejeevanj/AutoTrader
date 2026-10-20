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
        catch (Exception error) when (StorageFailure.Is(error)) { return new ReplayCommandResult.StorageUnavailable(); }
    }

    private async Task<ReplayCommandResult> Apply(ReplayJobRuntime runtime, ReplayCommand command)
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
                catch (Exception error) when (StorageFailure.Is(error)) { /* Retry resumes from the already exposed storage failure. */ }
        }
        await runtime.Gate.WaitAsync();
        try
        {
            runtime.Pacer ??= new(clock);
            Log(runtime, "COMMAND_" + command.GetType().Name.ToUpperInvariant(), new { command.Speed });
            switch (ReplayCommandPolicy.Decide(runtime.Facts(), command))
            {
                case ReplayCommandDecision.NoOp: return new ReplayCommandResult.NoOp(Snapshot(id)!);
                case ReplayCommandDecision.Rejected rejected: return new ReplayCommandResult.Rejected(rejected.Message);
                case ReplayCommandDecision.StorageUnavailable: return new ReplayCommandResult.StorageUnavailable();
                case ReplayCommandDecision.Defer:
                    // Failed state can be visible before worker cleanup finishes. Retain the retry; the
                    // worker re-queues the job once it releases it, unless the host is stopping.
                    runtime.RetryAfterAcquisition = true;
                    runtime.Job = runtime.Job with { Message = "Retry will resume once the current acquisition stops." };
                    await Save(runtime);
                    return new ReplayCommandResult.Deferred(Snapshot(id)!);
            }
            return await Transition(runtime, command) is { } refusal
                ? new ReplayCommandResult.Rejected(refusal) : new ReplayCommandResult.Applied(Snapshot(id)!);
        }
        finally { runtime.Gate.Release(); }
    }

    // The policy already established that the command is legal for the job's current facts. A handler
    // returns the reason when it finds the job's stored inputs cannot honour the command, otherwise null.
    private async Task<string?> Transition(ReplayJobRuntime runtime, ReplayCommand command)
    {
        var id = runtime.Job.Id;
        switch (command)
        {
            case ReplayCommand.SetProfitExit setting: return await SetProfitExit(runtime, setting.Policy!);
            case ReplayCommand.SetSpeed:
                runtime.Speed = command.Speed; runtime.Pacer!.Reanchor(runtime.Session?.CurrentTimeUtc, command.Speed);
                WakePlayback(runtime); await Save(runtime); return null;
            case ReplayCommand.Retry:
                runtime.Session?.Pause();
                runtime.Qualification = null;
                runtime.Job = runtime.Job with { State = ReplayJobState.Queued, Message = "Retry queued; valid cached responses will be reused.", CanReplay = false };
                await Save(runtime); Enqueue(id); return null;
            case ReplayCommand.RetryFailed or ReplayCommand.RefreshGaps:
                runtime.RetryOnlyFailed = runtime.AttemptEvidence.RefetchTargets(command is ReplayCommand.RetryFailed); runtime.Session?.Pause();
                runtime.Qualification = null;
                runtime.Job = runtime.Job with { State = ReplayJobState.Queued, CanReplay = false, PreparationComplete = false,
                    StartNewRunAfterPreparation = true,
                    Message = "Requesting a new revision for selected acquisition gaps; other pinned inputs and earlier run evidence remain preserved." };
                await Save(runtime); Enqueue(id); return null;
            case ReplayCommand.CancelPreparation or ReplayCommand.Cancel:
                if (command is ReplayCommand.CancelPreparation && runtime.Acquiring && runtime.PriceOnly && runtime.Session?.CurrentTimeUtc is not null)
                { runtime.Cancellation.Cancel(); return null; }
                WakePlayback(runtime);
                runtime.Cancellation.Cancel(); runtime.Session?.Pause();
                runtime.Job = runtime.Job with { State = ReplayJobState.Cancelled, Message = "Canceled. Completed cached files and finished reports remain available." };
                await Save(runtime); return null;
            case ReplayCommand.Pause:
                WakePlayback(runtime);
                runtime.Session?.Pause(); runtime.Job = runtime.Job with { State = runtime.Job.CanReplay ? ReplayJobState.Paused : runtime.Job.State, Message = "Replay paused at the current simulated timestamp." };
                await Save(runtime); return null;
            case ReplayCommand.PauseEntries or ReplayCommand.EnableEntries:
                SetPaperEntries(runtime, command is ReplayCommand.PauseEntries); await Save(runtime); return null;
            case ReplayCommand.Publish:
                runtime.Job = runtime.Job with { State = ReplayJobState.Reporting }; runtime.Publishing = true;
                await Save(runtime); work.Begin(); runtime.Playback = Task.Run(() => Publish(runtime)); return null;
            case ReplayCommand.Restart:
                runtime.Session!.Pause(); await ResetSession(runtime); runtime.Job = runtime.Job with { State = ReplayJobState.Paused, ReportsAvailable = false };
                await Save(runtime); return null;
        }
        await Play(runtime, command);
        return null;
    }

    // Step, Play, Run and RunAllPresets: open the playback scope, then advance once or start the loop.
    private async Task Play(ReplayJobRuntime runtime, ReplayCommand command)
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

    // A policy that fails its own validation is a rejection, never a thrown exception.
    private static EngineConfiguration? TryApplyProfitExit(EngineConfiguration baseline, ProfitExitConfiguration policy, out string error)
    {
        var errors = policy.Validate();
        error = string.Join(" ", errors);
        if (errors.Count != 0) return null;
        return EngineConfiguration.FromJson((baseline with
        {
            Version = $"{baseline.Version}|{policy.Version}:{policy.Mode}",
            Strategies = baseline.Strategies.Select(profile => profile with
                { Position = profile.Position with { ProfitExit = policy } }).ToArray()
        }).ToJson());
    }

    // A stored override was validated when it was selected; a job file that now fails validation is corrupt.
    private static EngineConfiguration ApplyStoredProfitExit(EngineConfiguration baseline, ProfitExitConfiguration? policy) =>
        policy is null ? baseline
            : TryApplyProfitExit(baseline, policy, out var error) ?? throw new InvalidDataException("The saved profit-exit policy is invalid: " + error);

    // Called under the job gate. Selection is run configuration, never a mutation of
    // an existing position's frozen strategy or of acquisition/source evidence.
    private async Task<string?> SetProfitExit(ReplayJobRuntime runtime, ProfitExitConfiguration policy)
    {
        if (acquisition.FrozenFor(runtime.Job.Id, runtime.Job.Date) is not { } frozen)
            return "Recover the frozen acquisition before selecting a profit policy.";
        if (runtime.Engine is not { } effective) return "Recover effective Engine settings before selecting a profit policy.";
        // Preserve session qualification and all other effective non-profit settings.
        // Only take the original version label from acquisition, avoiding suffix chains.
        var baseline = effective with { Version = EngineConfiguration.FromJson(frozen.EngineJson).Version };
        if (TryApplyProfitExit(baseline, policy, out var invalid) is not { } engine) return invalid;
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
            return null;
        }
        catch (Exception error) when (StorageFailure.Is(error))
        {
            runtime.Session?.Pause(); runtime.Qualification = null;
            runtime.Job = runtime.Job with { State = ReplayJobState.Failed, CanReplay = false,
                Message = "Profit-policy variant could not commit. Restore storage and retry preparation before playback.",
                Warnings = runtime.Job.Warnings.Append("PROFIT_EXIT_POLICY_COMMIT_FAILED").Distinct().ToArray() };
            PublishSnapshot(runtime);
            throw;
        }
    }

    private void SetPaperEntries(ReplayJobRuntime runtime, bool paused)
    {
        try
        {
            runtime.Lifecycle!.SetPaperEntriesPaused(paused);
            runtime.Job = runtime.Job with { Message = paused ? "Paper entries paused. Existing positions remain monitored as replay advances." : "Paper entries enabled; all strategy and evidence gates still apply." };
        }
        catch (Exception error) when (StorageFailure.Is(error))
        {
            runtime.Job = runtime.Job with { State = ReplayJobState.Paused, Message = "Entry-control commit failed. Playback is paused and the previous entry setting was restored; retry the control after restoring storage." };
            WakePlayback(runtime);
            PublishSnapshot(runtime); throw;
        }
    }

    private static void CacheDecisionRows(ReplayJobRuntime runtime, ReplayStep step)
    {
        foreach (var decision in step.Decisions)
        {
            runtime.Decisions.Enqueue(decision);
            while (runtime.Decisions.Count > 200) runtime.Decisions.Dequeue();
        }
    }
}
