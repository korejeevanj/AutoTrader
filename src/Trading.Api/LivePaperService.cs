using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Trading.Api.Hubs;
using Trading.Data;
using Trading.Domain;
using Trading.Engine;
using Trading.Portfolio;
using Trading.Recorder;
using Trading.Replay;
using Trading.Risk;

namespace Trading.Api;

public sealed class LivePaperOptions
{
    public bool ConnectOnStartup { get; set; }
    public bool ArchiveClosedDays { get; set; } = true;
    public string RootDirectory { get; set; } = "../../data/live";
    public string EngineProfile { get; set; } = "../Trading.Engine/engine.paper.example.json";
    public string FeeConfiguration { get; set; } = "../Trading.Replay/replay.example.json";
}
public sealed record LivePaperView(string Mode, string Status, string? ErrorCode, string CredentialStatus,
    string? RunId, DateOnly? TradingDate, DateTimeOffset? AsOfUtc, bool Connected, bool AutoEnabled,
    bool RecordingHealthy, bool RecordingIncomplete, long ReceivedSequence, long DurableSequence,
    int RecorderQueueDepth, double RecorderLagMilliseconds, IReadOnlyList<string> Issues,
    IReadOnlyList<HistoricalInstrument> Instruments, IReadOnlyList<string> Universe, IReadOnlyList<MarketTick> Quotes,
    IReadOnlyList<HistoricalCandle> FuturesBars, IReadOnlyList<CandidateAssessment> Candidates, IReadOnlyList<LiveQuoteFreshness> QuoteFreshness,
    QuotePaperState? Paper, decimal? SessionNet, PositionDecision? PositionDecision, IReadOnlyList<GateResult> ContextGates,
    DirectionalEvidence? Directional, bool PaperPersistenceHealthy, bool PaperPersistencePending,
    long MarketRecordingBytes, long FreeDiskBytes, string? ArchiveError);
public sealed record LiveQuoteFreshness(long InstrumentToken, string State, decimal? QuoteAgeSeconds,
    decimal? ReceiptAgeSeconds, decimal? LastTradeAgeSeconds);
public sealed record QuoteReplayRequest(string SourceRunId, string? EngineJson = null);
public sealed record QuoteReplayResult(string Id, string SourceRunId, string Mode, string SourceCodeVersion,
    string ReplayCodeVersion, string ConfigurationHash, QuoteSessionCheckpoint Result, IReadOnlyList<string> Limitations);

public sealed class LivePaperService : BackgroundService
{
    private readonly object gate = new();
    private readonly LivePaperOptions options;
    private readonly IHostEnvironment environment;
    private readonly KiteHistoricalClient kite;
    private readonly PublishedBankInputsClient published;
    private readonly IHttpClientFactory clients;
    private readonly IOptionsMonitor<KiteCredentialOptions> credentials;
    private readonly TimeProvider clock;
    private readonly IHubContext<DashboardHub> hub;
    private readonly MarketStoreSet markets;
    private readonly SqliteQuoteStore paperStore;
    private readonly SqliteQuoteStore replayStore;
    private readonly string root;
    private SqliteQuoteStore? marketStore; // the open run's trading-date file
    private QuoteTradingSession? session;
    private BufferedQuoteRecorder? recorder;
    private BufferedPaperCheckpoints? paperWriter;
    private QuoteSessionCheckpoint? acknowledged;
    private Task lastCommit = Task.CompletedTask;
    private DateOnly? heldMappingDate;
    private Task? feed;
    private long sequence;
    private string connectionId = "host";
    private string status = "Stopped";
    private string? errorCode;
    private DateTimeOffset lastCheckpoint;
    private CancellationToken stopping;
    private CancellationTokenSource? connectionLifetime;
    private string? archiveError;
    private DateOnly? archiveAttemptedOn;
    private (long Market, long Free) storageSample;
    private DateTimeOffset storageSampledAt;
    private const long LowDiskBytes = 5L * 1024 * 1024 * 1024;
    private static readonly string CodeVersion = QuoteTradingSession.Key(string.Join(":",
        typeof(LivePaperService).Assembly.ManifestModule.ModuleVersionId,
        typeof(QuoteTradingSession).Assembly.ManifestModule.ModuleVersionId,
        typeof(QuotePaperBroker).Assembly.ManifestModule.ModuleVersionId,
        typeof(ExperimentalOptionScenarios).Assembly.ManifestModule.ModuleVersionId,
        typeof(Trading.Analytics.QuoteSessionBars).Assembly.ManifestModule.ModuleVersionId,
        typeof(KiteStreamingDecoder).Assembly.ManifestModule.ModuleVersionId,
        typeof(TradingDecisionEngine).Assembly.ManifestModule.ModuleVersionId));

