using Trading.Data;
using Trading.Replay;

namespace Trading.Api;

public sealed partial class HistoricalReplayService
{
    public Task<HistoricalReplayView?> ViewAsync(string id, string? instrumentId = null)
    {
        if (!jobs.TryGetValue(id, out var runtime)) return Task.FromResult<HistoricalReplayView?>(null);
        var view = Volatile.Read(ref runtime.PublishedView) ?? BuildView(runtime);
        if (instrumentId is not null)
        {
            if (!view.Series.TryGetValue(instrumentId, out var series)) return Task.FromResult<HistoricalReplayView?>(null);
            view = view with { Bars = series.Where(bar => view.SimulatedTimeUtc is { } now && bar.AvailableAtUtc <= now && bar.EndUtc <= now).ToArray(), ChartInstrumentId = instrumentId,
                ChartLabel = instrumentId + (view.ChartInstruments?.Any(item => item.Id == instrumentId && item.Role == "CashIndex") == true ? " — index prices (no traded-volume VWAP)" : " — historical candles") };
        }
        return Task.FromResult<HistoricalReplayView?>(view);
    }
    private HistoricalReplayView BuildView(RuntimeJob runtime)
    {
            var time = runtime.Session?.CurrentTimeUtc;
             var data = runtime.Lifecycle?.Inputs?.Dataset ?? runtime.Dataset;
            var future = runtime.PriceOnly ? data?.Manifest.Instruments.FirstOrDefault(item => item.Role == Trading.Domain.HistoricalInstrumentRole.CashIndex)
                : data?.Manifest.Instruments.FirstOrDefault(item => item.Role == Trading.Domain.HistoricalInstrumentRole.Futures);
            future ??= data?.Manifest.Instruments.FirstOrDefault(item => item.Role == Trading.Domain.HistoricalInstrumentRole.Futures)
                ?? data?.Manifest.Instruments.FirstOrDefault(item => item.Role == Trading.Domain.HistoricalInstrumentRole.CashIndex);
            var bars = time is { } asOf && future is not null && runtime.Series.TryGetValue(future.Id, out var values)
                ? values.Where(bar => bar.AvailableAtUtc <= asOf && bar.EndUtc <= asOf).ToArray() : [];
            var latest = runtime.LastStep?.Features.LastOrDefault();
            var engine = runtime.Lifecycle?.Inputs?.Engine ?? runtime.Engine;
            var replay = runtime.Lifecycle?.Inputs?.Replay ?? runtime.Replay;
            var profile = engine?.Strategies.SingleOrDefault(profile => profile.IndexId == "BANKNIFTY");
            var preset = replay?.Presets[runtime.PresetIndex];
            var directional = latest?.DirectionalEvidence;
            var directionalView = directional is null ? null : new ReplayUiDirectionalEvidence(directional.Version,
                directional.Status.ToString(), directional.AsOfUtc, directional.BarEndUtc, directional.EvidenceAvailableAtUtc,
                directional.MeetsDirectionalRules, directional.CloseMinusOpeningHigh, directional.CloseMinusOpeningLow,
                directional.CloseMinusVwap, directional.Return1MinutePercent, directional.Return5MinutePercent,
                directional.ConfirmingCount, directional.OpposingCount, directional.NeutralCount, directional.TopThreeConfirmingWeightPercent,
                profile?.MinimumConfirmingConstituents, directional.Gates.Where(gate => gate.Outcome is Trading.Engine.GateOutcome.FAIL or Trading.Engine.GateOutcome.UNKNOWN)
                    .Select(gate => gate.Code).Distinct(StringComparer.Ordinal).ToArray());
            return new HistoricalReplayView(runtime.Job with { PreparingData = runtime.Acquiring }, time, runtime.Session?.IsPaused ?? true, runtime.Session?.IsComplete ?? false,
                bars, latest is null ? null : new(latest.OpeningHigh, latest.OpeningLow, latest.Vwap, latest.AboveWeight,
                     latest.BelowWeight, latest.MissingWeight, latest.Direction?.ToString(), latest.SetupId, directionalView),
                runtime.Session?.VisibleConstituents("BANKNIFTY") ?? [],
                runtime.Decisions.ToArray(), runtime.Session?.VisibleTrades.Select(trade => new ReplayUiTrade(trade.Id, trade.ContractId,
                    trade.Units, trade.EntryTimeUtc, trade.EntryPrice, trade.ExitKnownAtUtc, trade.ExitPrice, trade.NetPnl, trade.ExitReason, trade.EvidenceClass,
                    trade.EntryFees, trade.ExitFees, trade.GrossPnl, trade.ProfitExit)).ToArray() ?? [],
                runtime.LastStep?.OpenPositions ?? 0, runtime.LastStep?.PendingEntries ?? 0, runtime.LastStep?.NetSessionPnl,
                runtime.PriceOnly || profile is null || preset is null ? null : new(engine!.Version, preset.Id, profile.Risk.GrossCashStop,
                    profile.Risk.MaximumNetRisk, profile.MaximumCandidates, preset.FullSpreadTicks, preset.AdverseSlippageTicks, profile.Position.ProfitExit),
                runtime.Job.ReportsAvailable ? Reports.Where(file => ReportPath(runtime.Job.Id, runtime.Job.Run, file) is not null).ToArray() : [], runtime.PriceOnly ? "PriceOnly" : "Strategy",
                future is null ? "Historical price context" : future.Role == Trading.Domain.HistoricalInstrumentRole.CashIndex
                    ? "Bank Nifty index — historical prices (no traded-volume VWAP)" : future.Id + (future.Verified ? " — historical candles" : " — historical mapping unverified"), future?.Id,
                runtime.ChartChoices, runtime.Session?.PaperState, runtime.JournalRunId,
                runtime.Pacer?.Snapshot(runtime.Session?.IsPaused ?? true, runtime.Batch), runtime.Revision,
                runtime.JournalPending, runtime.CheckpointError ?? runtime.Lifecycle?.ManifestError ?? runtime.DiagnosticError,
                runtime.PriceOnly || preset is null ? null : new(preset.Id, runtime.PresetIndex + 1, replay!.Presets.Count, runtime.AllPresets), serverInstanceId) { Series = runtime.Series };
    }

