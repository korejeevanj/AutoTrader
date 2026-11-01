using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Analytics;
using Trading.Data;
using Trading.Domain;
using Trading.Engine;
using Trading.Portfolio;
using Trading.Risk;

namespace Trading.Replay;

public sealed record QuoteRunManifest(string Id, DateOnly TradingDate, DateTimeOffset CreatedAtUtc,
    IndexDefinition Index, IReadOnlyList<HistoricalInstrument> Instruments, HistoricalWeightSet? Weights,
    string EngineJson, ResearchFeeSchedule Fees, string CodeVersion, IReadOnlyList<string> ReferenceIssues,
    string? SessionPolicy = null, string? ManualLifecycleWaiver = null);
public sealed record QuoteControl(string Command, string RequestId, string? ContractId = null);
public sealed record QuoteRecorderHealth(bool Healthy, long CommittedThrough);
public sealed record QuoteSessionCheckpoint(long LastSequence, DateTimeOffset? CurrentTimeUtc,
    QuotePaperState Paper, StrategyState Strategy, PositionPolicyState PositionPolicy,
    IReadOnlyList<MarketTick> Latest, Dictionary<string, QuoteBarState> Bars, IReadOnlyList<string> Universe,
    bool AutoEnabled, bool FeedConnected, bool RecorderHealthy, long CommittedThrough,
    bool LossPaused, DateTimeOffset? UnhealthySince, bool RecordingIncomplete,
    IReadOnlyList<CandidateAssessment> Candidates, PositionDecision? PositionDecision,
    IReadOnlyList<string> Issues, IReadOnlyDictionary<string, OptionScenarioAssessment> AcceptedModels, string? ExecutingCodeVersion = null,
    IReadOnlyList<GateResult>? ContextGates = null, DirectionalEvidence? Directional = null, long LastPaperEffectSequence = 0,
    Dictionary<string, Dictionary<long, decimal>>? AtpByMinute = null, Dictionary<string, DateTimeOffset>? RecoveredFrom = null,
    bool RearmPending = false);
// A feed gap that may be healed when the instrument's first post-gap tick proves no exchange day extreme was missed.
public sealed record FeedGapState(DateTimeOffset StartUtc, decimal? DayHigh, decimal? DayLow);

