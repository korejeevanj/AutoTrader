using Trading.Data;

namespace Trading.Replay;

public sealed partial class ReplayJobs
{
    private async Task ResetSession(ReplayJobRuntime runtime, bool recoverCurrent = false)
    {
        var previousRun = runtime.Job.Run;
        runtime.Decisions.Clear(); runtime.Batch = false;
        if (runtime.EnrichedDataset is { } enriched)
        {
            runtime.Dataset = enriched; runtime.EnrichedDataset = null;
            runtime.Replay = runtime.EnrichedReplay ?? runtime.Replay; runtime.Engine = runtime.EnrichedEngine ?? runtime.Engine;
            runtime.EnrichedReplay = null; runtime.EnrichedEngine = null;
        }
        runtime.Lifecycle ??= new(runtime.Job.Id, runtime.Job.Date, journal, clock);
        try
        {
            runtime.Lifecycle.Open(new(runtime.Dataset!, runtime.Replay!, runtime.Engine!,
                ReplayRunIdentity.InputHash(runtime.Dataset!, runtime.Replay!, runtime.Engine!), ReplayRunIdentity.CodeVersions, runtime.Job.Warnings), previousRun,
                runtime.Job.StartNewRunAfterPreparation || runtime.RecoverSavedRun || recoverCurrent && runtime.Lifecycle.HasSession
                    ? new(previousRun, runtime.Job.ActivePresetId, runtime.Job.AllPresets, runtime.Job.StartNewRunAfterPreparation) : null,
                step => CacheDecisionRows(runtime, step));
            runtime.RecoverSavedRun = false;
        }
        catch
        {
            runtime.Job = runtime.Job with { State = ReplayJobState.Paused, ReportsAvailable = false };
            runtime.RecoverSavedRun = true;
            try { await Save(runtime); }
            catch (Exception error) when (StorageFailure.Is(error)) { /* The staged journal opening remains recoverable if the host checkpoint also fails. */ }
            throw;
        }
        finally
        {
            SyncRun(runtime);
            runtime.Pacer ??= new(clock); runtime.Pacer.Reanchor(runtime.Session?.CurrentTimeUtc, runtime.Speed); WakePlayback(runtime);
            runtime.Job = runtime.Job with { State = ReplayJobState.Paused, ReportsAvailable = false };
            PublishSnapshot(runtime);
        }
        Log(runtime, "RUN_OPENED", new { runtime.JournalRunId, runtime.Job.Run });
    }

    private static void SyncRun(ReplayJobRuntime runtime)
    {
        if (runtime.Lifecycle?.HasSession != true) return;
        var lifecycle = runtime.Lifecycle;
        var changed = runtime.Job.Run != lifecycle.Number;
        runtime.Job = runtime.Job with { Run = lifecycle.Number, ActivePresetId = lifecycle.Inputs!.Replay.Presets[lifecycle.PresetIndex].Id,
            AllPresets = lifecycle.AllPresets,
            StartNewRunAfterPreparation = runtime.Job.StartNewRunAfterPreparation && !changed,
            Mode = lifecycle.Inputs.Dataset.Manifest.IsSynthetic ? "SYNTHETIC TEST" : lifecycle.PriceOnly ? "PAPER + REPLAY — PRICE ONLY" : "PAPER + REPLAY — PAPER TRADER (CANDLE MODEL)" };
    }

    private void SyncRunPresentation(ReplayJobRuntime runtime, (int Run, int Preset) previous)
    {
        SyncRun(runtime);
        if (previous != (runtime.Job.Run, runtime.PresetIndex))
        {
            runtime.Job = runtime.Job with { ReportsAvailable = false };
            runtime.Decisions.Clear(); runtime.Pacer?.Reanchor(runtime.Session?.CurrentTimeUtc, runtime.Speed); WakePlayback(runtime);
        }
        if (runtime.Lifecycle?.ManifestPending == true)
        { runtime.Job = runtime.Job with { State = ReplayJobState.Paused, ReportsAvailable = false }; PublishSnapshot(runtime); }
    }

    private bool CompletePreset(ReplayJobRuntime runtime)
    {
        var previous = (runtime.Job.Run, runtime.PresetIndex);
        try { return runtime.Lifecycle!.CompletePreset(); }
        finally { SyncRunPresentation(runtime, previous); }
    }

    private void Advance(ReplayJobRuntime runtime)
    {
        var started = clock.GetTimestamp();
        try
        {
            var step = runtime.Session!.Step(); if (step is null) return;
            CacheDecisionRows(runtime, step);
            runtime.Pacer?.Observe(runtime.Session!.CurrentTimeUtc!.Value, clock.GetElapsedTime(started).TotalMilliseconds);
            Log(runtime, "STEP_COMMITTED", new { step = runtime.StepNumber, runtime.LastStep!.Decisions.Count, runtime.LastStep.OpenPositions, runtime.LastStep.PendingEntries,
                timing = runtime.Pacer?.Snapshot(runtime.Session!.IsPaused, runtime.Batch) });
        }
        catch
        {
            runtime.Session!.Pause(); runtime.Job = runtime.Job with { State = ReplayJobState.Paused, Message = "Paper journal commit failed. Replay is stopped; retry Step to commit the same batch before advancing." };
            PublishSnapshot(runtime);
            Log(runtime, "JOURNAL_COMMIT_FAILED", new { step = runtime.StepNumber }, "Error");
            throw;
        }
    }

