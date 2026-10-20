using System.Text.Json;
using Trading.Analytics;
using Trading.Data;
using Trading.Domain;
using Trading.Engine;
using Trading.Risk;

namespace Trading.Replay;

public sealed partial class CandleReplaySession : IPaperReplaySession
{
    private readonly HistoricalDataset dataset;
    private readonly ReplayConfiguration settings;
    private readonly EngineConfiguration engineSettings;
    private readonly CandleExecutionPreset preset;
    private readonly TradingDecisionEngine engine;
    private readonly EngineSession session;
    private readonly SortedDictionary<DateTimeOffset, ReplayBatch> batches;
    private readonly Dictionary<string, HistoricalInstrument> instruments;
    private readonly Dictionary<string, List<HistoricalCandle>> available = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HistoricalSessionFeatureAccumulator> accumulators = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SessionFeature>> features = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CandleOpening> knownOpenings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StrategyState> strategyStates = new(StringComparer.Ordinal);
    private readonly List<ReplayFeatureTrace> featureTrace = [];
    private readonly List<ReplayDecisionTrace> decisionTrace = [];
    private readonly List<ResearchTrade> trades = [];
    private readonly List<ResearchEquityPoint> equity = [];
    private readonly List<AcceptedEntryAttempt> attempts = [];
    private readonly List<PendingResearchEntry> pending = [];
    private readonly List<OpenResearchPosition> positions = [];
    private ResearchCashLedger ledger;
    private string evidenceVersion = "EMPTY";
    private readonly string codeVersion;
    private bool lossPaused;
    private bool paperEntriesPaused;
    private int nextBatch;
    private readonly HistoricalReadiness readiness;
    private readonly Dictionary<DateTimeOffset, IndexStrategyConfiguration[]> decisionTimes;
    private readonly List<HistoricalDecisionReadiness> readinessTrace = [];

