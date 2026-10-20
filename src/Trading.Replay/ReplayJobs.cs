using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Trading.Data;
using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

public sealed record ReplayJobsOptions
{
    /// <summary>Archive the provider's current instrument dump after a preparation and each morning.</summary>
    public bool ArchiveCurrentMaster { get; init; } = true;
}

/// <summary>
/// Owns every historical replay job: the registry, the single acquisition queue and worker, per-job
/// serialization, the play loop and pacing, report publication and persistence of each transition.
/// Hosts prepare days, send typed commands and read immutable snapshots; they never touch a job's
/// mutable state or call Save. RunAsync hosts the worker; WhenIdleAsync lets a caller wait for
/// quiescence without polling.
/// </summary>
public sealed partial class ReplayJobs
{
    private readonly HistoricalAcquisitionStore store;
    private readonly HistoricalAcquisition acquisition;
    private readonly IHistoricalSources sources;
    private readonly ReplayEvidenceJournal journal;
    private readonly ReplayOperationLog operationLog;
    private readonly TimeProvider clock;
    private readonly ReplayJobsOptions options;
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<string, ReplayJobRuntime> jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim createGate = new(1, 1);
    private readonly WorkTracker work = new();
    private CancellationToken stopping;
    private bool replayRequested;
    private static readonly string[] Reports = ["summary.md", "trades.csv", "features.csv", "decisions.csv", "directional-evidence.csv", "directional-outcomes.csv", "directional-outcomes.json", "quality.json", "results.json", "run-manifest.json", "normalized-candles.json", "runtime.jsonl.gz"];

    /// <summary>Raised after every published snapshot, under the job's gate; handlers must not block.</summary>
    public event Action<ReplayJobSnapshot>? Changed;

    public ReplayJobs(HistoricalAcquisitionStore store, HistoricalAcquisition acquisition, IHistoricalSources sources,
        ReplayEvidenceJournal journal, ReplayOperationLog operationLog, TimeProvider clock, ReplayJobsOptions options)
    {
        this.store = store; this.acquisition = acquisition; this.sources = sources; this.journal = journal;
        this.operationLog = operationLog; this.clock = clock; this.options = options;
        if (Directory.Exists(store.JobsRoot))
            foreach (var file in Directory.EnumerateFiles(store.JobsRoot, "job.json", SearchOption.AllDirectories))
            {
                ReplayJob? job;
                try { job = HistoricalAcquisitionStore.Read<ReplayJob>(file); }
                catch (Exception error) when (error is IOException or JsonException or InvalidDataException) { continue; }
                if (job is null || !Guid.TryParseExact(job.Id, "N", out _)) continue;
                jobs[job.Id] = new ReplayJobRuntime(job with { CanReplay = false,
                    State = job.ReportsAvailable ? ReplayJobState.Completed : ReplayJobState.Interrupted,
                    Message = "Server restarted. Retry preparation to recover the paused paper replay from pinned inputs." }) { RecoverSavedRun = true };
            }
    }

    public bool Exists(string id) => jobs.ContainsKey(id);

    /// <summary>The latest session that has finished by the provider's clock.</summary>
    public DateOnly LatestCompletedDate()
    {
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var latest = DateOnly.FromDateTime(local.DateTime);
        var calendar = store.Calendar(latest.Year);
        var close = calendar is { Verified: true } ? calendar.SpecialSessions.SingleOrDefault(session => session.Date == latest)?.Close : null;
        if (TimeOnly.FromDateTime(local.DateTime) < (close ?? new TimeOnly(15, 30))) latest = latest.AddDays(-1);
        return latest;
    }

    /// <summary>The newest published state of a job; never blocks and never exposes mutable fields.</summary>
    public ReplayJobSnapshot? Snapshot(string id) => jobs.TryGetValue(id, out var runtime) ? Volatile.Read(ref runtime.Published) ?? BuildSnapshot(runtime) : null;

    /// <summary>Completes when no job is queued, acquiring, playing or publishing.</summary>
    public Task WhenIdleAsync(CancellationToken token = default) => work.WhenIdle().WaitAsync(token);