    public LivePaperService(IOptions<LivePaperOptions> options, IHostEnvironment environment, KiteHistoricalClient kite,
        PublishedBankInputsClient published, IHttpClientFactory clients, IOptionsMonitor<KiteCredentialOptions> credentials,
        TimeProvider clock, IHubContext<DashboardHub> hub)
    {
        this.options = options.Value; this.environment = environment; this.kite = kite; this.published = published;
        this.clients = clients; this.credentials = credentials; this.clock = clock; this.hub = hub;
        root = Path.GetFullPath(this.options.RootDirectory, environment.ContentRootPath);
        markets = new(root); paperStore = new(Path.Combine(root, "paper.sqlite"));
        replayStore = new(Path.Combine(root, "quote-replays.sqlite"));
    }

    public LivePaperView View()
    {
        lock (gate)
        {
            var storage = Storage();
            return new("PAPER + LIVE DATA", status, paperWriter?.Failure ?? errorCode, kite.CredentialStatus,
            session?.Manifest.Id, session?.Manifest.TradingDate, session?.CurrentTimeUtc, session?.FeedConnected ?? false,
            session?.AutoEnabled == true && acknowledged?.AutoEnabled == true, recorder?.Healthy == true && session?.RecorderHealthy == true,
            session?.RecordingIncomplete ?? false, sequence, recorder?.DurableSequence ?? 0, recorder?.QueueDepth ?? 0,
            recorder?.LagMilliseconds ?? 0, (session is null ? [] : session.Manifest.ReferenceIssues.Concat(session.Issues)
                .Concat(storage.Free is > 0 and < LowDiskBytes ? ["LOW_DISK_SPACE"] : [])).Distinct().ToArray(),
            session?.Manifest.Instruments.Where(item => item.Role is not (HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put)
                || session.Universe.Contains(item.Id)).ToArray() ?? [], session?.Universe ?? [], session?.Quotes ?? [],
            session?.FuturesBars ?? [], session?.Candidates ?? [], Freshness(), Volatile.Read(ref acknowledged)?.Paper,
            ReferenceEquals(session?.Paper, acknowledged?.Paper) ? session?.SessionNet : null,
            acknowledged?.Paper.Position?.Approval.Id == session?.Paper.Position?.Approval.Id ? session?.LastPositionDecision : null,
            session?.ContextGates ?? [], session?.Directional, paperWriter?.Healthy == true, !lastCommit.IsCompleted,
            storage.Market, storage.Free, archiveError);
        }
    }