    public async Task<bool> CommandAsync(string id, HistoricalReplayCommand command)
    {
        if (!jobs.TryGetValue(id, out var runtime)) return false;
        if (!double.IsFinite(command.Speed) || command.Speed <= 0 || command.Speed > 100000)
            throw new ArgumentException("Playback speed must be positive and at most 100000.");
        if (command.Action is "retry" or "publish")
        {
            Task? finishing = null;
            await runtime.Gate.WaitAsync();
            try { if (runtime.Publishing && runtime.Job.State == "Failed") finishing = runtime.Playback; }
            finally { runtime.Gate.Release(); }
            if (finishing is not null)
                try { await finishing; }
                catch (Exception error) when (error is IOException or System.Data.Common.DbException) { /* Retry resumes from the already exposed storage failure. */ }
        }
        await runtime.Gate.WaitAsync();
        try
        {
            runtime.Pacer ??= new(clock);
            Log(runtime, "COMMAND_" + command.Action.ToUpperInvariant(), new { command.Speed });
            if (runtime.DiagnosticError is not null && command.Action is not ("pause" or "cancel" or "cancelPreparation")) throw new IOException("Operational log storage is unavailable.");
            if (runtime.Publishing && command.Action is not ("cancel" or "cancelPreparation"))
                throw new ArgumentException("Wait for report publication before changing replay state.");
            if (runtime.JournalPending && command.Action is not ("step" or "play" or "run" or "pause" or "cancel" or "cancelPreparation"))
                throw new ArgumentException("A paper batch is awaiting durable commit. Retry Step before changing inputs or paper controls.");
            if (command.Action is "cancel" or "pause" && runtime.Job.State == "Completed" && runtime.Job.ReportsAvailable && !runtime.Acquiring)
                return true;
            switch (command.Action)
            {
                case "setProfitExit":
                    await SetProfitExit(runtime, command.ProfitExit); return true;
                case "setSpeed":
                    runtime.Speed = command.Speed; runtime.Pacer.Reanchor(runtime.Session?.CurrentTimeUtc, command.Speed);
                    WakePlayback(runtime); await Save(runtime); return true;
                case "retry":
                    if (runtime.Acquiring)
                    {
                        // Failed state can be visible before worker cleanup finishes. Retain
                        // that retry just like a retry received during cancellation.
                        if (runtime.Cancellation.IsCancellationRequested || runtime.Job.State is "Failed" or "AuthenticationRequired" or "Interrupted")
                        { runtime.RetryAfterAcquisition = true; runtime.Job = runtime.Job with { Message = "Retry will resume once the current acquisition stops." }; await Save(runtime); }
                        return true;
                    }
                    if (runtime.Job.State == "Reporting") throw new ArgumentException("Wait for report publication before retrying.");
                    if (runtime.Job.State is "Queued" or "Downloading" or "Resolving" or "Validating") return true;
                    runtime.Session?.Pause();
                    runtime.Qualification = null;
                    runtime.Job = runtime.Job with { State = "Queued", Message = "Retry queued; valid cached responses will be reused.", CanReplay = false };
                    await Save(runtime); await queue.Writer.WriteAsync(id); return true;
                case "retryFailed":
                case "refreshGaps":
                    if (runtime.Acquiring || runtime.Job.State is "Playing" or "Reporting") throw new ArgumentException("Pause playback and wait for acquisition/reporting before retrying failed requests.");
                    var failed = runtime.AttemptEvidence.RefetchTargets(command.Action == "retryFailed");
                    if (failed.Count == 0) throw new ArgumentException("There are no matching acquisition gaps. Empty responses require Refresh coverage gaps, not Retry failed requests.");
                    runtime.RetryOnlyFailed = failed; runtime.Session?.Pause();
                    runtime.Qualification = null;
                    runtime.Job = runtime.Job with { State = "Queued", CanReplay = false, PreparationComplete = false,
                        StartNewRunAfterPreparation = true,
                        Message = "Requesting a new revision for selected acquisition gaps; other pinned inputs and earlier run evidence remain preserved." };
                    await Save(runtime); await queue.Writer.WriteAsync(id); return true;
                case "cancelPreparation":
                    if (runtime.Acquiring && runtime.PriceOnly && runtime.Session?.CurrentTimeUtc is not null)
                    { runtime.Cancellation.Cancel(); return true; }
                    if (!runtime.Acquiring && runtime.Job.State is not ("Queued" or "Downloading" or "Resolving" or "Validating")) return true;
                    goto case "cancel";
                case "cancel":
                    WakePlayback(runtime);
                    runtime.Cancellation.Cancel(); runtime.Session?.Pause();
                    runtime.Job = runtime.Job with { State = "Cancelled", Message = "Canceled. Completed cached files and finished reports remain available." };
                    await Save(runtime); return true;
                case "pause":
                    WakePlayback(runtime);
                    runtime.Session?.Pause(); runtime.Job = runtime.Job with { State = runtime.Job.CanReplay ? "Paused" : runtime.Job.State, Message = "Replay paused at the current simulated timestamp." };
                    await Save(runtime); return true;
                case "pauseEntries":
                case "enableEntries":
                    SetPaperEntries(runtime, command.Action == "pauseEntries"); await Save(runtime); return true;
            }
            if (runtime.Job.State == "Reporting") throw new ArgumentException("Wait for this report to finish publishing.");
            if (!runtime.Job.CanReplay || runtime.Session is null) throw new ArgumentException("Dataset is not ready for replay.");
            if (command.Action == "publish")
            {
                if (runtime.Lifecycle?.CanPublish != true) throw new ArgumentException("Commit and finish the requested replay scope before publishing reports.");
                runtime.Job = runtime.Job with { State = "Reporting" }; runtime.Publishing = true;
                await Save(runtime); runtime.Playback = Task.Run(() => Publish(runtime)); return true;
            }
            if (command.Action == "restart")
            {
                runtime.Session.Pause(); await ResetSession(runtime); runtime.Job = runtime.Job with { State = "Paused", ReportsAvailable = false };
                await Save(runtime); return true;
            }
            if (command.Action is not ("step" or "play" or "run" or "runAllPresets")) throw new ArgumentException("Unknown replay command.");
            var allPresets = command.Action == "runAllPresets";
            if (allPresets && (runtime.PriceOnly || runtime.Job.State == "Playing"))
                throw new ArgumentException("Pause an engine-backed replay before starting an explicit all-preset comparison.");
            var before = (runtime.Job.Run, runtime.PresetIndex);
            var failedTransition = false;
            try { runtime.Lifecycle!.PreparePlayback(command.Action switch
            { "step" => ReplayPlaybackRequest.Step, "play" => ReplayPlaybackRequest.Resume, "run" => ReplayPlaybackRequest.CurrentPreset, _ => ReplayPlaybackRequest.AllPresets }); }
            catch { failedTransition = true; runtime.Job = runtime.Job with { State = "Paused" }; throw; }
            finally { SyncRunPresentation(runtime, before); if (failedTransition) PublishSnapshot(runtime); }
            if (command.Action == "step")
            {
                runtime.Session.Pause(); Advance(runtime);
                var finished = runtime.Session.IsComplete && !CompletePreset(runtime);
                runtime.Job = runtime.Job with { State = finished ? "Reporting" : "Paused" };
                await Save(runtime);
                if (finished) { runtime.Publishing = true; runtime.Playback = Publish(runtime); }
            }
            else
            {
                runtime.Speed = command.Speed;
                runtime.Batch = command.Action is "run" or "runAllPresets"; runtime.Session.Resume();
                runtime.Pacer.Reanchor(runtime.Session.CurrentTimeUtc, runtime.Speed); WakePlayback(runtime);
                runtime.Job = runtime.Job with { State = "Playing", ReportsAvailable = false,
                    Message = runtime.AllPresets ? $"Explicit preset comparison: {runtime.PresetIndex + 1}/{runtime.Replay!.Presets.Count} ({runtime.Replay.Presets[runtime.PresetIndex].Id}). The clock restarts for each preset."
                        : runtime.Batch ? $"Running the current preset ({runtime.Replay!.Presets[runtime.PresetIndex].Id}) to the end once at maximum throughput."
                        : $"Paced replay is running at requested {runtime.Speed}×; achieved speed is measured separately." };
                await Save(runtime);
                if (!runtime.PlayLoopActive)
                {
                    runtime.PlayLoopActive = true; var loop = ++runtime.PlayLoopId;
                    runtime.Playback = Task.Run(() => Play(runtime, loop));
                }
            }
            return true;
        }
        finally { runtime.Gate.Release(); }
    }