    /// <exception cref="ArgumentException">The date is not a completed historical session.</exception>
    public async Task<ReplayJobSnapshot> PrepareAsync(DateOnly date, bool refresh = false)
    {
        var calendar = acquisition.Session(date);
        var fingerprint = acquisition.RequestFingerprint(date);
        await createGate.WaitAsync();
        try
        {
            if (!refresh)
            {
                var existing = jobs.Values.Select(runtime => runtime.Job).Where(job => job.Date == date && job.RequestFingerprint == fingerprint
                    && job.State is not (ReplayJobState.Failed or ReplayJobState.Cancelled or ReplayJobState.Interrupted
                        or ReplayJobState.AuthenticationRequired or ReplayJobState.NeedsMetadata or ReplayJobState.NoData))
                    .OrderByDescending(job => job.UpdatedAtUtc).FirstOrDefault();
                if (existing is not null)
                {
                    // A persisted completed job has reports but no in-memory session after
                    // process restart. Date-only preparation must rehydrate it automatically.
                    if ((!existing.CanReplay || !existing.PreparationComplete && !jobs[existing.Id].Acquiring) && !existing.State.IsPreparing()
                        && existing.State != ReplayJobState.MarketClosed)
                        await SendAsync(existing.Id, new ReplayCommand.Retry());
                    return Snapshot(existing.Id)!;
                }
            }
            var id = Guid.NewGuid().ToString("N");
            var closed = calendar.Status == "MarketClosed";
            var job = new ReplayJob(id, date, refresh, fingerprint, closed ? ReplayJobState.MarketClosed : ReplayJobState.Queued,
                calendar.Message, calendar.Status, null, [], [], 0, 0, false, "PAPER + REPLAY — CANDLE RESEARCH", clock.GetUtcNow());
            var runtime = new ReplayJobRuntime(job);
            jobs[id] = runtime;
            await Save(runtime);
            replayRequested = true;
            if (!closed) Enqueue(id);
            return Snapshot(id)!;
        }
        finally { createGate.Release(); }
    }

    private void Enqueue(string id) { work.Begin(); queue.Writer.TryWrite(id); }