    // Open recording size (database plus WAL) and free disk, refreshed at most every five seconds under the gate.
    private (long Market, long Free) Storage()
    {
        var now = clock.GetUtcNow();
        if (now - storageSampledAt < TimeSpan.FromSeconds(5)) return storageSample;
        storageSampledAt = now;
        try
        {
            var size = marketStore?.FilePath is { } path ? new[] { path, path + "-wal" }.Where(File.Exists).Sum(file => new FileInfo(file).Length) : 0;
            var anchor = root; while (!Directory.Exists(anchor) && Path.GetDirectoryName(anchor) is { } parent) anchor = parent;
            storageSample = (size, new DriveInfo(Path.GetPathRoot(anchor)!).AvailableFreeSpace);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { storageSample = (0, 0); }
        return storageSample;
    }

    // Once per IST date, outside 08:00–15:45 so compression never competes with the open or the session.
    private void ArchiveWhenIdle(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        if (!options.ArchiveClosedDays || archiveAttemptedOn == DateOnly.FromDateTime(local.DateTime)
            || local.TimeOfDay is { } time && time >= TimeSpan.FromHours(8) && time < new TimeSpan(15, 45, 0)) return;
        archiveAttemptedOn = DateOnly.FromDateTime(local.DateTime); ArchiveOldDays();
    }

    // Compresses closed trading-date files beyond the newest two. Runs off the feed path; a failure is shown, never fatal.
    private void ArchiveOldDays() => _ = Task.Run(() =>
    {
        try { markets.ArchiveClosedDays(); lock (gate) archiveError = null; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { lock (gate) archiveError = "MARKET_ARCHIVE_FAILED"; }
    });

    public async Task CommandAsync(QuoteControl command, CancellationToken token = default)
    {
        Task commit;
        lock (gate)
        {
            if (command.Command == "start")
            {
                if (feed is { IsCompleted: false }) return;
                if (status == "StorageFault") throw new InvalidOperationException("RESTART_REQUIRED_AFTER_STORAGE_FAILURE");
                status = "Preparing"; errorCode = null;
                feed = Task.Run(() => RunFeedAsync(stopping)); return;
            }
            if (session is null || recorder is null) throw new InvalidOperationException("LIVE_SESSION_NOT_STARTED");
            PublishLocked("Control", JsonSerializer.SerializeToUtf8Bytes(command, QuoteTradingSession.Json));
            commit = lastCommit;
        }
        await commit.WaitAsync(TimeSpan.FromSeconds(5), token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stopping = stoppingToken;
        try
        {
            if (File.Exists(Path.Combine(root, "paper.sqlite")))
            {
                var previous = paperStore.Runs().FirstOrDefault(run => !run.Closed);
                if (previous?.CheckpointJson is not null)
                {
                    var manifest = JsonSerializer.Deserialize<QuoteRunManifest>(previous.ManifestJson, QuoteTradingSession.Json)!;
                    var checkpoint = JsonSerializer.Deserialize<QuoteSessionCheckpoint>(previous.CheckpointJson, QuoteTradingSession.Json)!;
                    lock (gate)
                    {
                        session = new(manifest, checkpoint, CodeVersion);
                        acknowledged = checkpoint; paperWriter = new(paperStore);
                        marketStore = markets.StoreOf(previous.Id);
                        session.RecoverTail(marketStore.Read(previous.Id));
                        sequence = Math.Max(previous.LastSequence, marketStore.Runs().Single(run => run.Id == previous.Id).LastSequence);
                        recorder = new(marketStore, clock);
                        PublishLocked("Recovered", JsonSerializer.SerializeToUtf8Bytes(session.Checkpoint(), QuoteTradingSession.Json));
                        if (manifest.CodeVersion != CodeVersion) PublishLocked("CodeVersion", Encoding.UTF8.GetBytes(CodeVersion));
                        status = "RecoveredPaused";
                    }
                    if (manifest.TradingDate == LocalDate(clock.GetUtcNow()) || checkpoint.Paper.Position is not null || options.ConnectOnStartup)
                        await CommandAsync(new("start", "startup-recovery"), stoppingToken);
                }
            }
            if (options.ConnectOnStartup && session is null) await CommandAsync(new("start", "startup"), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                lock (gate)
                {
                    ArchiveWhenIdle(clock.GetUtcNow());
                    if (session is not null && recorder is not null && status is not ("StorageFault" or "RollingOver"))
                    {
                        if (NeedsNewReferences()) connectionLifetime?.Cancel();
                        PublishLocked("Health", JsonSerializer.SerializeToUtf8Bytes(new QuoteRecorderHealth(recorder.Healthy, recorder.DurableSequence), QuoteTradingSession.Json));
                    }
                }
                // UI delivery cannot hold up protection timers or ingestion.
                _ = BroadcastAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or JsonException or ArgumentException)
        { lock (gate) { status = "StorageFault"; errorCode = "LIVE_STORAGE_UNAVAILABLE"; session?.Degrade(errorCode); } }
        finally
        {
            if (feed is not null) try { await feed.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
            if (recorder is not null) try { await recorder.DisposeAsync(); } catch (TimeoutException) { }
            if (paperWriter is not null) await paperWriter.DisposeAsync();
        }
    }

    private void PublishLocked(string kind, byte[] payload, DateTimeOffset? receivedAtUtc = null)
    {
        if (session is null || recorder is null) return;
        var before = session.Paper;
        var now = clock.GetUtcNow();
        var item = new RecordedMarketEvent(session.Manifest.Id, ++sequence, receivedAtUtc ?? now, connectionId, kind, payload,
            new(now, recorder.Healthy, recorder.DurableSequence, paperWriter?.Healthy == true, Volatile.Read(ref acknowledged)?.LastSequence ?? 0));
        if (!recorder.TryRecord(item))
        {
            session.Degrade("RECORDER_CAPACITY_EXCEEDED");
            item = item with { Admission = item.Admission! with { RecordingHealthy = false } };
        }
        try { session.Apply(item); }
        finally
        {
            var decisions = kind is "Health" or "Control" || !ReferenceEquals(before, session.Paper)
                ? JsonSerializer.Serialize(new { sequence = session.LastSequence, timeUtc = session.CurrentTimeUtc,
                    codeVersion = session.ExecutingCodeVersion, candidates = session.Candidates, context = session.ContextGates,
                    position = session.LastPositionDecision }, QuoteTradingSession.Json) : null;
            if (!ReferenceEquals(before, session.Paper) || kind is "Control" or "Recovered" || now - lastCheckpoint >= TimeSpan.FromSeconds(1))
            {
                var checkpoint = session.Checkpoint();
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lastCommit = completed.Task;
                if (paperWriter?.TrySave(session.Manifest.Id, session.LastSequence, JsonSerializer.Serialize(checkpoint, QuoteTradingSession.Json), () =>
                {
                    Volatile.Write(ref acknowledged, checkpoint); completed.TrySetResult();
                }, decisions) != true) { session.Degrade("PAPER_CHECKPOINT_CAPACITY_EXCEEDED"); completed.TrySetException(new InvalidOperationException("PAPER_CHECKPOINT_CAPACITY_EXCEEDED")); }
                lastCheckpoint = now;
            }
            else if (decisions is not null && paperWriter?.TryDecision(session.Manifest.Id, session.LastSequence, decisions) != true)
                session.Degrade("PAPER_CHECKPOINT_CAPACITY_EXCEEDED");
        }
    }

    private async Task PrepareAsync(CancellationToken token)
    {
        var raw = await kite.MasterAsync(token); var captured = clock.GetUtcNow(); var date = LocalDate(captured);
        var master = KiteInstrumentMaster.Parse(raw, captured);
        var bank = await LiveBankNiftyReferences.LoadAsync(published, clients.CreateClient("NseHistoricalContracts"), root, date, captured, token);
        captured = clock.GetUtcNow();
        if (bank.Weights is { } weights) bank = bank with { Weights = weights with { AvailableAtUtc = captured, EffectiveFromUtc = captured,
            PublicationEvidence = weights.PublicationEvidence is { } evidence ? evidence with { EstablishedAtUtc = captured } : null } };
        var definitions = LiveBankNiftyReferences.Resolve(master, date, bank.Weights);
        var ticks = definitions.Where(item => item.Role is HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put)
            .Select(item => item.TickSize).Distinct().ToArray();
        if (ticks.Length != 1 || ticks[0] is not > 0) throw new InvalidDataException("UNSUPPORTED_OPTION_TICK_UNIVERSE");
        var settings = EngineConfiguration.FromJson(await File.ReadAllTextAsync(Path.GetFullPath(options.EngineProfile, environment.ContentRootPath), token));
        settings = settings with { Version = "live-quote-paper-v1", PaperSimulation = settings.PaperSimulation with
            { Version = "quote-ask-bid-one-tick-v1", EntryAdverseSlippage = ticks[0]!.Value, ExitAdverseSlippage = ticks[0]!.Value } };
        var feeJson = JsonDocument.Parse(await File.ReadAllTextAsync(Path.GetFullPath(options.FeeConfiguration, environment.ContentRootPath), token));
        using (feeJson)
        {
            var fees = feeJson.RootElement.GetProperty("FeeSchedules").Deserialize<ResearchFeeSchedule[]>()!
                .SingleOrDefault(item => item.Applies(captured, settings.Strategies.Single(item => item.IndexId == "BANKNIFTY").Risk.FeeProfileId, false))
                ?? throw new InvalidDataException("EFFECTIVE_FEES_UNAVAILABLE");
            var id = "live-" + date.ToString("yyyyMMdd") + "-" + Guid.NewGuid().ToString("N");
            var index = new IndexDefinition("BANKNIFTY", "Bank Nifty", "NSE", "Asia/Kolkata", "Banks",
                "KITE:" + master.Sha256, captured, null, false, new(true, true, true));
            var manifest = new QuoteRunManifest(id, date, captured, index, definitions, bank.Weights, settings.ToJson(), fees, CodeVersion, bank.Issues);
            var json = JsonSerializer.Serialize(manifest, QuoteTradingSession.Json);
            lock (gate)
            {
                marketStore = markets.ForDate(date);
                marketStore.Create(id, captured, json); paperStore.Create(id, captured, json);
                session = new(manifest, executingCodeVersion: CodeVersion); sequence = 0; recorder = new(marketStore, clock);
                paperWriter = new(paperStore); acknowledged = null; lastCheckpoint = default;
                PublishLocked("InstrumentMaster", raw);
                PublishLocked("MembershipReference", bank.MembershipCsv);
                PublishLocked("WeightArchive", bank.WeightArchive);
                PublishLocked("WeightReference", JsonSerializer.SerializeToUtf8Bytes(bank.Weights, QuoteTradingSession.Json));
            }
        }
    }

    private async Task RunFeedAsync(CancellationToken token)
    {
        try
        {
            if (session is null) await PrepareAsync(token);
            for (var attempt = 0; !token.IsCancellationRequested; attempt++)
            {
                if (NeedsNewReferences())
                {
                    if (session!.Paper.Position is { } held)
                    {
                        var masterBytes = await kite.MasterAsync(token);
                        var master = KiteInstrumentMaster.Parse(masterBytes, clock.GetUtcNow());
                        var definition = held.Approval.Contract;
                        if (!master.Instruments.Any(item => item.InstrumentToken == definition.InstrumentToken
                            && item.Exchange + ":" + item.TradingSymbol == definition.Id && item.Expiry == definition.Expiry
                            && item.Expiry >= LocalDate(clock.GetUtcNow()) && item.LotSize == definition.LotSize && item.TickSize == definition.TickSize))
                            throw new KiteDataException("HELD_CONTRACT_CURRENT_MAPPING_UNVERIFIED_EXIT_PENDING");
                        lock (gate) { heldMappingDate = LocalDate(clock.GetUtcNow()); PublishLocked("HeldContractMasterValidation", masterBytes); }
                    }
                    else
                    {
                    var oldRecorder = recorder!;
                    var oldPaperWriter = paperWriter!;
                    lock (gate) { status = "RollingOver"; PublishLocked("Control", JsonSerializer.SerializeToUtf8Bytes(new QuoteControl("pause", "session-close"), QuoteTradingSession.Json)); }
                    await oldRecorder.DisposeAsync();
                    await oldPaperWriter.DisposeAsync();
                    lock (gate)
                    {
                        if (oldRecorder.Failure is not null || oldRecorder.DurableSequence != sequence || oldPaperWriter.Failure is not null || !lastCommit.IsCompletedSuccessfully)
                        { status = "StorageFault"; errorCode = "SESSION_CLOSE_INCOMPLETE_DRAIN"; session.Degrade(errorCode); return; }
                        var checkpoint = JsonSerializer.Serialize(session.Checkpoint(), QuoteTradingSession.Json);
                        paperStore.Checkpoint(session.Manifest.Id, sequence, checkpoint, true);
                        marketStore!.Checkpoint(session.Manifest.Id, sequence, checkpoint, true);
                        session = null; recorder = null; paperWriter = null; acknowledged = null; marketStore = null;
                    }
                    await PrepareAsync(token);
                    }
                }
                try
                {
                    await using var socket = new KiteStreamingClient(clock);
                    using var connectionToken = CancellationTokenSource.CreateLinkedTokenSource(token);
                    lock (gate) connectionLifetime = connectionToken;
                    var secret = credentials.CurrentValue;
                    await socket.ConnectAsync(new(secret.ApiKey, secret.AccessToken), connectionToken.Token);
                    HashSet<long> subscribed;
                    lock (gate)
                    {
                        connectionId = Guid.NewGuid().ToString("N"); status = "Connected"; errorCode = null;
                        PublishLocked("Connected", []);
                        subscribed = session!.Manifest.TradingDate != LocalDate(clock.GetUtcNow()) && session.Paper.Position is { } overnight
                            ? [overnight.Approval.Contract.InstrumentToken]
                            : session.Manifest.Instruments.Where(item => item.Role is not (HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put)
                                || session.Universe.Contains(item.Id)).Select(item => item.InstrumentToken).ToHashSet();
                        PublishLocked("Subscription", JsonSerializer.SerializeToUtf8Bytes(new { mode = "full", tokens = subscribed.Order().ToArray() }));
                    }
                    await socket.SubscribeAsync(subscribed.Order().ToArray(), connectionToken.Token);
                    attempt = 0;
                    await socket.ReceiveAsync(async (kind, bytes, received) =>
                    {
                        long[] additional;
                        lock (gate)
                        {
                            PublishLocked(kind, bytes, received);
                            additional = session!.Manifest.Instruments.Where(item => session.Manifest.TradingDate == LocalDate(clock.GetUtcNow())
                                && session.Universe.Contains(item.Id) && !subscribed.Contains(item.InstrumentToken))
                                .Select(item => item.InstrumentToken).ToArray();
                            if (additional.Length != 0)
                            {
                                foreach (var value in additional) subscribed.Add(value);
                                PublishLocked("Subscription", JsonSerializer.SerializeToUtf8Bytes(new { mode = "full", tokens = additional }));
                            }
                        }
                        if (additional.Length != 0) await socket.SubscribeAsync(additional, connectionToken.Token);
                    }, connectionToken.Token);
                }
                catch (Exception error) when (error is KiteDataException or System.Net.WebSockets.WebSocketException
                    || error is OperationCanceledException && !token.IsCancellationRequested)
                {
                    lock (gate)
                    {
                        connectionLifetime = null;
                        status = "Reconnecting"; errorCode = error is KiteDataException provider ? provider.Code : "STREAM_DISCONNECTED";
                        PublishLocked("Disconnected", Encoding.UTF8.GetBytes(errorCode));
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5)))), clock, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is KiteDataException or InvalidDataException or IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException
            or JsonException or FormatException or ArgumentException or OverflowException)
        {
            lock (gate) { status = status == "StorageFault" ? status : "Blocked"; errorCode = error is KiteDataException provider ? provider.Code
                : error.Message == "UNRESOLVED_PRIOR_SESSION_POSITION" ? error.Message : "LIVE_PREPARATION_OR_STORAGE_FAILED"; session?.Degrade(errorCode); }
        }
        finally { lock (gate) { connectionLifetime = null; if (session is not null && status != "StorageFault") PublishLocked("Disconnected", Encoding.UTF8.GetBytes("HOST_FEED_STOPPED")); } }
    }

    public IReadOnlyList<object> Runs() => markets.Runs().Select(run => (object)new { run.Id, run.CreatedAtUtc, run.LastSequence, run.Closed,
            engineJson = JsonSerializer.Deserialize<QuoteRunManifest>(run.ManifestJson, QuoteTradingSession.Json)!.EngineJson }).ToArray();

    public IReadOnlyList<JsonElement> Decisions(string id, long after, int limit) => paperStore.Decisions(id, after, limit);

    public QuoteReplayResult Rehearse()
    {
        var template = QuoteRehearsal.Manifest();
        var manifest = template with { Id = "synthetic-quotes-" + Guid.NewGuid().ToString("N"), CodeVersion = CodeVersion };
        var store = markets.ForDate(LocalDate(clock.GetUtcNow()));
        store.Create(manifest.Id, clock.GetUtcNow(), JsonSerializer.Serialize(manifest, QuoteTradingSession.Json));
        var events = QuoteRehearsal.Lifecycle().Select(item => item with { RunId = manifest.Id }).ToArray();
        store.Append(events);
        store.Checkpoint(manifest.Id, events[^1].Sequence, "{}", true);
        return Replay(new(manifest.Id));
    }

    public QuoteReplayResult Replay(QuoteReplayRequest request)
    {
        var source = markets.Runs().SingleOrDefault(item => item.Id == request.SourceRunId) ?? throw new InvalidOperationException("RECORDING_NOT_FOUND");
        var manifest = JsonSerializer.Deserialize<QuoteRunManifest>(source.ManifestJson, QuoteTradingSession.Json)!;
        var originalCode = manifest.CodeVersion;
        if (request.EngineJson is not null)
        {
            var changed = EngineConfiguration.FromJson(request.EngineJson);
            manifest = manifest with { EngineJson = changed.ToJson() };
        }
        var runner = new QuoteTradingSession(manifest, executingCodeVersion: CodeVersion, mode: EngineDataMode.PAPER_REPLAY,
            applyRecoveryBoundaries: request.EngineJson is null || manifest.EngineJson == JsonSerializer.Deserialize<QuoteRunManifest>(source.ManifestJson, QuoteTradingSession.Json)!.EngineJson);
        var sourceManifest = JsonSerializer.Deserialize<QuoteRunManifest>(source.ManifestJson, QuoteTradingSession.Json)!;
        var sourceSession = new QuoteTradingSession(sourceManifest);
        var sourceStore = markets.StoreOf(source.Id);
        foreach (var item in sourceStore.Read(source.Id))
        {
            try { sourceSession.Apply(item); }
            catch (InvalidOperationException) when (item.Kind == "Control") { }
            if (sourceSession.Universe.Count != 0) break;
        }
        runner.FreezeReplayUniverse(sourceSession.Universe);
        foreach (var item in sourceStore.Read(source.Id))
        {
            try { runner.Apply(item); }
            catch (InvalidOperationException) when (item.Kind == "Control") { /* rejected controls remain part of original evidence */ }
        }
        var result = new QuoteReplayResult("quote-replay-" + Guid.NewGuid().ToString("N"), source.Id, manifest.Index.IsSynthetic ? "SYNTHETIC TEST" : "PAPER + REPLAY",
            originalCode, CodeVersion, QuoteTradingSession.Key(manifest.EngineJson), runner.Checkpoint(),
            ["RECORDED_UNIVERSE_ONLY", "OBSERVED_DEPTH_DOES_NOT_ESTABLISH_REAL_FILL_PROBABILITY",
                .. (originalCode == CodeVersion ? Array.Empty<string>() : new[] { "SOURCE_CODE_DIFFERS_REPLAY_USES_CURRENT_BUILD" }),
                .. (!source.Closed ? new[] { "RECORDING_STILL_OPEN_REPLAY_USES_COMMITTED_PREFIX" } : Array.Empty<string>()),
                .. (request.EngineJson is not null ? new[] { "COUNTERFACTUAL_PAPER_DURABILITY_TIMING_NOT_OBSERVED" } : Array.Empty<string>()),
                .. (runner.RecordingIncomplete ? new[] { "SOURCE_RECORDING_HAS_GAPS_OR_RECOVERY" } : Array.Empty<string>())]);
        var json = JsonSerializer.Serialize(result, QuoteTradingSession.Json);
        replayStore.Create(result.Id, clock.GetUtcNow(), JsonSerializer.Serialize(manifest, QuoteTradingSession.Json));
        replayStore.Checkpoint(result.Id, runner.LastSequence, json, true);
        return result;
    }

    private static DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")).DateTime);

    private IReadOnlyList<LiveQuoteFreshness> Freshness()
    {
        if (session is null) return [];
        var now = clock.GetUtcNow();
        var date = LocalDate(now);
        var open = HistoricalAcquisitionStore.Midnight(date, "Asia/Kolkata").AddHours(9).AddMinutes(15);
        var marketOpen = date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && now >= open && now < open.AddHours(6).AddMinutes(15);
        return session.Quotes.Select(tick =>
        {
            var quoteAge = Trading.Analytics.DashboardAnalytics.AgeSeconds(now, tick.ExchangeTimeUtc);
            var receiptAge = Trading.Analytics.DashboardAnalytics.AgeSeconds(now, tick.ReceivedAtUtc);
            var tradeAge = Trading.Analytics.DashboardAnalytics.AgeSeconds(now, tick.LastTradeTimeUtc);
            var state = !marketOpen ? "market-closed" : !session.FeedConnected ? "disconnected"
                : quoteAge is >= 0 and <= 2 && receiptAge is >= 0 and <= 2 ? "fresh" : "stale-or-unavailable";
            return new LiveQuoteFreshness(tick.InstrumentToken, state, quoteAge, receiptAge, tradeAge);
        }).ToArray();
    }

    private bool NeedsNewReferences()
    {
        lock (gate)
        {
            if (session is null) return false;
            var date = LocalDate(clock.GetUtcNow());
            if (session.Manifest.TradingDate != date) return session.Paper.Position is null || heldMappingDate != date;
            var refresh = HistoricalAcquisitionStore.Midnight(date, "Asia/Kolkata").AddHours(8).AddMinutes(30);
            return session.Manifest.CreatedAtUtc < refresh && clock.GetUtcNow() >= refresh && session.Universe.Count == 0 && session.Paper.Attempts.Count == 0;
        }
    }

    private int broadcasting;
    private async Task BroadcastAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref broadcasting, 1) != 0) return;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(1));
            await hub.Clients.All.SendAsync("livePaperChanged", new { runId = session?.Manifest.Id, sequence }, deadline.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException) { }
        finally { Interlocked.Exchange(ref broadcasting, 0); }
    }
}

