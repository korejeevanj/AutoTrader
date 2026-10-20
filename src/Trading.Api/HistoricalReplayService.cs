using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Trading.Data;
using Trading.Domain;
using Trading.Engine;
using Trading.Replay;

namespace Trading.Api;

public sealed partial class HistoricalReplayService : BackgroundService
{
    private readonly string serverInstanceId = Guid.NewGuid().ToString("N");
    private readonly HistoricalAcquisitionStore store;
    private readonly HistoricalReplayOptions options;
    private readonly KiteHistoricalClient provider;
    private readonly HistoricalAcquisition acquisition;
    private readonly TimeProvider clock;
    private readonly string configurationPath;
    private readonly ReplayPaperJournal paperJournal;
    private readonly Lazy<Task> journalInitialization;
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<string, RuntimeJob> jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim createGate = new(1, 1);
    private CancellationToken stopping;
    private bool replayRequested;
    private static readonly string[] Reports = ["summary.md", "trades.csv", "features.csv", "decisions.csv", "directional-evidence.csv", "directional-outcomes.csv", "directional-outcomes.json", "quality.json", "results.json", "run-manifest.json", "normalized-candles.json", "runtime.jsonl.gz"];

    private sealed class RuntimeJob(HistoricalReplayJob job)
    {
        public HistoricalReplayJob Job = job;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public CancellationTokenSource Cancellation = new();
        public HistoricalDataset? Dataset;
        public ReplayConfiguration? Replay;
        public EngineConfiguration? Engine;
        public ReplayRunLifecycle? Lifecycle;
        public IHistoricalReplaySession? Session => Lifecycle?.HasSession == true ? Lifecycle : null;
        public ReplayStep? LastStep => Lifecycle?.LastStep;
        public readonly Queue<ReplayUiDecision> Decisions = new();
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
        public HistoricalReplayView? PublishedView;
        public IReadOnlyDictionary<string, ReplayUiBar[]> Series = new Dictionary<string, ReplayUiBar[]>();
        public ReplayChartInstrument[] ChartChoices = [];
        public ReplayDataHealth? Health;
        /// <summary>Everything BuildHealth reads; health is rebuilt exactly when one of these changes.</summary>
        public (AcquisitionAttemptEvidence Evidence, bool CanReplay, bool PriceOnly, HistoricalReadinessSummary? Qualification, IReadOnlyList<string> Warnings) HealthInputs;
        public HistoricalReadinessSummary? Qualification;
        public AcquisitionAttemptEvidence AttemptEvidence = new(job.Attempt, job.Date,
            job.Instruments.Select(row => new HistoricalInstrumentAcquisition(row.Id, row.Token, row.Role, row.State, row.Rows, row.Cached, row.Error)));
        public HashSet<string>? RetryOnlyFailed;
        public string? DiagnosticError;
        public string? CheckpointError;
        public readonly ConcurrentQueue<PendingOperation> StartupOperations = new();
    }
    private sealed record PendingOperation(int Run, string? Preset, DateTimeOffset Wall, DateTimeOffset? Simulated, string Severity, string Code, string DetailJson);

    public HistoricalReplayService(IOptions<HistoricalReplayOptions> options, IWebHostEnvironment environment,
        KiteHistoricalClient provider, TimeProvider clock, NseHistoricalContractClient exchange, PublishedBankInputsClient bankInputs)
    {
        this.options = options.Value; this.provider = provider; this.clock = clock;
        store = new(Path.GetFullPath(this.options.RootDirectory, environment.ContentRootPath));
        paperJournal = new(Path.Combine(store.Root, "paper-replay.sqlite"));
        journalInitialization = new(async () =>
        {
            await Task.Run(paperJournal.Initialize);
            foreach (var runtime in jobs.Values)
                while (runtime.StartupOperations.TryDequeue(out var operation))
                    paperJournal.Operation(runtime.Job.Id, operation.Run, operation.Preset, operation.Wall, operation.Simulated, operation.Severity, operation.Code, operation.DetailJson);
        });
        configurationPath = Path.GetFullPath(this.options.ReplayConfigurationPath, environment.ContentRootPath);
        acquisition = new(store, new ProviderHistoricalSources(store, provider, exchange, bankInputs), clock, configurationPath, this.options.AcquirePublishedBankInputs);
        if (Directory.Exists(store.JobsRoot))
            foreach (var file in Directory.EnumerateFiles(store.JobsRoot, "job.json", SearchOption.AllDirectories))
            {
                HistoricalReplayJob? job;
                try { job = HistoricalAcquisitionStore.Read<HistoricalReplayJob>(file); }
                catch (Exception error) when (error is IOException or JsonException or InvalidDataException) { continue; }
                if (job is null || !Guid.TryParseExact(job.Id, "N", out _)) continue;
                jobs[job.Id] = new RuntimeJob(job with { CanReplay = false,
                    State = job.ReportsAvailable ? "Completed" : "Interrupted",
                    Message = "Server restarted. Retry preparation to recover the paused paper replay from pinned inputs." }) { RecoverSavedRun = true };
            }
    }