    private async Task PlayLoop(ReplayJobRuntime runtime, int loop)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                TimeSpan delay = TimeSpan.Zero;
                var publish = false;
                CancellationToken wake = default;
                await runtime.Gate.WaitAsync(stopping);
                try
                {
                    if (runtime.PlayLoopId != loop) return;
                    if (runtime.Job.State != ReplayJobState.Playing || runtime.Session!.IsPaused) { runtime.PlayLoopActive = false; return; }
                    wake = runtime.Wake.Token;
                    if (!runtime.Batch && !runtime.JournalPending && runtime.Session.NextTimeUtc is { } next)
                        delay = runtime.Pacer!.DelayUntil(next);
                    if (delay > TimeSpan.Zero) continue;
                    Advance(runtime);
                    if (runtime.DiagnosticError is not null) { await Save(runtime); continue; }
                    if (runtime.Session.IsComplete)
                    {
                        if (CompletePreset(runtime))
                        {
                            runtime.Session.Resume();
                            runtime.Job = runtime.Job with { State = ReplayJobState.Playing, Message = $"Explicit preset comparison: {runtime.PresetIndex + 1}/{runtime.Replay!.Presets.Count} ({runtime.Replay.Presets[runtime.PresetIndex].Id})." };
                            Log(runtime, "PRESET_STARTED", new { runtime.JournalRunId });
                        }
                        else { runtime.Job = runtime.Job with { State = ReplayJobState.Reporting }; runtime.Publishing = true; runtime.PlayLoopActive = false; publish = true; }
                    }
                    await Save(runtime);
                }
                finally
                {
                    runtime.Gate.Release();
                    if (delay > TimeSpan.Zero)
                    {
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stopping, wake);
                        try { await Task.Delay(delay, clock, wait.Token); }
                        catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { }
                    }
                }
                if (publish) { work.Begin(); await Publish(runtime); return; }
                await Task.Delay(TimeSpan.FromMilliseconds(1), clock, stopping);
            }
        }
        catch (OperationCanceledException) { await Change(runtime, job => job with { State = ReplayJobState.Interrupted, Message = "Server stopped. Restart replay from the cached day." }); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.Data.Common.DbException)
        { Log(runtime, "PLAYBACK_FAILED", new { exceptionType = error.GetType().Name, error.StackTrace }, "Error"); await Change(runtime, job => job with { State = ReplayJobState.Failed, Message = "Replay processing failed. See run diagnostics; cached source data is preserved." }); }
        finally
        {
            await runtime.Gate.WaitAsync();
            try { if (runtime.PlayLoopId == loop) runtime.PlayLoopActive = false; }
            finally { runtime.Gate.Release(); work.End(); }
        }
    }

    private static void WakePlayback(ReplayJobRuntime runtime)
    { var previous = runtime.Wake; runtime.Wake = new(); previous.Cancel(); previous.Dispose(); }

    // The caller counted this work before starting it, so WhenIdleAsync cannot report quiescence early.
    private async Task Publish(ReplayJobRuntime runtime)
    {
        try
        {
            ReplayRunResult result; int run;
            await runtime.Gate.WaitAsync();
            try
            {
                result = runtime.Lifecycle!.CaptureResults(); run = runtime.Lifecycle.Number;
            }
            finally { runtime.Gate.Release(); }
            var output = Path.Combine(store.JobsRoot, runtime.Job.Id, "reports", "run-" + run);
            if (File.Exists(Path.Combine(output, "complete.json")))
            {
                await Change(runtime, job => job with { State = ReplayJobState.Completed, ReportsAvailable = true, Message = "Recovered immutable published reports." }); return;
            }
            var staging = output + ".publishing-" + Guid.NewGuid().ToString("N");
            Log(runtime, "REPORT_STARTED", new { run });
            await Task.Run(() => ReplayReportWriter.Write(staging, result, runtime.PriceOnly ? null
                : session => DurableReplaySession.ReadDecisions(journal, ReplayEvidenceRun.IdFor(runtime.Job.Id, run, session.PresetId))));
            Log(runtime, "REPORT_COMPLETED", new { run, sessions = result.Sessions.Count });
            await using (var logs = File.Create(Path.Combine(staging, "runtime.jsonl.gz")))
                await operationLog.Export(logs, runtime.Job.Id, CancellationToken.None, run);
            await HistoricalAcquisitionStore.AtomicJsonAsync(Path.Combine(staging, "complete.json"), new { Complete = true }, CancellationToken.None);
            if (Directory.Exists(output)) Directory.Move(output, output + ".incomplete-" + Guid.NewGuid().ToString("N"));
            Directory.Move(staging, output);
            await Change(runtime, job => job.Run != run ? job : job with { State = job.State == ReplayJobState.Cancelled ? ReplayJobState.Cancelled : ReplayJobState.Completed, ReportsAvailable = true,
                Message = $"Replay completed. {result.Sessions.Count(session => !session.Complete)} incomplete experiments are disclosed in the report." });
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.Data.Common.DbException)
        { Log(runtime, "REPORT_FAILED", new { exceptionType = error.GetType().Name, error.StackTrace }, "Error"); await Change(runtime, job => job with { State = ReplayJobState.Failed, Message = "Report publication failed. Journal evidence remains downloadable; see run diagnostics." }); }
        finally
        {
            await runtime.Gate.WaitAsync();
            try { runtime.Publishing = false; }
            finally { runtime.Gate.Release(); work.End(); }
        }
    }
}