// Supplied ordered events/clock only. Shared by live hosting and recorded-quote replay.
public sealed class QuoteTradingSession
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() }, WriteIndented = false };
    private readonly TradingDecisionEngine engine;
    private readonly EngineDataMode mode;
    private readonly bool applyRecoveryBoundaries;
    private readonly bool assumeArmed;
    private readonly EngineConfiguration settings;
    private readonly IndexStrategyConfiguration profile;
    private readonly EngineSession session;
    private readonly Dictionary<long, HistoricalInstrument> instruments;
    private readonly Dictionary<string, HistoricalInstrument> definitionsById;
    private readonly Dictionary<string, QuoteSessionBars> bars = new(StringComparer.Ordinal);
    private readonly Dictionary<long, MarketTick> latest = [];
    private QuotePaperBroker broker;
    private StrategyState strategy = new();
    private PositionPolicyState positionPolicy = new();
    private List<string> universe = [];
    private readonly Dictionary<string, OptionScenarioAssessment> acceptedModels = [];
    private bool recovering;
    private DateTimeOffset? lastPacketAtUtc;
    private string? featureSignature;
    private FuturesFeatureEvidence[] cachedFeatures = [];
    private ConstituentBarEvidence[] cachedConstituents = [];
    private CompletedBarEvidence[] cachedBars = [];
    private IReadOnlyList<string>? frozenReplayUniverse;
    private long paperCommittedThrough = long.MaxValue;
    private static readonly TimeSpan RecorderStallTolerance = TimeSpan.FromSeconds(2);
    private DateTimeOffset? recorderStallSince;
    private long lastPaperEffectSequence;
    // Session policy v2 (recorded in the manifest; legacy runs keep the original behaviour so replays stay identical):
    // paper-persistence stall tolerance, a prior-run manual-lifecycle waiver, auto re-arm after a clean recovery,
    // and disclosed healing of short feed gaps using the exchange's own cumulative average traded price.
    public const string PolicyV2 = "live-session-policy-v2";
    // Below one minute the engine's contiguous-bar check also protects: a gap that swallows a whole minute bar leaves VWAP unavailable by itself.
    public static readonly TimeSpan MaximumRecoverableGap = TimeSpan.FromSeconds(60), RearmStability = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, FeedGapState> feedGaps = [];
    private readonly Dictionary<string, Dictionary<long, decimal>> atpByMinute = [];
    private readonly Dictionary<string, DateTimeOffset> recoveredFrom = [];
    private bool rearmPending;
    private DateTimeOffset? rearmHealthySince;
    private bool V2 => Manifest.SessionPolicy == PolicyV2;
    public IReadOnlyDictionary<string, DateTimeOffset> RecoveredFrom => recoveredFrom;
    public bool RearmPending => rearmPending;
    public QuoteRunManifest Manifest { get; }
    public string ExecutingCodeVersion { get; }
    public long LastSequence { get; private set; }
    public DateTimeOffset? CurrentTimeUtc { get; private set; }
    public bool AutoEnabled { get; private set; }
    public bool FeedConnected { get; private set; }
    public bool RecorderHealthy { get; private set; }
    public long CommittedThrough { get; private set; }
    public bool LossPaused { get; private set; }
    public bool RecordingIncomplete { get; private set; }
    // Counterfactual replay only: the source recording showed recorder gaps or health loss that the what-if overrode.
    public bool OverriddenRecordingGaps { get; private set; }
    public DateTimeOffset? UnhealthySince { get; private set; }
    public IReadOnlyList<CandidateAssessment> Candidates { get; private set; } = [];
    public PositionDecision? LastPositionDecision { get; private set; }
    public IReadOnlyList<string> Issues { get; private set; } = [];
    public IReadOnlyList<GateResult> ContextGates { get; private set; } = [];
    public DirectionalEvidence? Directional { get; private set; }
    public QuotePaperState Paper => broker.State;
    public IReadOnlyList<string> Universe => universe.AsReadOnly();
    public IReadOnlyList<MarketTick> Quotes => latest.Values.OrderBy(tick => tick.InstrumentToken).ToArray();
    public IReadOnlyList<HistoricalCandle> FuturesBars => bars[Future.Id].State.Completed;
    public HistoricalInstrument Future { get; }

    public QuoteTradingSession(QuoteRunManifest manifest, QuoteSessionCheckpoint? recovered = null, string? executingCodeVersion = null,
        EngineDataMode? mode = null, bool applyRecoveryBoundaries = true, bool assumeArmed = false)
    {
        this.assumeArmed = assumeArmed;
        Manifest = manifest; settings = EngineConfiguration.FromJson(manifest.EngineJson);
        ExecutingCodeVersion = executingCodeVersion ?? manifest.CodeVersion;
        this.mode = manifest.Index.IsSynthetic ? EngineDataMode.SYNTHETIC_TEST : mode ?? EngineDataMode.PAPER_LIVE_DATA;
        this.applyRecoveryBoundaries = applyRecoveryBoundaries;
        profile = settings.Strategies.Single(item => item.IndexId == "BANKNIFTY"); engine = new(settings);
        if (settings.Strategies.Count != 1 || profile.Lots != 1)
            throw new ArgumentException("Quote-paper v1 supports one BANKNIFTY strategy and one lot only.");
        var midnight = HistoricalAcquisitionStore.Midnight(manifest.TradingDate, "Asia/Kolkata");
        session = new(manifest.TradingDate.ToString("yyyy-MM-dd"), manifest.TradingDate, midnight);
        instruments = manifest.Instruments.ToDictionary(item => item.InstrumentToken);
        definitionsById = manifest.Instruments.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (manifest.Instruments.Where(item => item.Role is HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put)
            .Any(item => item.TickSize != settings.PaperSimulation.EntryAdverseSlippage || item.TickSize != settings.PaperSimulation.ExitAdverseSlippage))
            throw new ArgumentException("Quote-paper slippage must remain one actual contract tick; incompatible overrides are not supported.");
        Future = manifest.Instruments.Single(item => item.Role == HistoricalInstrumentRole.Futures);
        foreach (var item in manifest.Instruments.Where(item => item.Role is HistoricalInstrumentRole.Futures or HistoricalInstrumentRole.Constituent))
            bars[item.Id] = new(item.Id, session.At(profile.OpeningStart), profile.Evidence.MaximumReceiptAge);
        broker = CreateBroker();
        if (recovered is null) return;
        LastSequence = recovered.LastSequence; CurrentTimeUtc = recovered.CurrentTimeUtc;
        broker = CreateBroker(recovered.Paper); strategy = recovered.Strategy; positionPolicy = recovered.PositionPolicy;
        foreach (var tick in recovered.Latest) latest[tick.InstrumentToken] = tick;
        foreach (var item in recovered.Bars) bars[item.Key].Restore(item.Value);
        universe = recovered.Universe.ToList(); LossPaused = recovered.LossPaused;
        UnhealthySince = recovered.UnhealthySince; RecordingIncomplete = true;
        Candidates = recovered.Candidates; LastPositionDecision = recovered.PositionDecision;
        ContextGates = recovered.ContextGates ?? []; Directional = recovered.Directional;
        lastPaperEffectSequence = recovered.LastPaperEffectSequence;
        foreach (var model in recovered.AcceptedModels) acceptedModels[model.Key] = model.Value;
        foreach (var item in recovered.AtpByMinute ?? []) atpByMinute[item.Key] = new(item.Value);
        foreach (var item in recovered.RecoveredFrom ?? []) recoveredFrom[item.Key] = item.Value;
        Issues = ["RECOVERED_PAUSED_POSSIBLE_RECORDING_TAIL_LOSS"];
        if (ExecutingCodeVersion != (recovered.ExecutingCodeVersion ?? manifest.CodeVersion)) Issues = [.. Issues, "RECOVERED_WITH_DIFFERENT_CODE_BUILD"];
        // No recovered connection/arming claim survives a process restart.
    }

    private QuotePaperBroker CreateBroker(QuotePaperState? state = null) => new(state ?? new QuotePaperState(
        new(settings.PaperSimulation.InitialCapital, settings.PaperSimulation.InitialCapital), null, null, [], [], []))
        { Delay = settings.PaperSimulation.ExecutionDelay, EntryTimeout = settings.PaperSimulation.PendingEntryTimeout,
            MaximumQuoteAge = profile.Evidence.MaximumObservationAge };

    public QuoteSessionCheckpoint Checkpoint() => new(LastSequence, CurrentTimeUtc, Paper, strategy, positionPolicy,
        Quotes, bars.ToDictionary(item => item.Key, item => item.Value.State), Universe, AutoEnabled,
        FeedConnected, RecorderHealthy, CommittedThrough, LossPaused, UnhealthySince, RecordingIncomplete,
        Candidates, LastPositionDecision, Issues, new Dictionary<string, OptionScenarioAssessment>(acceptedModels), ExecutingCodeVersion, ContextGates, Directional, lastPaperEffectSequence,
        atpByMinute.ToDictionary(item => item.Key, item => new Dictionary<long, decimal>(item.Value)), new Dictionary<string, DateTimeOffset>(recoveredFrom), rearmPending);

    public void FreezeReplayUniverse(IReadOnlyList<string> recordedUniverse) => frozenReplayUniverse = recordedUniverse;

    public void RecoverTail(IEnumerable<RecordedMarketEvent> events)
    {
        recovering = true;
        try { foreach (var item in events.Where(item => item.Sequence > LastSequence)) Apply(item); }
        finally
        {
            recovering = false; FeedConnected = false; RecorderHealthy = false; AutoEnabled = false; rearmPending = false;
            RecordingIncomplete = true; Issues = Issues.Append("RECOVERY_TAIL_MARKET_ONLY_NO_RETROACTIVE_FILLS").Distinct().ToArray();
        }
    }

    public void Apply(RecordedMarketEvent item)
    {
        if (item.Sequence <= LastSequence) return;
        var now = item.Admission?.AvailableAtUtc ?? item.ReceivedAtUtc;
        if (CurrentTimeUtc > now) throw new InvalidDataException("ADMISSION_CLOCK_REGRESSION");
        if (LastSequence != 0 && item.Sequence != LastSequence + 1) Degrade("INGRESS_SEQUENCE_GAP");
        LastSequence = item.Sequence; CurrentTimeUtc = now;
        var beforePaper = Paper;
        if (item.Admission is { } admission)
        {
            CommittedThrough = admission.MarketCommittedThrough; paperCommittedThrough = admission.PaperCommittedThrough;
            ObserveRecorder(admission.RecordingHealthy, admission.PaperHealthy, now);
        }
        AssumeArmed();
        switch (item.Kind)
        {
            case "Connected": FeedConnected = true; break;
            case "Disconnected":
                if (AutoEnabled && V2) rearmPending = true;
                FeedConnected = false; AutoEnabled = false; RecordingIncomplete = true;
                RegisterFeedGap(now, lastPacketAtUtc ?? now);
                foreach (var builder in bars.Values) builder.GapAt(now);
                break;
            case "Health":
                var health = JsonSerializer.Deserialize<QuoteRecorderHealth>(item.Payload, Json)!;
                CommittedThrough = health.CommittedThrough;
                ObserveRecorder(health.Healthy && (item.Admission?.RecordingHealthy ?? true), item.Admission?.PaperHealthy ?? true, now);
                break;
            case "Binary":
                if (lastPacketAtUtc is { } previousPacket && now - previousPacket > profile.Evidence.MaximumFeedSilence
                    && previousPacket >= session.At(profile.OpeningStart) && now <= session.At(profile.SessionExit))
                {
                    RegisterFeedGap(now, previousPacket);
                    foreach (var builder in bars.Values) builder.Gap();
                    Issues = Issues.Append("FEED_SILENCE").Distinct(StringComparer.Ordinal).ToArray();
                }
                lastPacketAtUtc = now;
                try
                {
                    foreach (var decoded in KiteStreamingDecoder.Decode(item.Payload, item.ReceivedAtUtc, item.Sequence))
                    {
                        var tick = decoded with { AvailableAtUtc = now };
                        if (!instruments.TryGetValue(tick.InstrumentToken, out var definition)) continue;
                        if (latest.TryGetValue(tick.InstrumentToken, out var old) && (tick.ExchangeTimeUtc < old.ExchangeTimeUtc
                            || tick.ExchangeTimeUtc == old.ExchangeTimeUtc && tick.LastTradeTimeUtc == old.LastTradeTimeUtc
                            && tick.LastPrice == old.LastPrice && tick.DayVolume == old.DayVolume && tick.AveragePrice == old.AveragePrice
                            && tick.Bids.SequenceEqual(old.Bids) && tick.Asks.SequenceEqual(old.Asks))) continue;
                        latest[tick.InstrumentToken] = tick;
                        if (V2) { HealFeedGap(definition.Id, tick, now); RecordAtp(definition.Id, tick); }
                        if (now < session.LocalMidnightUtc.AddDays(1) && bars.TryGetValue(definition.Id, out var builder)) builder.Add(tick);
                        SelectUniverse(tick);
                        var beforePosition = Paper.Position?.Approval.Id;
                        var relevant = Paper.Position is { } monitored && (tick.InstrumentToken == monitored.Approval.Contract.InstrumentToken
                            || tick.InstrumentToken == Future.InstrumentToken);
                        if (relevant) Monitor(now);
                        broker.Advance(now, LastSequence, EntriesHealthy(now), session.At(profile.EntryEnd), session.At(profile.SessionExit));
                        if (!recovering) broker.OnQuote(tick, now, EntriesHealthy(now)
                            && (Paper.Pending is null || paperCommittedThrough >= Paper.Pending.AcceptedSequence), Reassess);
                        if (Paper.Position?.Approval.Id != beforePosition) Monitor(now); // initial spread/cash stop is observed immediately
                    }
                }
                catch (InvalidDataException) { Degrade("DECODE_ERROR"); }
                break;
            case "Control": if (!recovering) Control(JsonSerializer.Deserialize<QuoteControl>(item.Payload, Json)!, now); break;
            case "Recovered":
                if (item.Payload.Length != 0 && applyRecoveryBoundaries)
                    ApplyRecoveryBoundary(JsonSerializer.Deserialize<QuoteSessionCheckpoint>(item.Payload, Json)!);
                else if (item.Payload.Length == 0) Issues = Issues.Append("RECOVERY_BOUNDARY_MISSING").Distinct().ToArray();
                Degrade("RECOVERED_PAUSED_POSSIBLE_RECORDING_TAIL_LOSS"); FeedConnected = false; break;
        }
        AssumeArmed();
        foreach (var builder in bars.Values) builder.Advance(now);
        var heldQuoteHealthy = Paper.Position is not { } held || latest.TryGetValue(held.Approval.Contract.InstrumentToken, out var heldTick)
            && broker.Fresh(heldTick, now) && heldTick.Bids.FirstOrDefault()?.Price > 0;
        if (!EntriesHealthy(now) || !heldQuoteHealthy)
        {
            // A short recorder stall blocks entries but keeps auto armed; any other loss, or a stall past the tolerance, disarms it.
            var stallOnly = recorderStallSince is not null && !RecorderHealthy && heldQuoteHealthy && EntriesHealthy(now, ignoreRecorder: true);
            if (!stallOnly) { if (AutoEnabled && V2) rearmPending = true; AutoEnabled = false; }
            UnhealthySince ??= now;
            if (now - UnhealthySince >= TimeSpan.FromSeconds(5)) broker.RequestExit(now, LastSequence, "DATA_OR_RECORDER_LOSS");
        }
        else UnhealthySince = null;
        Rearm(now, heldQuoteHealthy);
        broker.Advance(now, LastSequence, EntriesHealthy(now), session.At(profile.EntryEnd), session.At(profile.SessionExit));
        if (item.Kind != "Binary") Monitor(now);
        if (item.Kind is "Health" or "Timer" && now < session.LocalMidnightUtc.AddDays(1)) Evaluate(now);
        if (!ReferenceEquals(beforePaper, Paper)) lastPaperEffectSequence = LastSequence;
    }

    // What-if only: treat the recorder as healthy and paper-auto as armed, so recorded quotes can be tested against
    // changed settings. Feed disconnects, stale quotes and the session loss pause still apply.
    private void AssumeArmed()
    {
        if (!assumeArmed) return;
        if (RecordingIncomplete || !RecorderHealthy) OverriddenRecordingGaps = true;
        RecorderHealthy = true; RecordingIncomplete = false; AutoEnabled = !LossPaused;
        CommittedThrough = LastSequence; paperCommittedThrough = long.MaxValue;
    }

    // Market recording that is behind for less than RecorderStallTolerance (a slow commit, not lost data) only blocks entries
    // meanwhile. Paper persistence loss, a stall past the tolerance, or a sequence gap (handled in Apply) void the session.
    private void ObserveRecorder(bool marketHealthy, bool paperHealthy, DateTimeOffset now)
    {
        if (marketHealthy && paperHealthy)
        {
            RecorderHealthy = true;
            if (recorderStallSince is not null && !RecordingIncomplete)
                Issues = Issues.Append("RECORDER_STALL_TRANSIENT").Distinct(StringComparer.Ordinal).ToArray();
            recorderStallSince = null;
            return;
        }
        RecorderHealthy = false;
        var tolerated = paperHealthy || V2; // v2: a slow paper commit blocks entries briefly; a latched write failure outlasts the tolerance
        if (tolerated) recorderStallSince ??= now;
        if (tolerated && now - recorderStallSince < RecorderStallTolerance) return;
        var issue = paperHealthy ? "RECORDER_NOT_HEALTHY_AT_INGRESS" : "PAPER_PERSISTENCE_NOT_HEALTHY_AT_INGRESS";
        // A what-if keeps the received market data: a recorder blip says the recording is incomplete, not that quotes were lost.
        if (assumeArmed) Issues = Issues.Append(issue).Distinct(StringComparer.Ordinal).ToArray(); else Degrade(issue);
    }

    public void Degrade(string issue)
    {
        recorderStallSince = null; rearmPending = false; feedGaps.Clear(); // lost recording cannot be healed or re-armed
        AutoEnabled = false; RecorderHealthy = false; RecordingIncomplete = true;
        if (CurrentTimeUtc is { } at) foreach (var builder in bars.Values) builder.GapAt(at);
        Issues = Issues.Append(issue).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static long MinuteKey(DateTimeOffset time) => time.ToUnixTimeSeconds() / 60;

    // Notes, per instrument whose coverage is still intact, that a feed gap began. Storage-side loss never lands here.
    private void RegisterFeedGap(DateTimeOffset now, DateTimeOffset start)
    {
        if (!V2 || now < session.At(profile.OpeningStart)) return;
        foreach (var (id, builder) in bars)
        {
            if (feedGaps.ContainsKey(id) || !builder.State.CompleteCoverage) continue;
            latest.TryGetValue(definitionsById[id].InstrumentToken, out var last);
            feedGaps[id] = new(start, last?.DayHigh, last?.DayLow);
        }
    }

    // First tick after a gap: if the exchange day high/low did not move, no extreme was missed, so the instrument's coverage is
    // restored and its VWAP comes from the exchange's cumulative average traded price from the gap's minute onward.
    // Otherwise coverage stays void. The post-gap bar still carries the whole outage's volume; the recovered VWAP never uses it.
    private void HealFeedGap(string id, MarketTick tick, DateTimeOffset now)
    {
        if (!feedGaps.Remove(id, out var gap)) return;
        var reason = now - gap.StartUtc > MaximumRecoverableGap ? "GAP_TOO_LONG"
            : gap.DayHigh is null || gap.DayLow is null || tick.AveragePrice is not > 0 || tick.ExchangeTimeUtc is null ? "EXCHANGE_FIELDS_MISSING"
            : tick.DayHigh != gap.DayHigh || tick.DayLow != gap.DayLow ? "DAY_EXTREME_MOVED_DURING_GAP" : null;
        if (reason is not null || !bars.TryGetValue(id, out var builder))
        {
            Issues = Issues.Append("FEED_GAP_NOT_RECOVERABLE:" + (reason ?? "UNKNOWN_INSTRUMENT")).Distinct(StringComparer.Ordinal).ToArray();
            return;
        }
        builder.Restore(builder.State with { CompleteCoverage = true });
        if (!recoveredFrom.ContainsKey(id)) { recoveredFrom[id] = DateTimeOffset.FromUnixTimeSeconds(MinuteKey(gap.StartUtc) * 60); atpByMinute[id] = []; }
        Issues = Issues.Append("FEED_GAP_RECOVERED_EXCHANGE_ATP").Distinct(StringComparer.Ordinal).ToArray();
    }

    private void RecordAtp(string id, MarketTick tick)
    {
        if (recoveredFrom.ContainsKey(id) && tick.AveragePrice is > 0 && tick.ExchangeTimeUtc is { } time)
            atpByMinute[id][MinuteKey(time)] = tick.AveragePrice.Value;
    }

    private decimal? BarVwap(string id, SessionFeature feature)
    {
        if (!V2 || !recoveredFrom.TryGetValue(id, out var from) || feature.Candle.StartUtc < from) return feature.Vwap;
        return feature.CompleteCoverage && atpByMinute.TryGetValue(id, out var map) && map.TryGetValue(MinuteKey(feature.Candle.StartUtc), out var atp) ? atp : null;
    }

    private string AtpLabel(string id, DateTimeOffset start)
        => V2 && recoveredFrom.TryGetValue(id, out var from) && start >= from ? "+exchange-atp-gap-recovered" : "";

    // After a feed loss, auto that was armed re-arms once entries have been continuously healthy for RearmStability.
    private void Rearm(DateTimeOffset now, bool heldQuoteHealthy)
    {
        if (!V2 || !rearmPending || AutoEnabled || LossPaused || !EntriesHealthy(now) || !heldQuoteHealthy) { rearmHealthySince = null; return; }
        rearmHealthySince ??= now;
        if (now - rearmHealthySince < RearmStability) return;
        AutoEnabled = true; rearmPending = false; rearmHealthySince = null;
        Issues = Issues.Append("AUTO_REARMED_AFTER_RECOVERY").Distinct(StringComparer.Ordinal).ToArray();
    }

    private void ApplyRecoveryBoundary(QuoteSessionCheckpoint boundary)
    {
        broker = CreateBroker(boundary.Paper); strategy = boundary.Strategy; positionPolicy = boundary.PositionPolicy;
        latest.Clear(); foreach (var tick in boundary.Latest) latest[tick.InstrumentToken] = tick;
        foreach (var item in boundary.Bars) bars[item.Key].Restore(item.Value);
        universe = boundary.Universe.ToList(); LossPaused = boundary.LossPaused; UnhealthySince = boundary.UnhealthySince;
        Candidates = boundary.Candidates; LastPositionDecision = boundary.PositionDecision;
        ContextGates = boundary.ContextGates ?? []; Directional = boundary.Directional;
        acceptedModels.Clear(); foreach (var model in boundary.AcceptedModels) acceptedModels[model.Key] = model.Value;
        lastPaperEffectSequence = boundary.LastPaperEffectSequence; featureSignature = null;
        Issues = boundary.Issues; AutoEnabled = false;
    }

    private bool EntriesHealthy(DateTimeOffset now, bool ignoreRecorder = false) => FeedConnected && (ignoreRecorder || RecorderHealthy) && !LossPaused
        && now >= session.LocalMidnightUtc && now < session.LocalMidnightUtc.AddDays(1)
        && latest.TryGetValue(Future.InstrumentToken, out var future) && broker.Fresh(future, now)
        && future.LastTradeTimeUtc is { } trade && trade <= now && now - trade <= profile.Evidence.MaximumObservationAge;

    private void SelectUniverse(MarketTick tick)
    {
        if (universe.Count != 0 || tick.InstrumentToken != Future.InstrumentToken || tick.LastPrice <= 0
            || tick.ExchangeTimeUtc < session.At(profile.OpeningStart) || !broker.Fresh(tick, tick.ReceivedAtUtc)) return;
        if (frozenReplayUniverse is not null) { universe = frozenReplayUniverse.ToList(); return; }
        var pairs = Manifest.Instruments.Where(item => item.Role is HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put)
            .GroupBy(item => item.Strike).Where(group => group.Count() == 2 && group.Select(item => item.Role).Distinct().Count() == 2)
            .OrderBy(group => group.Key).ToArray();
        if (pairs.Length < 15) { Issues = Issues.Append("OPTION_LADDER_INCOMPLETE").Distinct().ToArray(); return; }
        var anchor = pairs.Select((group, i) => (group.Key, i)).OrderBy(item => Math.Abs(item.Key!.Value - tick.LastPrice)).ThenBy(item => item.Key).First().i;
        if (anchor < 7 || anchor + 7 >= pairs.Length) { Issues = Issues.Append("OPTION_LADDER_EDGE_UNAVAILABLE").Distinct().ToArray(); return; }
        universe = pairs.Skip(anchor - 7).Take(15).SelectMany(group => group.OrderBy(item => item.Id, StringComparer.Ordinal)).Select(item => item.Id).ToList();
    }

    private void Control(QuoteControl control, DateTimeOffset now)
    {
        switch (control.Command)
        {
            case "pause": AutoEnabled = false; rearmPending = false; broker.CancelPending(now, "OPERATOR_PAUSE"); break;
            case "arm":
                if (!EntriesHealthy(now) || !Paper.Trades.Any(trade => trade.Origin == "MANUAL_TEST") && !(V2 && Manifest.ManualLifecycleWaiver is not null))
                    throw new InvalidOperationException("AUTO_REQUIRES_HEALTHY_FEED_AND_COMPLETED_MANUAL_LIFECYCLE");
                AutoEnabled = true; rearmPending = false; break;
            case "exit": broker.CancelPending(now, "OPERATOR_EXIT"); broker.RequestExit(now, LastSequence, "MANUAL_EXIT"); break;
            case "manual":
                if (string.IsNullOrWhiteSpace(control.RequestId) || string.IsNullOrWhiteSpace(control.ContractId)) throw new InvalidOperationException("MANUAL_IDENTITY_REQUIRED");
                var previous = Paper.Attempts.SingleOrDefault(attempt => attempt.Id == control.RequestId);
                if (previous is not null)
                {
                    if (previous.Contract.Id != control.ContractId) throw new InvalidOperationException("CONFLICTING_INTENT_ID");
                    return;
                }
                Evaluate(now, control); break;
            default: throw new InvalidOperationException("UNKNOWN_LIVE_COMMAND");
        }
    }

    private EvaluationIdentity Identity => new(Manifest.Id, LastSequence.ToString(), ExecutingCodeVersion, mode);
    private QuoteEvidence? Quote(HistoricalInstrument definition)
    {
        if (!latest.TryGetValue(definition.InstrumentToken, out var tick)) return null;
        return new($"{tick.Sequence}:{tick.PacketIndex}", definition.Id, tick.Bids.FirstOrDefault()?.Price, tick.Asks.FirstOrDefault()?.Price,
            tick.Bids.FirstOrDefault()?.Quantity, tick.Asks.FirstOrDefault()?.Quantity, tick.ExchangeTimeUtc, tick.ReceivedAtUtc,
            tick.ExchangeTimeUtc, tick.AvailableAtUtc ?? tick.ReceivedAtUtc, KiteStreamingDecoder.Version, tick.Sequence);
    }
    private UnderlyingPriceEvidence? Underlying() => latest.TryGetValue(Future.InstrumentToken, out var tick)
        ? new($"{tick.Sequence}:{tick.PacketIndex}", Future.Id, tick.LastPrice, tick.LastTradeTimeUtc, tick.ReceivedAtUtc, tick.AvailableAtUtc ?? tick.ReceivedAtUtc, KiteStreamingDecoder.Version) : null;
    private decimal? NetMark(DateTimeOffset now)
    {
        if (Paper.Position is not { } position) return 0m;
        if (!latest.TryGetValue(position.Approval.Contract.InstrumentToken, out var quote) || !broker.Fresh(quote, now)
            || quote.Bids.FirstOrDefault()?.Quantity < position.Units) return null;
        var exit = IndicativePlanning.ExecutablePrice(quote.Bids.FirstOrDefault()?.Price, position.Approval.Contract.TickSize!.Value, position.Approval.Contract.TickSize.Value, false);
        if (exit is null || !position.Approval.Fees.Applies(now, profile.Risk.FeeProfileId, Manifest.Index.IsSynthetic)) return null;
        return HistoricalResearchCalculations.Net(position.EntryPrice, exit.Value, position.Units, position.EntryFees.Total,
            HistoricalResearchCalculations.Fees(exit.Value, position.Units, false, position.Approval.Fees).Total);
    }
    public decimal? SessionNet => CurrentTimeUtc is { } now ? HistoricalResearchCalculations.SessionNet(Paper.Ledger, [NetMark(now)]) : null;
    private PortfolioEvidence Portfolio(DateTimeOffset now) => new("quote-portfolio:" + LastSequence, session.Id, now,
        HistoricalResearchCalculations.AvailableCash(Paper.Ledger, Paper.Pending is { } reservation ? [reservation.ReservedCash] : []),
        HistoricalResearchCalculations.SessionNet(Paper.Ledger, [NetMark(now)]),
        LossPaused, !AutoEnabled, true, EntriesHealthy(now),
        Paper.Pending is { } pending ? [new(pending.Id, "BANKNIFTY", Enum.Parse<EntryOrigin>(pending.Origin))]
            : Paper.Position is { } position ? [new(position.Approval.Id, "BANKNIFTY", Enum.Parse<EntryOrigin>(position.Approval.Origin))] : [],
        Paper.Attempts.Select(item => new AcceptedEntryAttempt(item.Id, "BANKNIFTY", session.Id, item.SetupId, Enum.Parse<EntryOrigin>(item.Origin), item.AcceptedAtUtc)).ToArray());

    private IndexEvaluationInput Build(DateTimeOffset now, QuoteControl? manual = null, bool includeCandidates = true)
    {
        var signature = string.Join(";", bars.Select(item => $"{item.Key}:{item.Value.State.Completed.Count}:{item.Value.State.CompleteCoverage}")) + ":" + string.Join(",", recoveredFrom.Select(item => item.Key + item.Value.Ticks));
        if (signature != featureSignature)
        {
        var futureFeatures = HistoricalSessionAnalytics.Calculate(FuturesBars, session.At(profile.OpeningStart), now, profile.Risk.StructuralLookbackBars);
        var complete = bars[Future.Id].State.CompleteCoverage;
        cachedFeatures = futureFeatures.Select(feature =>
        {
            var structural = feature.PreviousMeanTrueRange is { } mean ? HistoricalResearchCalculations.Structural(feature.Candle.Low,
                feature.Candle.High, feature.Candle.Close, mean, profile.Risk.StructuralBufferMultiplier) : ((decimal LongStop, decimal LongRisk, decimal ShortStop, decimal ShortRisk)?)null;
            return new FuturesFeatureEvidence(feature.Candle.Id + ":quote-features", feature.Candle.Id, session.Id, settings.Version,
                feature.Candle.AvailableAtUtc, feature.InputBarIds, complete ? BarVwap(Future.Id, feature) : null, profile.Risk.StructuralLookbackBars,
                profile.Risk.StructuralBufferMultiplier, structural?.LongStop, structural?.LongRisk, structural?.ShortStop, structural?.ShortRisk,
                HistoricalSessionAnalytics.Version + "+" + HistoricalResearchCalculations.Version + AtpLabel(Future.Id, feature.Candle.StartUtc), complete && feature.CompleteCoverage);
        }).ToArray();
        cachedConstituents = Manifest.Instruments.Where(item => item.Role == HistoricalInstrumentRole.Constituent).SelectMany(item =>
            HistoricalSessionAnalytics.Calculate(bars[item.Id].State.Completed, session.At(profile.OpeningStart), now, profile.Risk.StructuralLookbackBars)
                .Select(feature => new ConstituentBarEvidence(item.Id, session.Id, feature.Candle.StartUtc, feature.Candle.EndUtc, feature.Candle.AvailableAtUtc,
                    feature.Candle.Close, bars[item.Id].State.CompleteCoverage ? BarVwap(item.Id, feature) : null,
                    feature.CompleteCoverage && bars[item.Id].State.CompleteCoverage, HistoricalSessionAnalytics.Version + AtpLabel(item.Id, feature.Candle.StartUtc)))).ToArray();
        cachedBars = FuturesBars.Select(bar => new CompletedBarEvidence(bar.Id, bar.InstrumentId, session.Id, bar.StartUtc, bar.EndUtc, bar.AvailableAtUtc,
            bar.Open, bar.High, bar.Low, bar.Close, bar.Volume)).ToArray();
        featureSignature = signature;
        }
        var weight = Manifest.Weights;
        var candidateDefinitions = includeCandidates ? universe.Select(id => definitionsById[id]).ToArray() : [];
        return new(Identity, now, Manifest.Index, session, Future.Id,
            cachedBars, cachedFeatures,
            weight is null ? null : new(weight.Version, weight.EffectiveFromUtc, weight.EffectiveToUtc, weight.AvailableAtUtc,
                weight.Verified, weight.Members.Select(item => new ConstituentMember(item.InstrumentId, item.WeightPercent)).ToArray()),
            cachedConstituents, Underlying(), Manifest.Id + ":frozen-options", Manifest.CreatedAtUtc,
            candidateDefinitions.Select(definition => new OptionCandidateEvidence(Contract(definition), Quote(definition), null)).ToArray(),
            Portfolio(now), manual is null ? EntryOrigin.STRATEGY : EntryOrigin.MANUAL_TEST, manual?.RequestId, manual?.ContractId,
            new(Future.Id, "BANKNIFTY", Future.ValidFromUtc, Future.ValidToUtc, Future.AvailableAtUtc, Future.DefinitionVersion));
    }

    private OptionContractEvidence Contract(HistoricalInstrument item) => new(item.Id, "BANKNIFTY", item.Exchange,
        item.Role == HistoricalInstrumentRole.Call ? OptionRight.Call : OptionRight.Put, item.Expiry!.Value, item.Strike!.Value,
        item.LotSize!.Value, item.TickSize!.Value, item.ValidFromUtc, item.ValidToUtc, item.AvailableAtUtc, item.DefinitionVersion, Manifest.Index.IsSynthetic);

    private void Evaluate(DateTimeOffset now, QuoteControl? manual = null)
    {
        var input = Build(now, manual);
        var preview = engine.EvaluateIndex(input, strategy);
        if (manual is null) strategy = preview.State;
        var setup = manual is null ? preview.State.Setup : null;
        var feature = setup is null ? input.FuturesFeatures.LastOrDefault() : input.FuturesFeatures.SingleOrDefault(item => item.BarId == setup.SignalBarId);
        var models = new Dictionary<string, OptionScenarioAssessment>();
        input = input with { Candidates = input.Candidates.Select(candidate =>
        {
            var definition = Manifest.Instruments.Single(item => item.Id == candidate.Contract.Id);
            var underlyingRisk = candidate.Contract.Right == OptionRight.Call ? feature?.LongInitialRisk : feature?.ShortInitialRisk;
            if (feature?.CompleteSessionCoverage != true) return candidate;
            var stop = candidate.Contract.Right == OptionRight.Call ? feature?.LongStructuralStop : feature?.ShortStructuralStop;
            var model = Model(definition, now, underlyingRisk, stop);
            if (model is null || candidate.Quote is null) return candidate;
            models[candidate.Contract.Id] = model;
            return candidate with { Risk = new(candidate.Quote.Id + ":model:" + settings.Version, definition.Id, definition.DefinitionVersion,
                candidate.Quote.Id, setup?.SignalBarId, settings.Version, definition.LotSize!.Value, now, now.AddMilliseconds(250),
                Manifest.Fees.ProfileId, Manifest.Fees.Version, Manifest.Fees.EffectiveFromUtc, Manifest.Fees.EffectiveToUtc,
                ExperimentalOptionScenarios.Version, settings.PaperSimulation.Version, definition.TickSize!.Value, definition.TickSize.Value,
                profile.Risk.GrossCashStop, true, true, true, model.EntryPrice, model.RoundTripFees, model.RequiredCash, model.NetRisk,
                model.FavorableNetProfit, model.SpreadPercent, model.Friction, model.StrikeDistance, input.FuturesPrice?.Id,
                setup is null ? null : feature?.Id) };
        }).ToArray() };
        var result = engine.EvaluateIndex(input, manual is null ? strategy : null);
        ContextGates = result.ContextGates;
        if (manual is null) Directional = result.DirectionalEvidence;
        var allocation = engine.Allocate(input.Identity, now, session, input.Portfolio, [result]);
        Candidates = allocation.Decisions;
        var selection = Candidates.FirstOrDefault(candidate => candidate.Action == EntryAction.ENTER);
        if (!recovering && selection is not null && EntriesHealthy(now) && paperCommittedThrough >= lastPaperEffectSequence && (manual is not null || AutoEnabled)
            && selection.QuoteId is not null && latest[Future.InstrumentToken].Sequence <= CommittedThrough
            && input.Candidates.Single(candidate => candidate.Contract.Id == selection.ContractId).Quote!.Sequence <= CommittedThrough)
        {
            var definition = Manifest.Instruments.Single(item => item.Id == selection.ContractId);
            var model = models[selection.ContractId];
            var bullish = selection.Direction == TradeDirection.Bullish;
            var id = manual?.RequestId ?? Key(Manifest.Id + ":" + selection.SetupId + ":" + selection.ContractId);
            broker.Accept(new(id, selection.SetupId, selection.Origin.ToString(), definition, now, LastSequence, model.RequiredCash,
                profile.Risk.MaximumNetRisk, profile.Risk.GrossCashStop, input.FuturesPrice!.Price!.Value,
                bullish ? feature?.LongStructuralStop : feature?.ShortStructuralStop,
                (bullish ? feature?.LongInitialRisk : feature?.ShortInitialRisk)!.Value, Manifest.EngineJson, Manifest.Fees, Manifest.Index.IsSynthetic));
            acceptedModels[id + ":accept"] = model;
        }
        else if (manual is not null) throw new InvalidOperationException("MANUAL_ENTRY_BLOCKED:" + string.Join(",",
            Candidates.SingleOrDefault(candidate => candidate.ContractId == manual.ContractId)?.ReasonCodes ?? ["UNIVERSE_OR_RECORDING_NOT_READY"]));
    }

    private OptionScenarioAssessment? Model(HistoricalInstrument contract, DateTimeOffset now, decimal? risk, decimal? stop)
    {
        if (risk is not > 0 || !latest.TryGetValue(contract.InstrumentToken, out var quote) || !broker.Fresh(quote, now)
            || !EntriesHealthy(now) || !latest.TryGetValue(Future.InstrumentToken, out var future)
            || quote.Bids.FirstOrDefault() is not { } bid || quote.Asks.FirstOrDefault() is not { } ask
            || !Manifest.Fees.Applies(now, profile.Risk.FeeProfileId, Manifest.Index.IsSynthetic)) return null;
        var expiry = HistoricalAcquisitionStore.Midnight(contract.Expiry!.Value, "Asia/Kolkata").AddHours(15).AddMinutes(30);
        return ExperimentalOptionScenarios.Assess(future.LastPrice, contract.Strike!.Value, expiry, now, bid.Price, ask.Price,
            contract.TickSize!.Value, contract.LotSize!.Value, contract.Role == HistoricalInstrumentRole.Call, risk.Value, profile.Risk.GrossCashStop, Manifest.Fees, stop);
    }

    private QuoteFillAssessment? Reassess(QuoteEntryApproval intent, MarketTick quote)
    {
        var now = CurrentTimeUtc!.Value;
        if (intent.Contract.Expiry <= session.TradingDate || quote.Bids.FirstOrDefault() is not { } bid || quote.Asks.FirstOrDefault() is not { } ask
            || ask.Price <= 0 || (ask.Price - bid.Price) / ask.Price * 100 > profile.Evidence.MaximumSpreadPercent
            || Underlying()?.LastTradeTimeUtc is not { } trade || quote.ExchangeTimeUtc is not { } observed
            || (trade - observed).Duration() > profile.Evidence.MaximumQuoteSkew
            || intent.StructuralStop is { } stop && (intent.Contract.Role == HistoricalInstrumentRole.Call ? latest[Future.InstrumentToken].LastPrice <= stop : latest[Future.InstrumentToken].LastPrice >= stop)
            || SessionNet is not { } net || net <= -settings.Portfolio.NetSessionPauseLoss) return null;
        if (intent.Origin == "STRATEGY" && (!bars[Future.Id].State.CompleteCoverage
            || Manifest.Weights is null || Manifest.Weights.Members.Any(member => !bars.TryGetValue(member.InstrumentId, out var bank)
                || !bank.State.CompleteCoverage) || strategy.Setup is not { } setup || now >= setup.ExpiresAtUtc)) return null;
        var model = Model(intent.Contract, now, intent.UnderlyingRisk, intent.StructuralStop);
        if (model is null || model.FavorableNetProfit < profile.Risk.MinimumScenarioNetProfit) return null;
        acceptedModels[intent.Id + ":prefill"] = model;
        return new(model.NetRisk, Manifest.Fees);
    }

    private void Monitor(DateTimeOffset now)
    {
        if (SessionNet <= -settings.Portfolio.NetSessionPauseLoss) { LossPaused = true; AutoEnabled = false; rearmPending = false; broker.RequestExit(now, LastSequence, "PORTFOLIO_SESSION_PAUSE"); }
        if (Paper.Position is not { } open) { positionPolicy = new(); LastPositionDecision = null; return; }
        if (now >= session.LocalMidnightUtc.AddDays(1))
        { LastPositionDecision = null; broker.RequestExit(now, LastSequence, "PRIOR_SESSION_UNRESOLVED_EXIT"); return; }
        var input = Build(now, includeCandidates: false); var contract = Contract(open.Approval.Contract);
        var position = new PaperPositionEvidence(open.Approval.Id, "BANKNIFTY", session.Id, open.Approval.Id + ":entry", contract,
            contract.Right == OptionRight.Call ? TradeDirection.Bullish : TradeDirection.Bearish, open.EnteredAtUtc, open.EntryPrice, open.Units,
            open.EntryFees.Total, settings.Version, profile, Future.Id, open.Approval.StructuralStop, open.Approval.UnderlyingRisk,
            open.Approval.UnderlyingEntry, contract.TickSize);
        var optionQuote = Quote(open.Approval.Contract); var mark = NetMark(now);
        NetLiquidationEvidence? liquidation = null;
        if (mark is { } net && optionQuote is not null)
        {
            var exit = IndicativePlanning.ExecutablePrice(optionQuote.Bid, contract.TickSize, contract.TickSize, false)!.Value;
            var exitFees = HistoricalResearchCalculations.Fees(exit, open.Units, false, open.Approval.Fees).Total;
            liquidation = new(optionQuote.Id + ":net", position.Id, position.EntryFillVersion, optionQuote.Id, now, now.AddMilliseconds(250),
                ExperimentalOptionScenarios.Version, open.Approval.Fees.Version,
                IndicativePlanning.LongGrossPnl(open.EntryPrice, exit, open.Units)!.Value, open.EntryFees.Total, exitFees, net);
        }
        UnderlyingProgressEvidence? progress = null;
        if (input.FuturesPrice is { Price: > 0 } underlying && IndicativePlanning.UnderlyingProgressR(open.Approval.UnderlyingEntry,
            underlying.Price.Value, open.Approval.UnderlyingRisk, contract.Right == OptionRight.Call) is { } r)
            progress = new(underlying.Id + ":progress", position.Id, underlying.Id, now, ExperimentalOptionScenarios.Version,
                open.Approval.UnderlyingRisk, open.Approval.UnderlyingEntry, r);
        var decision = engine.EvaluatePosition(new(Identity, now, session, position, optionQuote, liquidation, input.FuturesPrice, progress,
            input.FuturesBars, input.FuturesFeatures, input.ConstituentDefinitions, input.Constituents, input.Portfolio), positionPolicy);
        positionPolicy = decision.State; LastPositionDecision = decision;
        if (decision.Action == PositionAction.EXIT) broker.RequestExit(now, LastSequence, decision.ReasonCodes.FirstOrDefault() ?? "POLICY_EXIT");
    }

    public static string Key(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