    public ReplayConnectionStatus Status()
    {
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var latest = DateOnly.FromDateTime(local.DateTime);
        var calendar = store.Calendar(latest.Year);
        var close = calendar is { Verified: true } ? calendar.SpecialSessions.SingleOrDefault(session => session.Date == latest)?.Close : null;
        if (TimeOnly.FromDateTime(local.DateTime) < (close ?? new TimeOnly(15, 30))) latest = latest.AddDays(-1);
        return new(provider.CredentialStatus, latest, "/api/replay/setup", "Historical metadata is date-specific. Current dumps never substitute for missing historical catalogues/weights.");
    }

    public async Task<HistoricalReplayJob> PrepareAsync(PrepareHistoricalReplayRequest request)
    {
        var calendar = acquisition.Session(request.Date);
        var fingerprint = acquisition.RequestFingerprint(request.Date);
        await createGate.WaitAsync();
        try
        {
            if (!request.Refresh)
            {
                var existing = jobs.Values.Select(runtime => runtime.Job).Where(job => job.Date == request.Date && job.RequestFingerprint == fingerprint
                    && job.State is not ("Failed" or "Cancelled" or "Interrupted" or "AuthenticationRequired" or "NeedsMetadata" or "NoData"))
                    .OrderByDescending(job => job.UpdatedAtUtc).FirstOrDefault();
                if (existing is not null)
                {
                    // A persisted completed job has reports but no in-memory session after
                    // process restart. Date-only preparation must rehydrate it automatically.
                    if ((!existing.CanReplay || !existing.PreparationComplete && !jobs[existing.Id].Acquiring)
                        && existing.State is not ("Queued" or "Resolving" or "Downloading" or "Validating" or "MarketClosed"))
                    { await CommandAsync(existing.Id, new("retry")); return jobs[existing.Id].Job; }
                    return existing;
                }
            }
            var id = Guid.NewGuid().ToString("N");
            var closed = calendar.Status == "MarketClosed";
            var job = new HistoricalReplayJob(id, request.Date, request.Refresh, fingerprint,
                closed ? "MarketClosed" : "Queued", calendar.Message, calendar.Status, null, [], [], 0, 0, false,
                "PAPER + REPLAY — CANDLE RESEARCH", clock.GetUtcNow());
            var runtime = new RuntimeJob(job);
            jobs[id] = runtime;
            await Save(runtime);
            replayRequested = true;
            if (!closed) await queue.Writer.WriteAsync(id);
            return job;
        }
        finally { createGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stopping = stoppingToken;
        if (File.Exists(Path.Combine(store.Root, "paper-replay.sqlite"))) _ = journalInitialization.Value;
        await Task.WhenAll(ProcessQueue(stoppingToken), ArchiveDaily(stoppingToken));
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        var playback = jobs.Values.Select(runtime => runtime.Playback).Where(task => task is not null).Select(task => task!).ToArray();
        if (playback.Length != 0) await Task.WhenAll(playback).WaitAsync(cancellationToken);
    }
    private async Task ProcessQueue(CancellationToken token)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(token))
        {
            var runtime = jobs[id];
            await runtime.Gate.WaitAsync(token);
            try
            {
                if (runtime.Job.State != "Queued") continue;
                runtime.Cancellation.Dispose(); runtime.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                runtime.Acquiring = true; runtime.Qualification = null; runtime.Job = runtime.Job with { State = "Resolving" };
            }
            finally { runtime.Gate.Release(); }
            try
            {
                await Change(runtime, job => job with { Message = "Initializing durable replay storage and indexes; preparation will continue automatically." });
                await journalInitialization.Value.WaitAsync(runtime.Cancellation.Token);
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
                        runtime.Job = runtime.Job with { State = "Queued", Message = "Resuming preparation from pinned cached inputs." };
                        try { await Save(runtime); await queue.Writer.WriteAsync(id); }
                        catch (HistoricalStorageException) { /* Failed checkpoint is already exposed; worker remains available. */ }
                    }
                }
                finally { runtime.Gate.Release(); }
            }
        }
    }

    private async Task AcquisitionFailed(RuntimeJob runtime, Exception error, bool stopping)
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
                runtime.Job = runtime.Job with { State = "Paused", CanReplay = true, PreparationComplete = true,
                    ReportsAvailable = false, Message = runtime.Lifecycle.ManifestError! };
                try { await Save(runtime); }
                catch (HistoricalStorageException) { /* A failed host checkpoint remains visibly blocked until storage recovers. */ }
                return;
            }
            var keepPreview = runtime.PriceOnly && runtime.Job.CanReplay && runtime.Job.State != "Cancelled" && error is not HistoricalStorageException;
            runtime.Job = keepPreview ? runtime.Job with { Message = $"Additional acquisition stopped ({code}); cached price playback remains available.",
                    Warnings = runtime.Job.Warnings.Append(code).Distinct().ToArray() }
                : runtime.Job with { State = cancelled ? stopping ? "Interrupted" : "Cancelled"
                    : code is "CREDENTIALS_MISSING" or "AUTHENTICATION_REQUIRED" ? "AuthenticationRequired" : "Failed",
                Message = cancelled ? "Preparation stopped; completed downloads remain cached."
                    : error is KiteDataException ? code : "Preparation failed. Check run diagnostics; source data remains cached.",
                CanReplay = false, Warnings = runtime.Job.Warnings.Append(code).Distinct().ToArray() };
            if (error is HistoricalStorageException) CheckpointFailed(runtime);
            else try { await Save(runtime); }
                catch (HistoricalStorageException) { /* Save publishes the recoverable failure without another failed write. */ }
        }
        finally { runtime.Gate.Release(); }
    }

    private async Task Acquire(RuntimeJob runtime, CancellationToken token)
    {
        var day = runtime.Job.Date;
        var outcome = await acquisition.PrepareAsync(new(runtime.Job.Id, day, runtime.Job.CalendarStatus, runtime.Job.Attempt,
            runtime.Job.Refresh, runtime.RetryOnlyFailed), new AcquisitionProgress(this, runtime), token);
        if (outcome is not AcquisitionOutcome.Acquired acquired) { await Conclude(runtime, outcome); return; }
        var (calendar, attempt, frozen, plan, manifestPath) = (acquired.Session, acquired.Attempt, acquired.Frozen, acquired.Plan, acquired.ManifestPath);
        var verdict = PreparationVerdict.Evaluate(ReplayConfiguration.FromJson(frozen.ReplayJson),
            ApplyProfitExit(EngineConfiguration.FromJson(frozen.EngineJson), runtime.Job.ProfitExitOverride), plan, manifestPath, day, calendar.Open, calendar.Close);
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
                runtime.Job = runtime.Job with { State = "NoData", Message = "No selected-session candles were available; this does not establish a market holiday.", CanReplay = false };
                await Save(runtime); return;
            }
            if (runtime.PriceOnly)
            {
                runtime.EnrichedDataset = dataset; runtime.EnrichedReplay = replay; runtime.EnrichedEngine = engine;
                if ((runtime.Session?.CurrentTimeUtc is null || runtime.Session.IsComplete) && runtime.Job.State != "Playing" && !runtime.Publishing) await ResetSession(runtime, recoverCurrent: true);
                runtime.Job = runtime.Job with { CanReplay = true, PreparationComplete = true, Warnings = verdict.Issues,
                    State = runtime.PriceOnly ? runtime.Job.State : "ReadyWithWarnings",
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
            runtime.Job = runtime.Job with { State = completedPlayback ? publishedReports ? "Completed" : "Paused"
                    : verdict.Ready ? "Ready" : "ReadyWithWarnings", CanReplay = true, PreparationComplete = true, ReportsAvailable = publishedReports,
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
        try { await store.ArchiveMasterAsync(await provider.MasterAsync(token), clock.GetUtcNow(), token); }
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
    private async Task Change(RuntimeJob runtime, Func<HistoricalReplayJob, HistoricalReplayJob> update)
    { await runtime.Gate.WaitAsync(); try { runtime.Job = update(runtime.Job); await Save(runtime); } finally { runtime.Gate.Release(); } }
    private async Task Save(RuntimeJob runtime)
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
    private void CheckpointFailed(RuntimeJob runtime)
    {
        runtime.Session?.Pause(); runtime.Qualification = null;
        runtime.CheckpointError = "Local storage is unavailable. Restore write access and retry preparation; previously cached sources remain preserved.";
        runtime.Job = runtime.Job with { State = "Failed", CanReplay = false, Message = runtime.CheckpointError,
            Warnings = runtime.Job.Warnings.Append("LOCAL_STORAGE_UNAVAILABLE").Distinct().ToArray() };
        PublishSnapshot(runtime);
    }
    private void PublishSnapshot(RuntimeJob runtime)
    {
        runtime.Revision++; Volatile.Write(ref runtime.PublishedView, BuildView(runtime));
        var inputs = (runtime.AttemptEvidence, runtime.Job.CanReplay, runtime.PriceOnly, runtime.Qualification, runtime.Job.Warnings);
        if (runtime.Health is not null && runtime.HealthInputs == inputs) return;
        runtime.HealthInputs = inputs;
        Volatile.Write(ref runtime.Health, BuildHealth(runtime));
    }
}