    /// <summary>Runs the acquisition worker and the daily reference-data archive until stopped.</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        stopping = stoppingToken;
        if (journal.FileExists) operationLog.Begin();
        await Task.WhenAll(ProcessQueue(stoppingToken), ArchiveDaily(stoppingToken));
    }

    /// <summary>Waits for running playback and publication to finish after the host stopped.</summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        var playback = jobs.Values.Select(runtime => runtime.Playback).Where(task => task is not null).Select(task => task!).ToArray();
        if (playback.Length != 0) await Task.WhenAll(playback).WaitAsync(cancellationToken);
    }

    private async Task ProcessQueue(CancellationToken token)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(token))
        {
            try
            {
                var runtime = jobs[id];
                await runtime.Gate.WaitAsync(token);
                try
                {
                    if (runtime.Job.State != ReplayJobState.Queued) continue;
                    runtime.Cancellation.Dispose(); runtime.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    runtime.Acquiring = true; runtime.Qualification = null; runtime.Job = runtime.Job with { State = ReplayJobState.Resolving };
                }
                finally { runtime.Gate.Release(); }
                try
                {
                    await Change(runtime, job => job with { Message = "Initializing durable replay storage and indexes; preparation will continue automatically." });
                    await operationLog.WhenReady(runtime.Cancellation.Token);
                    await Acquire(runtime, runtime.Cancellation.Token);
                }
                catch (Exception error) { await AcquisitionFailed(runtime, error, token.IsCancellationRequested); }
                finally
                {
                    await runtime.Gate.WaitAsync();
                    try
                    {
                        runtime.Acquiring = false;
                        Log(runtime, "ACQUISITION_STOPPED", new { runtime.Job.Attempt, runtime.Job.State, runtime.Job.Finished, runtime.Job.Total }, acquisition: true);
                        PublishSnapshot(runtime);
                        if (runtime.RetryAfterAcquisition && !token.IsCancellationRequested)
                        {
                            runtime.RetryAfterAcquisition = false;
                            runtime.Job = runtime.Job with { State = ReplayJobState.Queued, Message = "Resuming preparation from pinned cached inputs." };
                            try { await Save(runtime); Enqueue(id); }
                            catch (HistoricalStorageException) { /* Failed checkpoint is already exposed; worker remains available. */ }
                        }
                    }
                    finally { runtime.Gate.Release(); }
                }
            }
            finally { work.End(); }
        }
    }

    private async Task AcquisitionFailed(ReplayJobRuntime runtime, Exception error, bool stopping)
    {
        var code = error is HistoricalStorageException ? "LOCAL_STORAGE_UNAVAILABLE"
            : error is KiteDataException providerError ? providerError.Code : error is OperationCanceledException ? "ACQUISITION_CANCELLED" : "ACQUISITION_FAILED";
        Log(runtime, code, new { exceptionType = error.GetType().Name, innerType = error.InnerException?.GetType().Name, error.StackTrace }, "Error", acquisition: true);
        await runtime.Gate.WaitAsync();
        try
        {
            var cancelled = error is OperationCanceledException;
            runtime.Session?.Pause();
            if (!cancelled && runtime.Lifecycle?.ManifestPending == true && runtime.AttemptEvidence.Validated
                && ReferenceEquals(runtime.Lifecycle.Inputs?.Dataset, runtime.Dataset))
            {
                runtime.RecoverSavedRun = true;
                runtime.Job = runtime.Job with { State = ReplayJobState.Paused, CanReplay = true, PreparationComplete = true,
                    ReportsAvailable = false, Message = runtime.Lifecycle.ManifestError! };
                try { await Save(runtime); }
                catch (HistoricalStorageException) { /* A failed host checkpoint remains visibly blocked until storage recovers. */ }
                return;
            }
            var keepPreview = runtime.PriceOnly && runtime.Job.CanReplay && runtime.Job.State != ReplayJobState.Cancelled && error is not HistoricalStorageException;
            runtime.Job = keepPreview ? runtime.Job with { Message = $"Additional acquisition stopped ({code}); cached price playback remains available.",
                    Warnings = runtime.Job.Warnings.Append(code).Distinct().ToArray() }
                : runtime.Job with { State = cancelled ? stopping ? ReplayJobState.Interrupted : ReplayJobState.Cancelled
                    : code is "CREDENTIALS_MISSING" or "AUTHENTICATION_REQUIRED" ? ReplayJobState.AuthenticationRequired : ReplayJobState.Failed,
                Message = cancelled ? "Preparation stopped; completed downloads remain cached."
                    : error is KiteDataException ? code : "Preparation failed. Check run diagnostics; source data remains cached.",
                CanReplay = false, Warnings = runtime.Job.Warnings.Append(code).Distinct().ToArray() };
            if (error is HistoricalStorageException) CheckpointFailed(runtime);
            else try { await Save(runtime); }
                catch (HistoricalStorageException) { /* Save publishes the recoverable failure without another failed write. */ }
        }
        finally { runtime.Gate.Release(); }
    }

    private async Task Acquire(ReplayJobRuntime runtime, CancellationToken token)
    {
        var day = runtime.Job.Date;
        var outcome = await acquisition.PrepareAsync(new(runtime.Job.Id, day, runtime.Job.CalendarStatus, runtime.Job.Attempt,
            runtime.Job.Refresh, runtime.RetryOnlyFailed), new AcquisitionProgress(this, runtime), token);
        if (outcome is not AcquisitionOutcome.Acquired acquired) { await Conclude(runtime, outcome); return; }
        var (calendar, attempt, frozen, plan, manifestPath) = (acquired.Session, acquired.Attempt, acquired.Frozen, acquired.Plan, acquired.ManifestPath);
        var verdict = PreparationVerdict.Evaluate(ReplayConfiguration.FromJson(frozen.ReplayJson),
            ApplyStoredProfitExit(EngineConfiguration.FromJson(frozen.EngineJson), runtime.Job.ProfitExitOverride), plan, manifestPath, day, calendar.Open, calendar.Close);
        var (replay, engine, dataset, qualification) = (verdict.Replay, verdict.Engine, verdict.Dataset, verdict.Qualification);
        runtime.RetryOnlyFailed = null;
        Log(runtime, "VALIDATION_COMPLETED", new { dataErrors = verdict.DataErrors, issues = dataset.Issues.Count, coverage = dataset.Coverage },
            verdict.DataErrors > 0 ? "Warning" : "Information", acquisition: true);
        await runtime.Gate.WaitAsync();
        try
        {
            token.ThrowIfCancellationRequested();
            runtime.AttemptEvidence = runtime.AttemptEvidence.WithDataset(attempt, dataset);
            runtime.Qualification = qualification;
            if (dataset.Candles.Count == 0)
            {
                runtime.Job = runtime.Job with { State = ReplayJobState.NoData, Message = "No selected-session candles were available; this does not establish a market holiday.", CanReplay = false };
                await Save(runtime); return;
            }
            if (runtime.PriceOnly)
            {
                runtime.EnrichedDataset = dataset; runtime.EnrichedReplay = replay; runtime.EnrichedEngine = engine;
                if ((runtime.Session?.CurrentTimeUtc is null || runtime.Session.IsComplete) && runtime.Job.State != ReplayJobState.Playing && !runtime.Publishing) await ResetSession(runtime, recoverCurrent: true);
                // A preview that was just promoted to an engine-backed session is no longer price-only here.
                runtime.Job = runtime.Job with { CanReplay = true, PreparationComplete = true, Warnings = verdict.Issues,
                    State = runtime.PriceOnly ? runtime.Job.State : ReplayJobState.ReadyWithWarnings,
                    Message = runtime.PriceOnly ? "Engine inputs prepared. Restart replay to activate the decision maker and paper simulation; current replay remains frozen."
                        : "Engine-backed paper replay ready. Every decision is evaluated; missing required evidence rejects entries with reasons." };
                await Save(runtime);
                return;
            }
            runtime.Dataset = dataset; runtime.Replay = replay; runtime.Engine = engine;
            runtime.Job = runtime.Job with { Warnings = verdict.Issues };
            await ResetSession(runtime, recoverCurrent: true);
            var completedPlayback = runtime.Session!.IsComplete;
            var publishedReports = completedPlayback && ReportPath(runtime.Job.Id, runtime.Job.Run, "summary.md") is not null;
            runtime.Job = runtime.Job with { State = completedPlayback ? publishedReports ? ReplayJobState.Completed : ReplayJobState.Paused
                    : verdict.Ready ? ReplayJobState.Ready : ReplayJobState.ReadyWithWarnings, CanReplay = true, PreparationComplete = true, ReportsAvailable = publishedReports,
                Warnings = verdict.Issues, Mode = dataset.Manifest.IsSynthetic ? "SYNTHETIC TEST" : "PAPER + REPLAY — CANDLE RESEARCH",
                Message = completedPlayback ? publishedReports ? "Recovered completed playback and its immutable published reports."
                    : "Recovered completed playback. Reports are not published; retry report publication from Run evidence."
                    : qualification.Qualified ? "Historical input evidence qualified. Playback is paused; execution remains a candle model."
                    : "Acquisition processed; decision evidence is incomplete. Diagnostic replay is available; inspect Data health before drawing performance conclusions." };
            await Save(runtime);
        }
        finally { runtime.Gate.Release(); }
        await ArchiveCurrent(token, ignoreFailure: true);
    }

    private async Task ArchiveCurrent(CancellationToken token, bool ignoreFailure)
    {
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        if (!options.ArchiveCurrentMaster || store.HasMaster(DateOnly.FromDateTime(local.DateTime))) return;
        try { await store.ArchiveMasterAsync(await sources.MasterAsync(token), clock.GetUtcNow(), token); }
        catch (Exception error) when (ignoreFailure && error is KiteDataException or IOException or InvalidDataException or JsonException or FormatException) { }
    }

    private async Task ArchiveDaily(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
            var next = HistoricalAcquisitionStore.Midnight(DateOnly.FromDateTime(local.DateTime), "Asia/Kolkata").AddHours(8).AddMinutes(30);
            if (next <= clock.GetUtcNow()) next = next.AddDays(1);
            await Task.Delay(next - clock.GetUtcNow(), clock, token);
            if (replayRequested) await ArchiveCurrent(token, ignoreFailure: true);
        }
    }

    private async Task Change(ReplayJobRuntime runtime, Func<ReplayJob, ReplayJob> update)
    { await runtime.Gate.WaitAsync(); try { runtime.Job = update(runtime.Job); await Save(runtime); } finally { runtime.Gate.Release(); } }

    private async Task Save(ReplayJobRuntime runtime)
    {
        SyncRun(runtime);
        runtime.Job = runtime.Job with { UpdatedAtUtc = clock.GetUtcNow(), AllPresets = runtime.AllPresets };
        if (runtime.Replay is not null) runtime.Job = runtime.Job with { ActivePresetId = runtime.Replay.Presets[runtime.PresetIndex].Id };
        try { await HistoricalAcquisitionStore.AtomicJsonAsync(Path.Combine(store.JobsRoot, runtime.Job.Id, "job.json"), runtime.Job, CancellationToken.None); }
        catch (HistoricalStorageException) { CheckpointFailed(runtime); throw; }
        runtime.Lifecycle?.AcknowledgeCheckpoint();
        if (runtime.Lifecycle?.HasSession == true && !runtime.Lifecycle.ManifestPending) runtime.RecoverSavedRun = false;
        runtime.CheckpointError = null;
        PublishSnapshot(runtime);
    }

    private void CheckpointFailed(ReplayJobRuntime runtime)
    {
        runtime.Session?.Pause(); runtime.Qualification = null;
        runtime.CheckpointError = "Local storage is unavailable. Restore write access and retry preparation; previously cached sources remain preserved.";
        runtime.Job = runtime.Job with { State = ReplayJobState.Failed, CanReplay = false, Message = runtime.CheckpointError,
            Warnings = runtime.Job.Warnings.Append("LOCAL_STORAGE_UNAVAILABLE").Distinct().ToArray() };
        PublishSnapshot(runtime);
    }

    private void PublishSnapshot(ReplayJobRuntime runtime)
    {
        runtime.Revision++;
        var snapshot = BuildSnapshot(runtime);
        Volatile.Write(ref runtime.Published, snapshot);
        try { Changed?.Invoke(snapshot); }
        catch { /* An observer failure must never fail the job that is being checkpointed. */ }
    }

    private ReplayJobSnapshot BuildSnapshot(ReplayJobRuntime runtime) => runtime.Snapshot(
        runtime.Job.ReportsAvailable ? Reports.Where(file => ReportPath(runtime.Job.Id, runtime.Job.Run, file) is not null).ToArray() : []);

    public string? ReportPath(string id, int run, string file)
    {
        if (!jobs.ContainsKey(id) || run < 1 || !Reports.Contains(file, StringComparer.Ordinal)) return null;
        var root = Path.Combine(store.JobsRoot, id, "reports", "run-" + run);
        return File.Exists(Path.Combine(root, "complete.json")) && File.Exists(Path.Combine(root, file)) ? Path.Combine(root, file) : null;
    }

    private void Log(ReplayJobRuntime runtime, string code, object? detail = null, string severity = "Information", bool acquisition = false)
    {
        var json = JsonSerializer.Serialize(detail ?? new { }, KiteCandleImporter.JsonOptions);
        try
        {
            switch (operationLog.Record(new(runtime.Job.Id, acquisition ? 0 : runtime.Job.Run, runtime.Replay?.Presets[runtime.PresetIndex].Id,
                clock.GetUtcNow(), runtime.Session?.CurrentTimeUtc, severity, code, json)))
            {
                case OperationLogStatus.BacklogFull:
                    runtime.DiagnosticError = "Operational log initialization backlog is full; wait for storage initialization before continuing.";
                    return;
                case OperationLogStatus.Queued: return;
                default: runtime.DiagnosticError = null; break;
            }
        }
        catch (Exception error) when (StorageFailure.Is(error))
        {
            runtime.DiagnosticError = "Operational log storage is unavailable. Restore storage before continuing replay.";
            runtime.Session?.Pause(); runtime.Job = runtime.Job with { State = runtime.Job.CanReplay ? ReplayJobState.Paused : ReplayJobState.Failed, Message = runtime.DiagnosticError };
            PublishSnapshot(runtime);
        }
    }
}