public static class LivePaperRoutes
{
    public static void MapLivePaper(this WebApplication app)
    {
        var group = app.MapGroup("/api/live").WithTags("Live paper");
        if (app.Environment.IsDevelopment())
            group.MapPost("/rehearsal", (LivePaperService service) => Results.Json(service.Rehearse())).WithName("SyntheticQuoteRehearsal")
                .Produces<QuoteReplayResult>().ProducesProblem(500).WithSummary("Run a separate synthetic received-packet rehearsal, without provider access.")
                .WithDescription("Development-only. Fictional full-mode packets pass through SQLite, bar building, shared risk/engine, delayed manual fills, exit and replay. Never changes the live session; result mode is SYNTHETIC TEST.");
        group.MapGet("/status", (LivePaperService service) => Results.Json(service.View())).Produces<LivePaperView>().WithName("LivePaperStatus")
            .WithSummary("Live feed, recorder and local paper ledger status; no account/order access.")
            .WithDescription("Read-only server-clock freshness, recording completeness, candidate gates and acknowledged paper state. Does not connect to the provider.")
            .ProducesProblem(500);
        group.MapPost("/commands", async (QuoteControl command, LivePaperService service, CancellationToken token) =>
        {
            try { await service.CommandAsync(command, token); return Results.Json(service.View()); }
            catch (TimeoutException) { return Results.Problem(statusCode: 503, title: "Paper acknowledgement pending", detail: "Paper state has not committed. Inspect status before retrying the same request identity."); }
            catch (Exception error) when (error is InvalidOperationException or KiteDataException or ArgumentException)
            { return Results.Problem(statusCode: 409, title: "Paper command blocked", detail: error.Message); }
        }).WithName("LivePaperCommand").Produces<LivePaperView>().ProducesProblem(409).ProducesProblem(400).ProducesProblem(415).ProducesProblem(500).ProducesProblem(503)
            .WithSummary("Start recording, pause/arm auto, manual-test entry or request local paper exit.")
            .WithDescription("Commands: start, pause, arm, manual, exit. Stable requestId and server-resolved frozen-universe contractId make manual requests idempotent. No real order transport.");
        group.MapGet("/recordings", (LivePaperService service) => service.Runs()).WithName("LiveRecordings")
            .WithSummary("List local recorded quote sessions.").WithDescription("Run IDs select server-managed immutable source evidence, never client filesystem paths.").ProducesProblem(500);
        group.MapGet("/recordings/{id}/decisions", (string id, long? after, int? limit, LivePaperService service) =>
        {
            try { return Results.Json(service.Decisions(id, after ?? 0, limit ?? 50), QuoteTradingSession.Json); }
            catch (ArgumentException error) { return Results.Problem(statusCode: 400, title: "Invalid decision page", detail: error.Message); }
        }).WithName("LivePaperDecisionHistory").Produces<JsonElement[]>().ProducesProblem(400).ProducesProblem(500)
            .WithSummary("Read committed compressed candidate and position decision batches.")
            .WithDescription("Ordered SQLite evidence; after is exclusive ingress sequence, limit 1–200 (default 50). Does not recompute historical decisions.");
        group.MapPost("/replays", (QuoteReplayRequest request, LivePaperService service) =>
        {
            try { return Results.Json(service.Replay(request)); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException)
            { return Results.Problem(statusCode: 400, title: "Quote replay unavailable", detail: error.Message); }
        }).WithName("ReplayRecordedQuotes").Produces<QuoteReplayResult>().ProducesProblem(400).ProducesProblem(415).ProducesProblem(500)
            .WithSummary("Re-run an immutable received-event recording with original or changed engine settings.")
            .WithDescription("Frozen source universe and recorded arrival/timer order; no provider requests. Original or explicitly changed engine JSON produces a separately persisted result. Code and coverage limitations remain explicit.");
    }
}