    private async Task ResetSession(RuntimeJob runtime, bool recoverCurrent = false)
    {
        var previousRun = runtime.Job.Run;
        runtime.Decisions.Clear(); runtime.Batch = false;
        if (runtime.EnrichedDataset is { } enriched)
        {
            runtime.Dataset = enriched; runtime.EnrichedDataset = null;
            runtime.Replay = runtime.EnrichedReplay ?? runtime.Replay; runtime.Engine = runtime.EnrichedEngine ?? runtime.Engine;
            runtime.EnrichedReplay = null; runtime.EnrichedEngine = null;
        }
        runtime.Lifecycle ??= new(runtime.Job.Id, runtime.Job.Date, paperJournal, clock);
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
            runtime.Job = runtime.Job with { State = "Paused", ReportsAvailable = false };
            runtime.RecoverSavedRun = true;
            try { await Save(runtime); }
            catch (Exception error) when (error is IOException or System.Data.Common.DbException) { /* The staged journal opening remains recoverable if the host checkpoint also fails. */ }
            throw;
        }
        finally
        {
            SyncRun(runtime);
            var data = runtime.Lifecycle.Inputs?.Dataset ?? runtime.Dataset!;
            runtime.Series = data.Candles.GroupBy(bar => bar.InstrumentId).ToDictionary(group => group.Key,
            group => group.Select(bar => new ReplayUiBar(bar.Id, bar.StartUtc, bar.EndUtc, bar.AvailableAtUtc, bar.Open, bar.High, bar.Low, bar.Close)).ToArray(), StringComparer.Ordinal);
            runtime.ChartChoices = data.Manifest.Instruments.Where(item => runtime.Series.ContainsKey(item.Id))
            .Select(item => new ReplayChartInstrument(item.Id, item.Role.ToString())).ToArray();
            runtime.Pacer ??= new(clock); runtime.Pacer.Reanchor(runtime.Session?.CurrentTimeUtc, runtime.Speed); WakePlayback(runtime);
            runtime.Job = runtime.Job with { State = "Paused", ReportsAvailable = false };
            PublishSnapshot(runtime);
        }
        Log(runtime, "RUN_OPENED", new { runtime.JournalRunId, runtime.Job.Run });
    }
    private static void SyncRun(RuntimeJob runtime)
    {
        if (runtime.Lifecycle?.HasSession != true) return;
        var lifecycle = runtime.Lifecycle;
        var changed = runtime.Job.Run != lifecycle.Number;
        runtime.Job = runtime.Job with { Run = lifecycle.Number, ActivePresetId = lifecycle.Inputs!.Replay.Presets[lifecycle.PresetIndex].Id,
            AllPresets = lifecycle.AllPresets,
            StartNewRunAfterPreparation = runtime.Job.StartNewRunAfterPreparation && !changed,
            Mode = lifecycle.Inputs.Dataset.Manifest.IsSynthetic ? "SYNTHETIC TEST" : lifecycle.PriceOnly ? "PAPER + REPLAY — PRICE ONLY" : "PAPER + REPLAY — PAPER TRADER (CANDLE MODEL)" };
    }
    private void SyncRunPresentation(RuntimeJob runtime, (int Run, int Preset) previous)
    {
        SyncRun(runtime);
        if (previous != (runtime.Job.Run, runtime.PresetIndex))
        {
            runtime.Job = runtime.Job with { ReportsAvailable = false };
            runtime.Decisions.Clear(); runtime.Pacer?.Reanchor(runtime.Session?.CurrentTimeUtc, runtime.Speed); WakePlayback(runtime);
        }
        if (runtime.Lifecycle?.ManifestPending == true)
        { runtime.Job = runtime.Job with { State = "Paused", ReportsAvailable = false }; PublishSnapshot(runtime); }
    }
    private bool CompletePreset(RuntimeJob runtime)
    {
        var previous = (runtime.Job.Run, runtime.PresetIndex);
        try { return runtime.Lifecycle!.CompletePreset(); }
        finally { SyncRunPresentation(runtime, previous); }
    }
    private void Advance(RuntimeJob runtime)
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
            runtime.Session!.Pause(); runtime.Job = runtime.Job with { State = "Paused", Message = "Paper journal commit failed. Replay is stopped; retry Step to commit the same batch before advancing." };
            PublishSnapshot(runtime);
            Log(runtime, "JOURNAL_COMMIT_FAILED", new { step = runtime.StepNumber }, "Error");
            throw;
        }
    }
    private async Task Play(RuntimeJob runtime, int loop)
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
                    if (runtime.Job.State != "Playing" || runtime.Session!.IsPaused) { runtime.PlayLoopActive = false; return; }
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
                             runtime.Job = runtime.Job with { State = "Playing", Message = $"Explicit preset comparison: {runtime.PresetIndex + 1}/{runtime.Replay!.Presets.Count} ({runtime.Replay.Presets[runtime.PresetIndex].Id})." };
                            Log(runtime, "PRESET_STARTED", new { runtime.JournalRunId });
                        }
                        else { runtime.Job = runtime.Job with { State = "Reporting" }; runtime.Publishing = true; runtime.PlayLoopActive = false; publish = true; }
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
                if (publish) { await Publish(runtime); return; }
                await Task.Delay(TimeSpan.FromMilliseconds(1), clock, stopping);
            }
        }
        catch (OperationCanceledException) { await Change(runtime, job => job with { State = "Interrupted", Message = "Server stopped. Restart replay from the cached day." }); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.Data.Common.DbException)
        { Log(runtime, "PLAYBACK_FAILED", new { exceptionType = error.GetType().Name, error.StackTrace }, "Error"); await Change(runtime, job => job with { State = "Failed", Message = "Replay processing failed. See run diagnostics; cached source data is preserved." }); }
        finally
        {
            await runtime.Gate.WaitAsync();
            try { if (runtime.PlayLoopId == loop) runtime.PlayLoopActive = false; }
            finally { runtime.Gate.Release(); }
        }
    }
    private static void WakePlayback(RuntimeJob runtime)
    { var previous = runtime.Wake; runtime.Wake = new(); previous.Cancel(); previous.Dispose(); }
    private async Task Publish(RuntimeJob runtime)
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
                await Change(runtime, job => job with { State = "Completed", ReportsAvailable = true, Message = "Recovered immutable published reports." }); return;
            }
            var staging = output + ".publishing-" + Guid.NewGuid().ToString("N");
            Log(runtime, "REPORT_STARTED", new { run });
            await Task.Run(() => ReplayReportWriter.Write(staging, result, runtime.PriceOnly ? null
                : session => DurableReplaySession.ReadDecisions(paperJournal, ReplayEvidenceRun.IdFor(runtime.Job.Id, run, session.PresetId))));
            Log(runtime, "REPORT_COMPLETED", new { run, sessions = result.Sessions.Count });
            await using (var logs = File.Create(Path.Combine(staging, "runtime.jsonl.gz")))
                await paperJournal.ExportOperations(logs, runtime.Job.Id, CancellationToken.None, run);
            await HistoricalAcquisitionStore.AtomicJsonAsync(Path.Combine(staging, "complete.json"), new { Complete = true }, CancellationToken.None);
            if (Directory.Exists(output)) Directory.Move(output, output + ".incomplete-" + Guid.NewGuid().ToString("N"));
            Directory.Move(staging, output);
            await Change(runtime, job => job.Run != run ? job : job with { State = job.State == "Cancelled" ? "Cancelled" : "Completed", ReportsAvailable = true,
                Message = $"Replay completed. {result.Sessions.Count(session => !session.Complete)} incomplete experiments are disclosed in the report." });
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.Data.Common.DbException)
        { Log(runtime, "REPORT_FAILED", new { exceptionType = error.GetType().Name, error.StackTrace }, "Error"); await Change(runtime, job => job with { State = "Failed", Message = "Report publication failed. Journal evidence remains downloadable; see run diagnostics." }); }
        finally
        {
            await runtime.Gate.WaitAsync();
            try { runtime.Publishing = false; }
            finally { runtime.Gate.Release(); }
        }
    }
    public string? ReportPath(string id, int run, string file)
    {
        if (!jobs.ContainsKey(id) || run < 1 || !Reports.Contains(file, StringComparer.Ordinal)) return null;
        var root = Path.Combine(store.JobsRoot, id, "reports", "run-" + run);
        return File.Exists(Path.Combine(root, "complete.json")) && File.Exists(Path.Combine(root, file)) ? Path.Combine(root, file) : null;
    }
}