    internal CandleReplaySession(HistoricalDataset dataset, ReplayConfiguration settings, EngineConfiguration engineSettings,
        CandleExecutionPreset preset, DateOnly date)
    {
        this.dataset = dataset; this.settings = settings; this.engineSettings = engineSettings; this.preset = preset;
        engine = new(engineSettings);
        codeVersion = ReplayRunner.Key(typeof(TradingDecisionEngine).Assembly.ManifestModule.ModuleVersionId.ToString(),
            typeof(CandleReplaySession).Assembly.ManifestModule.ModuleVersionId.ToString(),
            typeof(HistoricalSessionAnalytics).Assembly.ManifestModule.ModuleVersionId.ToString(),
            typeof(HistoricalResearchCalculations).Assembly.ManifestModule.ModuleVersionId.ToString(),
            HistoricalSessionAnalytics.Version, HistoricalResearchCalculations.Version);
        instruments = dataset.Manifest.Instruments.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(dataset.Manifest.Indices[0].TimeZone);
        session = new(date.ToString("yyyy-MM-dd"), date,
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), timezone)));
        batches = new(ReplaySchedule.Build(dataset, session, settings.PublicationLag).ToDictionary(batch => batch.TimeUtc));
        readiness = new(dataset, settings, engineSettings);
        decisionTimes = engineSettings.Strategies.Where(profile => profile.Enabled && dataset.Manifest.Indices.Any(index => index.Id == profile.IndexId))
            .SelectMany(profile => readiness.DecisionTimes(date, profile).Select(time => (time, profile)))
            .GroupBy(item => item.time).ToDictionary(group => group.Key, group => group.Select(item => item.profile).ToArray());
        foreach (var time in decisionTimes.Keys) AddTimer(time);
        foreach (var profile in engineSettings.Strategies)
        {
            AddTimer(session.At(profile.EntryStart)); AddTimer(session.At(profile.EntryEnd)); AddTimer(session.At(profile.SessionExit));
        }
        foreach (var definition in dataset.Manifest.Instruments)
        { AddTimer(definition.AvailableAtUtc); AddTimer(definition.ValidFromUtc); AddTimer(definition.ValidToUtc); }
        foreach (var weights in dataset.Manifest.Weights)
        { AddTimer(weights.AvailableAtUtc); AddTimer(weights.EffectiveFromUtc); if (weights.EffectiveToUtc is { } end) AddTimer(end); }
        foreach (var schedule in settings.FeeSchedules)
        { AddTimer(schedule.AvailableAtUtc); AddTimer(schedule.EffectiveFromUtc); if (schedule.EffectiveToUtc is { } end) AddTimer(end); }
        ledger = new(engineSettings.PaperSimulation.InitialCapital, engineSettings.PaperSimulation.InitialCapital);
        foreach (var instrument in instruments.Values)
        {
            available[instrument.Id] = [];
            features[instrument.Id] = [];
            if (instrument.Role is HistoricalInstrumentRole.Futures or HistoricalInstrumentRole.Constituent)
                accumulators[instrument.Id] = new(session.At(dataset.Manifest.MarketOpen), Profile(instrument.IndexId)?.Risk.StructuralLookbackBars ?? 14);
        }
    }

    public bool IsPaused { get; private set; }
    public bool IsComplete => batches.Count == 0;
    public DateTimeOffset? CurrentTimeUtc { get; private set; }
    public DateTimeOffset? NextTimeUtc => IsComplete ? null : batches.First().Key;
    public IReadOnlyList<ResearchTrade> VisibleTrades => Array.AsReadOnly(trades.ToArray());
    public bool PaperEntriesPaused => paperEntriesPaused;
    public void SetPaperEntriesPaused(bool paused) => paperEntriesPaused = paused;
    public PaperStateView PaperState => new(ledger.InitialCapital, ledger.Cash,
        HistoricalResearchCalculations.AvailableCash(ledger, pending.Select(intent => intent.ReservedCash)),
        HistoricalResearchCalculations.ReservedCash(pending.Select(intent => intent.ReservedCash)), ledger.RealizedNetPnl, ledger.PaidFees,
        paperEntriesPaused || lossPaused, attempts.Count,
        pending.Select(intent => new PaperIntentView(intent.Id, intent.Instrument.Id, intent.Setup.Id, intent.AcceptedAtUtc,
            intent.AcceptedAtUtc.Add(preset.PendingEntryTimeout), intent.ReservedCash)).ToArray(),
        positions.Select(state => new PaperPositionView(state.Position.Id, state.Position.Contract.Id, state.Position.Units,
            state.Position.EnteredAtUtc, state.Position.EntryPrice, state.GrossMark, state.NetMark,
             state.PolicyState.ExitLatched ? "EXIT" : state.LastAction, state.LastReasons, state.PendingExitReason,
             trades[state.TradeIndex].ProfitExit, state.Position.FrozenStrategy.Position.ProfitExit.Mode switch
             { ProfitExitMode.FixedNetTarget => state.Position.FrozenStrategy.Position.ProfitExit.NetTarget,
                 ProfitExitMode.TrailingNetProfit => state.PolicyState.ProfitTrailThreshold, _ => null })).ToArray());
    public IReadOnlyList<ReplayConstituentView> VisibleConstituents(string indexId)
    {
            if (CurrentTimeUtc is not { } now) return [];
            var weights = dataset.Manifest.Weights.Where(weights => weights.IndexId == indexId && weights.AvailableAtUtc <= now
                && weights.EffectiveFromUtc <= now && (weights.EffectiveToUtc is null || now < weights.EffectiveToUtc)).ToArray();
            var members = weights.Length == 1 && weights[0].Verified ? weights[0].Members : [];
            var ids = members.Select(member => member.InstrumentId).Concat(instruments.Values
                .Where(item => item.Role == HistoricalInstrumentRole.Constituent && item.IndexId == indexId).Select(item => item.Id))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            return Array.AsReadOnly(ids.Select(id =>
                {
                    var item = instruments.GetValueOrDefault(id);
                    var feature = item is not null && item.Verified && item.AvailableAtUtc <= now && item.ValidFromUtc <= now && now < item.ValidToUtc
                        ? features[item.Id].LastOrDefault() : null;
                    if (feature is not null && now - feature.Candle.AvailableAtUtc >= TimeSpan.FromMinutes(1)) feature = null;
                    var matching = members.Where(member => member.InstrumentId == id).ToArray();
                    return new ReplayConstituentView(id, matching.Length == 1 ? matching[0].WeightPercent : null,
                        feature?.Candle.Close, feature?.Vwap, feature?.Candle.EndUtc,
                        feature is null ? "Missing" : !feature.CompleteCoverage ? "Incomplete" : "Available");
                }).ToArray());
    }
    public void Pause() => IsPaused = true;
    public void Resume() => IsPaused = false;
    public void RunToEnd() { while (!IsComplete && !IsPaused) Step(); }
    // Hosts with a durable batch journal can release diagnostic history after commit.
    // Policy state, features, trades and the pending event queue are unaffected.
    public void ReleaseCommittedDecisionHistory() => decisionTrace.Clear();

    // Explicit manual stepping works while paused. Only the current batch is exposed.
    public ReplayStep? Step()
    {
        if (IsComplete) return null;
        var batch = batches.First().Value;
        batches.Remove(batch.TimeUtc);
        nextBatch++;
        CurrentTimeUtc = batch.TimeUtc;
        var featureStart = featureTrace.Count; var decisionStart = decisionTrace.Count;
        var currentReadiness = (decisionTimes.GetValueOrDefault(batch.TimeUtc) ?? []).Select(profile => readiness.At(session.TradingDate, profile, batch.TimeUtc)).ToArray();
        readinessTrace.AddRange(currentReadiness);
        foreach (var candle in batch.Completed)
        {
            available[candle.InstrumentId].Add(candle);
            evidenceVersion = ReplayRunner.Key(evidenceVersion, JsonSerializer.Serialize(candle, KiteCandleImporter.JsonOptions));
            if (accumulators.TryGetValue(candle.InstrumentId, out var accumulator))
                features[candle.InstrumentId].Add(accumulator.Add(candle, batch.TimeUtc));
        }
        foreach (var opening in batch.Openings)
        {
            knownOpenings[opening.InstrumentId] = opening;
            evidenceVersion = ReplayRunner.Key(evidenceVersion, JsonSerializer.Serialize(opening, KiteCandleImporter.JsonOptions));
        }
        // Protective model checks precede entry scanning. No opening exposes future high/low/close/volume.
        MonitorPositions(batch);
        RefreshLossPause();
        if (lossPaused) foreach (var position in positions) position.PendingExitReason ??= "PORTFOLIO_SESSION_PAUSE";
        ProcessExistingExits(batch);
        ExpirePendingEntries(batch.TimeUtc);
        var identity = Identity(batch.TimeUtc);
        var research = new List<(IndexDefinition Index, IndexEvaluationInput Input, IndexEvaluationResult Diagnostics,
            IReadOnlyList<CandleCandidateAssessment> Assessments)>();
        foreach (var index in dataset.Manifest.Indices.OrderBy(index => index.Id, StringComparer.Ordinal))
        {
            // Strict diagnostics have no fabricated paper fills, quotes, depth or risk evidence.
            var input = BuildInput(index, identity, DiagnosticPortfolio(batch.TimeUtc));
            var diagnostics = engine.EvaluateAllCandidates(input, strategyStates.GetValueOrDefault(index.Id));
            strategyStates[index.Id] = diagnostics.State;
            if (diagnostics.State.Setup is { } active) AddTimer(active.ExpiresAtUtc);
            Trace(index, input, diagnostics);
            foreach (var candidate in diagnostics.Candidates)
                decisionTrace.Add(new(batch.TimeUtc, index.Id, candidate.ContractId, "STRICT_ENGINE", candidate.Action.ToString(),
                    candidate.SetupId, candidate.ReasonCodes, candidate.Gates, candidate.Rank));
            if (settings.EnableCandleResearch)
            {
                var researchInput = input with { Portfolio = Portfolio(batch.TimeUtc) };
                var financials = Financials(researchInput, diagnostics);
                var assessments = engine.AssessCandleResearch(researchInput, diagnostics, financials);
                var rankingInputs = financials.ToDictionary(item => item.ContractId, StringComparer.Ordinal);
                research.Add((index, researchInput, diagnostics, assessments));
                foreach (var candidate in assessments)
                    decisionTrace.Add(new(batch.TimeUtc, index.Id, candidate.ContractId, "CANDLE_RESEARCH",
                        candidate.HypotheticallyEligible ? "HYPOTHETICAL_ELIGIBLE" : "REJECTED", candidate.SetupId,
                        candidate.ReasonCodes, candidate.Gates, candidate.Rank, rankingInputs.TryGetValue(candidate.ContractId, out var metrics)
                            ? new(metrics.SpreadPercent, metrics.Friction, metrics.StrikeDistance) : null));
            }
        }
        // Keep qualification visible even in a bounded recent-decision snapshot of a large universe.
        foreach (var status in currentReadiness)
            decisionTrace.Add(new(batch.TimeUtc, status.IndexId, "", "DATA_READINESS", status.Qualified ? "DATA_READY" : "DATA_EVIDENCE_INCOMPLETE",
                null, status.Issues, status.Options.Where(option => option.State == "Unknown")
                    .Select(option => new GateResult(option.Reason, GateOutcome.UNKNOWN, option.InstrumentId))
                    .Concat((status.RequiredInputs ?? []).Where(item => item.State != "Complete")
                        .Select(item => new GateResult(item.Reason, GateOutcome.UNKNOWN, item.InstrumentId)))
                    .Concat(status.Issues.Select(code => new GateResult(code, GateOutcome.UNKNOWN, "Historical decision evidence is incomplete."))).ToArray()));
        AcceptResearchEntries(research, batch.TimeUtc);
        ProcessEntryOpenings(batch);
        RefreshLossPause();
        if (lossPaused)
        {
            foreach (var position in positions) position.PendingExitReason ??= "PORTFOLIO_SESSION_PAUSE";
        }
        ProcessExistingExits(batch); // also handles an immediate spread-loss stop after entry
        equity.Add(new(batch.TimeUtc, NetSessionPnl()));
        return new(batch.TimeUtc, available.Values.Sum(list => list.Count),
            Array.AsReadOnly(featureTrace.Skip(featureStart).ToArray()), Array.AsReadOnly(decisionTrace.Skip(decisionStart).ToArray()),
            positions.Count, pending.Count, NetSessionPnl(), currentReadiness);
    }

    public ReplaySessionResult Result
    {
        get
        {
            var issues = new List<string>();
            if (!IsComplete) issues.Add("REPLAY_NOT_FINISHED");
            if (readinessTrace.Count == 0) issues.Add("NO_ENABLED_STRATEGY_DECISIONS");
            if (!settings.EnableCandleResearch) issues.Add("CANDLE_RESEARCH_DISABLED");
            issues.AddRange(readinessTrace.SelectMany(status => status.Issues).Distinct(StringComparer.Ordinal));
            if (IsComplete) issues.AddRange(readiness.SessionIssues(session.TradingDate));
            if (positions.Count != 0) issues.Add("UNRESOLVED_POSITIONS");
            if (pending.Count != 0) issues.Add("UNRESOLVED_ENTRY_INTENTS");
            var tradedIds = trades.Select(trade => trade.ContractId).ToHashSet(StringComparer.Ordinal);
            if (dataset.Issues.Any(issue => issue.Code is not ("NO_OPTION_CANDLES" or "SPARSE_OPTION_CANDLES" or "OPTION_FILE_UNAVAILABLE")
                && (instruments.GetValueOrDefault(issue.InstrumentId)?.Role is HistoricalInstrumentRole.Futures
                    || tradedIds.Contains(issue.InstrumentId) && (issue.StartUtc is null || trades.Any(trade => trade.ContractId == issue.InstrumentId
                        && issue.StartUtc >= trade.EntryTimeUtc && (trade.ExitIntervalEndUtc is null || issue.StartUtc < trade.ExitIntervalEndUtc))))
                && (issue.StartUtc is null || issue.StartUtc >= session.LocalMidnightUtc && issue.StartUtc < session.LocalMidnightUtc.AddDays(1)))) issues.Add("REQUIRED_OR_TRADED_SERIES_QUALITY_GAPS");
            if (dataset.Manifest.Instruments.Any(instrument => !instrument.Verified && (tradedIds.Contains(instrument.Id)
                || instrument.Role is HistoricalInstrumentRole.Futures))) issues.Add("UNVERIFIED_REQUIRED_CONTRACT_METADATA");
            if (settings.EnableCandleResearch && engineSettings.Strategies.Any(profile => profile.Enabled
                && dataset.Manifest.Indices.Any(index => index.Id == profile.IndexId)
                && FeeSchedule(profile, session.At(profile.EntryStart)) is null)) issues.Add("FEES_UNAVAILABLE");
            return new(dataset.Manifest.DatasetId, session.TradingDate, preset.Id,
                Array.AsReadOnly(featureTrace.ToArray()), Array.AsReadOnly(decisionTrace.ToArray()), Array.AsReadOnly(trades.ToArray()),
                Array.AsReadOnly(equity.ToArray()), ledger.RealizedNetPnl, ledger.PaidFees,
                HistoricalResearchCalculations.ObservedDrawdown(equity.Select(point => point.NetSessionPnl)),
                issues.Count == 0, issues.AsReadOnly(), pending.Count, positions.Count(position => position.PendingExitReason is not null), readinessTrace.ToArray());
        }
    }

    private IndexStrategyConfiguration? Profile(string indexId) => engineSettings.Strategies.SingleOrDefault(profile => profile.IndexId == indexId);
    private void AddTimer(DateTimeOffset time)
    {
        if (time >= session.At(dataset.Manifest.MarketOpen)
            && time <= session.At(dataset.Manifest.MarketClose).Add(settings.PublicationLag)
            && (CurrentTimeUtc is null || time > CurrentTimeUtc) && !batches.ContainsKey(time))
            batches[time] = new(time, [], []);
    }
    private ResearchFeeSchedule? FeeSchedule(IndexStrategyConfiguration profile, DateTimeOffset time) => settings.FeeSchedules
        .SingleOrDefault(schedule => schedule.Applies(time, profile.Risk.FeeProfileId, dataset.Manifest.IsSynthetic));
    private EvaluationIdentity Identity(DateTimeOffset time) => new(
        $"{settings.RunId}:{dataset.Manifest.DatasetId}:{session.Id}:{preset.Id}",
        ReplayRunner.Key(time.ToString("O"), evidenceVersion), codeVersion,
        dataset.Manifest.IsSynthetic ? EngineDataMode.SYNTHETIC_TEST : EngineDataMode.PAPER_REPLAY);
    private PortfolioEvidence DiagnosticPortfolio(DateTimeOffset time) => new("diagnostics-empty", session.Id, time,
        ledger.InitialCapital, 0m, false, false, true, true, [], []);
    private PortfolioEvidence Portfolio(DateTimeOffset time) => new($"research-ledger-{nextBatch}-{attempts.Count}-{trades.Count}",
        session.Id, time, HistoricalResearchCalculations.AvailableCash(ledger, pending.Select(intent => intent.ReservedCash)),
        NetSessionPnl(), lossPaused, paperEntriesPaused, true, true,
        positions.Select(position => new ActivePortfolioSlot(position.Position.Id, position.Position.IndexId, EntryOrigin.STRATEGY))
            .Concat(pending.Select(intent => new ActivePortfolioSlot(intent.Id, intent.IndexId, EntryOrigin.STRATEGY))).ToArray(), attempts.ToArray());
    private static CompletedBarEvidence Bar(HistoricalCandle bar, string sessionId) => new(bar.Id, bar.InstrumentId, sessionId,
        bar.StartUtc, bar.EndUtc, bar.AvailableAtUtc, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
    private HistoricalCandle? CurrentCandle(string instrumentId, DateTimeOffset time) => available[instrumentId].LastOrDefault(bar =>
        bar.AvailableAtUtc <= time && time - bar.AvailableAtUtc < TimeSpan.FromMinutes(1)
        && time - bar.EndUtc < TimeSpan.FromMinutes(1));
    private OptionContractEvidence Contract(HistoricalInstrument instrument) => new(instrument.Id, instrument.IndexId, instrument.Exchange,
        instrument.Role == HistoricalInstrumentRole.Call ? OptionRight.Call : OptionRight.Put,
        instrument.Expiry ?? DateOnly.MinValue, instrument.Strike ?? 0m, instrument.LotSize ?? 0, instrument.TickSize ?? 0m,
        instrument.ValidFromUtc, instrument.ValidToUtc,
        instrument.Verified ? instrument.AvailableAtUtc : DateTimeOffset.MaxValue, instrument.DefinitionVersion, dataset.Manifest.IsSynthetic);

    private IndexEvaluationInput BuildInput(IndexDefinition index, EvaluationIdentity identity, PortfolioEvidence portfolio)
    {
        var identityTime = CurrentTimeUtc!.Value;
        var future = instruments.Values.Single(item => item.IndexId == index.Id && item.Role == HistoricalInstrumentRole.Futures);
        var profile = Profile(index.Id);
        var futureBars = available[future.Id].Select(bar => Bar(bar, session.Id)).ToArray();
        var futureFeatures = features[future.Id].Select(feature =>
        {
            var structural = feature.PreviousMeanTrueRange is { } mean && profile is not null
                ? HistoricalResearchCalculations.Structural(feature.Candle.Low, feature.Candle.High, feature.Candle.Close, mean, profile.Risk.StructuralBufferMultiplier)
                : ((decimal LongStop, decimal LongRisk, decimal ShortStop, decimal ShortRisk)?)null;
            return new FuturesFeatureEvidence($"{feature.Candle.Id}:features", feature.Candle.Id, session.Id,
                engineSettings.Version, feature.Candle.AvailableAtUtc, feature.InputBarIds, feature.Vwap,
                profile?.Risk.StructuralLookbackBars ?? 14, profile?.Risk.StructuralBufferMultiplier ?? 0m,
                structural?.LongStop, structural?.LongRisk, structural?.ShortStop, structural?.ShortRisk,
                HistoricalSessionAnalytics.Version + "+" + HistoricalResearchCalculations.Version, feature.CompleteCoverage);
        }).ToArray();
        var weights = dataset.Manifest.Weights.Where(weights => weights.IndexId == index.Id && weights.AvailableAtUtc <= identityTime
            && weights.EffectiveFromUtc <= identityTime && (weights.EffectiveToUtc is null || identityTime < weights.EffectiveToUtc)).ToArray();
        var weight = weights.Length == 1 ? weights[0] : null;
        var requiredMembers = weight?.Members.Select(member => member.InstrumentId).ToHashSet(StringComparer.Ordinal) ?? [];
        var constituentFrames = instruments.Values.Where(item => item.Role == HistoricalInstrumentRole.Constituent && requiredMembers.Contains(item.Id))
            .Where(item => item.Verified && item.AvailableAtUtc <= identityTime && !string.IsNullOrWhiteSpace(item.DefinitionVersion))
            .SelectMany(item => features[item.Id].Where(feature => item.ValidFromUtc <= feature.Candle.EndUtc && feature.Candle.EndUtc < item.ValidToUtc))
            .Select(feature => new ConstituentBarEvidence(feature.Candle.InstrumentId,
                session.Id, feature.Candle.StartUtc, feature.Candle.EndUtc, feature.Candle.AvailableAtUtc,
                feature.Candle.Close, feature.Vwap, feature.CompleteCoverage, HistoricalSessionAnalytics.Version)).ToArray();
        return new(identity, identityTime, index, session, future.Id, futureBars, futureFeatures,
            weight is null ? null : new(weight.Version, weight.EffectiveFromUtc, weight.EffectiveToUtc, weight.AvailableAtUtc,
                weight.Verified && profile is not null && readiness.WeightIssues(session.TradingDate, profile, identityTime).Count == 0,
                weight.Members.Select(member => new ConstituentMember(member.InstrumentId, member.WeightPercent)).ToArray()),
            constituentFrames, null, dataset.Manifest.UniverseVersion, dataset.Manifest.UniverseAvailableAtUtc,
            instruments.Values.Where(item => item.IndexId == index.Id && item.Role is HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put)
                .Where(item => item.AvailableAtUtc <= identityTime && item.ValidFromUtc <= identityTime && identityTime < item.ValidToUtc)
                .OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => new OptionCandidateEvidence(Contract(item), null, null)).ToArray(),
            portfolio, FuturesContract: future.Verified ? new(future.Id, future.IndexId, future.ValidFromUtc, future.ValidToUtc,
                future.AvailableAtUtc, future.DefinitionVersion) : null);

    }

    private IReadOnlyList<CandleCandidateFinancialEvidence> Financials(IndexEvaluationInput input, IndexEvaluationResult diagnostics)
    {
        var setup = diagnostics.State.Setup; var profile = Profile(input.Index.Id);
        if (setup is null || profile is null) return [];
        var signal = input.FuturesBars.Single(bar => bar.Id == setup.SignalBarId);
        var schedule = FeeSchedule(profile, input.AsOfUtc);
        if (schedule is null) return [];
        var financials = new List<CandleCandidateFinancialEvidence>();
        foreach (var candidate in input.Candidates)
        {
            var candle = CurrentCandle(candidate.Contract.Id, input.AsOfUtc);
            if (candle is null || candle.Volume <= 0m || candidate.Contract.LotSize <= 0 || candidate.Contract.TickSize <= 0m) continue;
            try
            {
                var units = IndicativePlanning.WholeLotUnits(profile.Lots, candidate.Contract.LotSize);
                var plan = HistoricalResearchCalculations.Plan(candle.Close, candidate.Contract.Strike, signal.Close, units,
                    candidate.Contract.TickSize, preset.FullSpreadTicks, preset.AdverseSlippageTicks,
                    profile.Risk.GrossCashStop, preset.GapStressRupees, schedule);
                if (plan is null) continue;
                financials.Add(new(candidate.Contract.Id, candidate.Contract.DefinitionVersion, Bar(candle, session.Id), signal.Id,
                    input.AsOfUtc, HistoricalResearchCalculations.Version, schedule.ProfileId, schedule.Version, true,
                    plan.RequiredCash, plan.ModelledNetRisk, plan.SpreadPercent, plan.RoundTripFriction, plan.StrikeDistance));
            }
            catch (OverflowException) { /* Engine quantity gate retains the failure. */ }
        }
        return financials.AsReadOnly();
    }

    private void Trace(IndexDefinition index, IndexEvaluationInput input, IndexEvaluationResult result)
    {
        var latest = input.FuturesFeatures.LastOrDefault();
        var directional = result.DirectionalEvidence;
        featureTrace.Add(new(input.AsOfUtc, index.Id, directional?.BarId, directional?.OpeningHigh, directional?.OpeningLow,
            directional?.Vwap, latest?.LongStructuralStop, latest?.ShortStructuralStop,
            directional?.AboveWeightPercent, directional?.BelowWeightPercent, directional?.MissingWeightPercent,
            result.State.Setup?.Id, result.State.Setup?.Direction, result.ContextGates, directional,
            decisionTimes.GetValueOrDefault(input.AsOfUtc)?.Any(profile => profile.IndexId == index.Id) == true));
    }
}
