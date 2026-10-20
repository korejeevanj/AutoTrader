using Trading.Analytics;
using Trading.Domain;
using Trading.Risk;

namespace Trading.Engine;

public sealed partial class TradingDecisionEngine
{
    private readonly EngineConfiguration configuration;
    public string ConfigurationFingerprint { get; }

    public TradingDecisionEngine(EngineConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.ThrowIfInvalid();
        this.configuration = configuration.Freeze();
        ConfigurationFingerprint = EvidenceChecks.Key(this.configuration.ToJson());
    }

    public IndexEvaluationResult EvaluateIndex(IndexEvaluationInput input, StrategyState? previousState = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        EvidenceChecks.ValidateContext(input.Identity, input.AsOfUtc, input.Session);
        var gates = new GateBook();
        var strategy = configuration.Strategies.SingleOrDefault(profile => profile.IndexId == input.Index.Id);
        var state = previousState ?? new StrategyState();
        if (state.LastEvaluationUtc > input.AsOfUtc || state.LastProcessedBarEndUtc > input.AsOfUtc
            || state.Setup?.SignalEndUtc > input.AsOfUtc)
            throw new ArgumentException("Future strategy state cannot be used at an earlier evaluation time.", nameof(previousState));
        if (strategy is null)
        {
            gates.Missing("STRATEGY_NOT_CONFIGURED", "Index definitions do not implicitly authorize a strategy.");
            return Unconfigured(input, state, gates);
        }
        gates.Require(strategy.Enabled, "STRATEGY_ENABLED", "An explicit enabled index strategy is required.");
        gates.Require(input.Index.EffectiveFromUtc <= input.AsOfUtc
            && (input.Index.EffectiveToUtc is null || input.Index.EffectiveToUtc > input.AsOfUtc),
            "INDEX_VALIDITY", "Index definition must be effective at decision time.");
        gates.Require(input.Identity.Mode == EngineDataMode.SYNTHETIC_TEST || !input.Index.IsSynthetic,
            "DATA_MODE", "Synthetic definitions cannot masquerade as live/replay market contracts.");
        EvidenceChecks.Portfolio(gates, input.Portfolio, input.Session, input.AsOfUtc, configuration.Portfolio, input.Origin);
        if (EvidenceChecks.PortfolioUsable(input.Portfolio, input.Session, input.AsOfUtc))
            CheckCapacity(gates, input.Portfolio, input.Index.Id, input.Origin);
        CheckFuturesContract(input, gates);
        if (input.UniverseAvailableAtUtc > input.AsOfUtc || string.IsNullOrWhiteSpace(input.UniverseVersion))
            gates.Missing("UNIVERSE_UNAVAILABLE", "Use a frozen point-in-time universe with a version.");
        gates.Require(input.Candidates.Count <= strategy.MaximumCandidates, "BOUNDED_UNIVERSE", "Host must bound the universe before evaluation.", input.Candidates.Count, strategy.MaximumCandidates);
        gates.Require(input.Candidates.Select(candidate => candidate.Contract.Id).Distinct(StringComparer.Ordinal).Count() == input.Candidates.Count,
            "UNIQUE_CANDIDATES", "Conflicting duplicate candidate identities are not ranked.");

        if (state.IndexId is not null && (state.IndexId != input.Index.Id || state.SessionId != input.Session.Id
            || state.ConfigurationVersion != configuration.Version || state.ConfigurationFingerprint != ConfigurationFingerprint))
            throw new ArgumentException("Strategy state belongs to another index, session or configuration.", nameof(previousState));
        state = state with { IndexId = input.Index.Id, SessionId = input.Session.Id,
            ConfigurationVersion = configuration.Version, ConfigurationFingerprint = ConfigurationFingerprint,
            LastEvaluationUtc = input.AsOfUtc };

        var bars = input.Origin == EntryOrigin.STRATEGY
            ? EvidenceChecks.Bars(input.FuturesBars, input.FuturesInstrumentId, input.Session, input.AsOfUtc, strategy, gates) : [];
        ActiveSetup? setup = null;
        ChartContext? chart = null;
        gates.Require(input.AsOfUtc >= input.Session.At(strategy.EntryStart) && input.AsOfUtc < input.Session.At(strategy.EntryEnd),
            "ENTRY_WINDOW", "Strategy and manual entries respect the half-open configured entry window.");
        if (input.Origin == EntryOrigin.STRATEGY)
        {
            chart = DashboardAnalytics.CompletedFuturesChart(input.FuturesInstrumentId,
                bars.Select(bar => new FixtureBar(bar.StartUtc, bar.EndUtc, bar.AvailableAtUtc,
                    bar.Open, bar.High, bar.Low, bar.Close, bar.Volume)), input.AsOfUtc,
                input.Session.At(strategy.OpeningStart), input.Session.At(strategy.OpeningEnd));
            if (chart.OpeningRangeHigh is null || chart.OpeningRangeLow is null)
                gates.Missing("OPENING_RANGE_INCOMPLETE", "Every opening-range minute must be completed and available.");
            else if (!gates.Unknown)
                state = AdvanceSetup(input, strategy, state, bars, chart.OpeningRangeHigh.Value, chart.OpeningRangeLow.Value, gates);
            setup = state.Setup;
            if (setup is not null && input.AsOfUtc >= setup.ExpiresAtUtc)
            {
                state = state with { Setup = null };
                setup = null;
            }
            if (setup is null) gates.Add("NO_ACTIVE_SETUP", GateOutcome.NOT_EVALUATED, "Await a newly armed crossing; no financial risk is inferred.");
            else
            {
                var signal = bars.SingleOrDefault(bar => bar.Id == setup.SignalBarId);
                if (signal is null) gates.Missing("SIGNAL_EVIDENCE_MISSING", "The frozen signal bar must remain in supplied evidence.");
                else
                {
                    var participation = EvidenceChecks.Confirmation(input.ConstituentDefinitions, input.Constituents,
                        signal, input.AsOfUtc, strategy, gates);
                    if (participation is not null)
                        EvidenceChecks.DirectionalConfirmation(participation, setup.Direction, strategy, gates);
                    var features = EvidenceChecks.Feature(input.FuturesFeatures, signal, bars, configuration.Version, input.AsOfUtc, gates);
                    if (features is not null) CheckStructuralEvidence(features, signal, setup.Direction, strategy, gates);
                }
                gates.Require(!input.Portfolio.AcceptedAttempts.Any(attempt => attempt.SetupId == setup.Id),
                    "SETUP_NOT_CONSUMED", "One accepted intent consumes this setup even if it never fills.");
            }
        }
        else
        {
            gates.Add("MANUAL_SETUP_BYPASS", GateOutcome.NOT_EVALUATED, "MANUAL_TEST bypasses setup detection only.");
            gates.Require(!string.IsNullOrWhiteSpace(input.ManualRequestId) && !string.IsNullOrWhiteSpace(input.ManualContractId),
                "MANUAL_IDENTITY", "Host supplies stable manual request and selected contract identities.");
            gates.Require(input.Candidates.Any(candidate => candidate.Contract.Id == input.ManualContractId),
                "MANUAL_CONTRACT", "Manual selection must belong to the supplied valid universe.");
        }

        var directionalEvidence = DirectionalSnapshot(input, strategy, state, bars, chart);
        gates.Append(directionalEvidence.Gates.Where(gate => gate.Code == "CONSTITUENT_BREADTH"));
        var assessments = input.Candidates.OrderBy(candidate => candidate.Contract.Id, StringComparer.Ordinal)
            .Select(candidate => AssessCandidate(input, strategy, setup, candidate, gates)).ToArray();
        var ranked = Rank(assessments.Where(candidate => candidate.Eligible), strategy).ToArray();
        var ranks = ranked.Select((candidate, i) => (candidate.ContractId, Rank: i + 1))
            .ToDictionary(item => item.ContractId, item => item.Rank, StringComparer.Ordinal);
        assessments = assessments.Select(candidate => ranks.TryGetValue(candidate.ContractId, out var rank)
            ? candidate with { Rank = rank } : candidate).ToArray();
        return new(state, Array.AsReadOnly(assessments), gates.Snapshot(), directionalEvidence);
    }

    private StrategyState AdvanceSetup(IndexEvaluationInput input, IndexStrategyConfiguration strategy,
        StrategyState state, CompletedBarEvidence[] bars, decimal high, decimal low, GateBook gates)
    {
        foreach (var bar in bars.Where(bar => bar.StartUtc >= input.Session.At(strategy.OpeningEnd)
            && (state.LastProcessedBarEndUtc is null || bar.EndUtc > state.LastProcessedBarEndUtc)))
        {
            var feature = EvidenceChecks.Feature(input.FuturesFeatures, bar, bars, configuration.Version, input.AsOfUtc, gates);
            if (feature is null) break;
            state = state with { Setup = null, LastProcessedBarEndUtc = bar.EndUtc };
            var inside = bar.Close >= low && bar.Close <= high;
            if (inside) { state = state with { Armed = true }; continue; }
            var previous = bars.SingleOrDefault(previous => previous.EndUtc == bar.StartUtc);
            var crossed = previous is not null && previous.Close >= low && previous.Close <= high;
            var bullish = bar.Close > high && bar.Close > feature.Vwap;
            var bearish = bar.Close < low && bar.Close < feature.Vwap;
            if (state.Armed && crossed && (bullish || bearish))
            {
                var direction = bullish ? TradeDirection.Bullish : TradeDirection.Bearish;
                var expiry = bar.EndUtc.Add(strategy.SetupLifetime);
                if (expiry > input.Session.At(strategy.EntryEnd)) expiry = input.Session.At(strategy.EntryEnd);
                state = state with { Armed = false, Setup = new(
                    EvidenceChecks.Key(input.Index.Id, input.Session.Id, input.FuturesInstrumentId,
                        "STRATEGY_CROSSING", bar.EndUtc.ToString("O"), direction.ToString()),
                    bar.Id, direction, bar.EndUtc, expiry) };
            }
        }
        return state;
    }

    private static void CheckStructuralEvidence(FuturesFeatureEvidence feature, CompletedBarEvidence signal, TradeDirection direction,
        IndexStrategyConfiguration strategy, GateBook gates)
    {
        var stop = direction == TradeDirection.Bullish ? feature.LongStructuralStop : feature.ShortStructuralStop;
        var risk = direction == TradeDirection.Bullish ? feature.LongInitialRisk : feature.ShortInitialRisk;
        if (feature.StructuralLookbackBars != strategy.Risk.StructuralLookbackBars
            || feature.StructuralBufferMultiplier != strategy.Risk.StructuralBufferMultiplier
            || feature.InputBarIds.Count <= strategy.Risk.StructuralLookbackBars || stop is null or <= 0m || risk is null or <= 0m
            || (direction == TradeDirection.Bullish ? stop > signal.Low : stop < signal.High)
            || IndicativePlanning.UnderlyingProgressR(signal.Close, stop.Value, risk.Value, direction == TradeDirection.Bullish) != -1m)
            gates.Missing("STRUCTURAL_RISK_UNKNOWN", "Shared Analytics/Risk must supply the configured opposite-extreme/buffer stop and positive underlying R.");
        else gates.Add("STRUCTURAL_RISK_KNOWN", GateOutcome.PASS, "The structural stop is exactly -1 underlying R from signal close; R is separate from option cash risk.", risk);
    }

    private static void CheckFuturesContract(IndexEvaluationInput input, GateBook gates)
    {
        var contract = input.FuturesContract;
        if (contract is null || contract.AvailableAtUtc > input.AsOfUtc || string.IsNullOrWhiteSpace(contract.DefinitionVersion))
            gates.Missing("FUTURES_CONTRACT_UNAVAILABLE", "Host supplies a point-in-time valid selected futures definition.");
        else gates.Require(contract.Id == input.FuturesInstrumentId && contract.IndexId == input.Index.Id
            && contract.ValidFromUtc <= input.AsOfUtc && input.AsOfUtc < contract.ValidToUtc,
            "FUTURES_CONTRACT_VALIDITY", "Selected futures identity and validity must match this index.");
    }

    private void CheckCapacity(GateBook gates, PortfolioEvidence portfolio, string indexId, EntryOrigin origin)
    {
        gates.Require(portfolio.ActiveOrPendingEntries.Count < configuration.Portfolio.MaximumActiveOrPendingEntries,
            "PORTFOLIO_CAPACITY", "Pending entries reserve portfolio capacity before fills.");
        gates.Require(portfolio.ActiveOrPendingEntries.Count(slot => slot.IndexId == indexId)
            < configuration.Portfolio.MaximumActiveOrPendingEntriesPerIndex, "INDEX_CAPACITY", "One index cannot consume more than its configured capacity.");
        var attempts = portfolio.AcceptedAttempts.DistinctBy(attempt => attempt.IntentId, StringComparer.Ordinal).ToArray();
        if (origin == EntryOrigin.STRATEGY)
        {
            gates.Require(attempts.Count(attempt => attempt.Origin == origin) < configuration.Portfolio.MaximumStrategyAttempts,
                "PORTFOLIO_ATTEMPTS", "Accepted strategy intents, not recommendations, consume attempts.");
            gates.Require(attempts.Count(attempt => attempt.Origin == origin && attempt.IndexId == indexId)
                < configuration.Portfolio.MaximumStrategyAttemptsPerIndex, "INDEX_ATTEMPTS", "Canceled/unfilled accepted intents still count.");
        }
        else gates.Require(attempts.Count(attempt => attempt.Origin == origin) < configuration.Portfolio.MaximumManualAttempts,
            "MANUAL_ATTEMPTS", "Manual diagnostics have a separate allowance but share safety limits.");
    }

    private CandidateAssessment AssessCandidate(IndexEvaluationInput input, IndexStrategyConfiguration strategy,
        ActiveSetup? setup, OptionCandidateEvidence candidate, GateBook context)
    {
        var gates = new GateBook();
        gates.Append(context.Snapshot());
        var contract = candidate.Contract;
        var manual = input.Origin == EntryOrigin.MANUAL_TEST;
        var selectedManual = manual && input.ManualContractId == contract.Id;
        var setupId = manual ? EvidenceChecks.Key(input.Index.Id, input.Session.Id,
            "MANUAL_TEST", input.ManualRequestId ?? "") : setup?.Id ?? "";
        var direction = manual ? contract.Right == OptionRight.Call ? TradeDirection.Bullish : TradeDirection.Bearish : setup?.Direction;
        gates.Require(contract.IndexId == input.Index.Id && Enum.IsDefined(contract.Right)
            && contract.Strike > 0m && contract.LotSize > 0 && contract.TickSize > 0m
            && contract.ValidFromUtc <= input.AsOfUtc && input.AsOfUtc < contract.ValidToUtc
            && contract.Expiry >= input.Session.TradingDate
            && (!strategy.ExcludeExpiryDay || contract.Expiry > input.Session.TradingDate),
            "OPTION_CONTRACT_VALIDITY", "Underlying, expiry policy, validity and positive strike/lot/tick must pass.");
        if (contract.AvailableAtUtc > input.AsOfUtc || string.IsNullOrWhiteSpace(contract.DefinitionVersion))
            gates.Missing("OPTION_DEFINITION_UNAVAILABLE", "No future/unknown contract definition may qualify.");
        gates.Require(input.Identity.Mode == EngineDataMode.SYNTHETIC_TEST || !contract.IsSynthetic,
            "CONTRACT_DATA_MODE", "Live/replay qualification cannot use synthetic contracts.");
        if (manual) gates.Require(selectedManual, "MANUAL_SELECTION", "Only the selected manual contract is evaluated for entry.");
        if (selectedManual) gates.Require(!input.Portfolio.AcceptedAttempts.Any(attempt => attempt.SetupId == setupId),
            "SETUP_NOT_CONSUMED", "A stable manual request cannot be accepted twice, even with remaining manual allowance.");
        if (direction is not null) gates.Require((direction == TradeDirection.Bullish) == (contract.Right == OptionRight.Call),
            "OPTION_DIRECTION", "Bullish evaluates calls; bearish evaluates puts.");

        int? units = null;
        try { units = IndicativePlanning.WholeLotUnits(strategy.Lots, contract.LotSize); gates.Pass("WHOLE_LOT_QUANTITY", "Checked whole-lot units from the supplied definition."); }
        catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException)
        { gates.Fail("WHOLE_LOT_QUANTITY", "Invalid or overflowing whole-lot quantity."); }
        var quote = candidate.Quote;
        if (quote is null || quote.Bid is null || quote.Ask is null || quote.BidAskObservedAtUtc is null
            || quote.EventTimeUtc is null || quote.ReceivedAtUtc is null)
            gates.Missing("OPTION_QUOTE_MISSING", "Observed two-sided prices and independent observation/event/receipt timestamps are required.");
        else
        {
            gates.Require(quote.Bid > 0m && quote.Ask > 0m && quote.Bid <= quote.Ask,
                "UNCROSSED_QUOTE", "Prices must be positive and uncrossed.");
            if (quote.AvailableAtUtc > input.AsOfUtc || quote.BidAskObservedAtUtc > input.AsOfUtc
                || quote.EventTimeUtc > input.AsOfUtc || quote.ReceivedAtUtc > input.AsOfUtc)
                gates.Missing("OPTION_QUOTE_FUTURE", "Future evidence is not yet available.");
            else gates.Require(EvidenceChecks.FreshQuote(quote, contract.Id, input.AsOfUtc, strategy.Evidence),
                "OPTION_QUOTE_FRESH", "Heartbeat/last trade cannot substitute for fresh bid/ask and receipt.");
            if (quote.BidQuantity is null || quote.AskQuantity is null)
                gates.Missing("OBSERVED_DEPTH_MISSING", "Unknown depth is not unlimited liquidity.");
            else if (units is not null)
            {
                try
                {
                    var requiredDepth = IndicativePlanning.WholeLotUnits(checked(strategy.Lots * strategy.Evidence.MinimumDepthLots), contract.LotSize);
                    gates.Require(quote.BidQuantity >= requiredDepth && quote.AskQuantity >= requiredDepth,
                        "WHOLE_LOT_DEPTH", "Both best levels must cover configured whole-lot depth.", Math.Min(quote.BidQuantity.Value, quote.AskQuantity.Value), requiredDepth);
                }
                catch (OverflowException) { gates.Fail("WHOLE_LOT_DEPTH", "Configured whole-lot depth overflows."); }
            }
        }
        if (setup is not null || selectedManual)
        {
            if (!EvidenceChecks.FreshUnderlying(input.FuturesPrice, input.FuturesInstrumentId, input.AsOfUtc, strategy.Evidence))
                gates.Missing("FUTURES_PRICE_FRESHNESS", "A fresh futures trade/receipt is required, not merely a fresh packet.");
            else if (quote?.BidAskObservedAtUtc is { } quoteTime && input.FuturesPrice!.LastTradeTimeUtc is { } futuresTime)
                gates.Require((quoteTime - futuresTime).Duration() <= strategy.Evidence.MaximumQuoteSkew,
                    "QUOTE_SYNCHRONIZATION", "Candidate and underlying times must be within configured skew.");
            CheckFinancialEvidence(input, strategy, setup, candidate, units, gates);
        }
        else gates.Add("RISK_NOT_EVALUATED", GateOutcome.NOT_EVALUATED, "Setup-dependent risk is evaluated only for an active setup/manual selection.");
        var eligible = (setup is not null || selectedManual) && !gates.Failed && !gates.Unknown;
        if (eligible) gates.Pass("ELIGIBLE_PENDING_ALLOCATION", "All gates passed; only portfolio allocation may produce ENTER.");
        var snapshot = gates.Snapshot();
        var reasons = EvidenceChecks.Reasons(snapshot);
        if (reasons.Count == 0) reasons = [eligible ? "ELIGIBLE_PENDING_ALLOCATION" : "NO_ACTIVE_SETUP"];
        return new(EvidenceChecks.Key(input.Identity.RunId, input.Identity.EvidenceVersion, input.AsOfUtc.ToString("O"),
            configuration.Version, ConfigurationFingerprint, input.Portfolio.Version, contract.Id, setupId), setupId, input.Index.Id,
            contract.Id, contract.Expiry, direction, input.Origin, input.AsOfUtc, configuration.Version,
            strategy.Kind.ToString(), input.Identity, input.UniverseVersion, input.Portfolio.Version, input.Session.Id,
            EvidenceChecks.Action(gates), strategy.Lots, units, quote?.Id,
            DashboardAnalytics.AgeSeconds(input.AsOfUtc, quote?.BidAskObservedAtUtc), candidate.Risk,
            snapshot, reasons, eligible, null, ConfigurationFingerprint);
    }

    private void CheckFinancialEvidence(IndexEvaluationInput input, IndexStrategyConfiguration strategy,
        ActiveSetup? setup, OptionCandidateEvidence candidate, int? units, GateBook gates)
    {
        var risk = candidate.Risk;
        var structuralMatches = setup is null ? [] : input.FuturesFeatures.Where(feature =>
            feature.BarId == setup.SignalBarId && feature.AvailableAtUtc <= input.AsOfUtc).ToArray();
        var structuralFeature = structuralMatches.Length == 1 ? structuralMatches[0] : null;
        if (risk is null || risk.AvailableAtUtc > input.AsOfUtc || risk.ValidUntilUtc <= input.AsOfUtc
            || risk.ContractId != candidate.Contract.Id || risk.ContractDefinitionVersion != candidate.Contract.DefinitionVersion
            || risk.QuoteId != candidate.Quote?.Id || risk.Units != units || risk.ConfigurationVersion != configuration.Version
            || risk.SignalBarId != setup?.SignalBarId || risk.UnderlyingQuoteId != input.FuturesPrice?.Id
            || risk.StructuralFeatureId != structuralFeature?.Id
            || (candidate.Quote is not null && risk.AvailableAtUtc < candidate.Quote.AvailableAtUtc)
            || (input.FuturesPrice is not null && risk.AvailableAtUtc < input.FuturesPrice.AvailableAtUtc)
            || (structuralFeature is not null && risk.AvailableAtUtc < structuralFeature.AvailableAtUtc)
            || string.IsNullOrWhiteSpace(risk.Id)
            || string.IsNullOrWhiteSpace(risk.CalculatorVersion))
        { gates.Missing("RISK_ASSESSMENT_UNAVAILABLE", "Require a current shared-Risk assessment bound to exact quote, contract, signal, quantity and configuration."); return; }
        if (risk.FeeProfileId != strategy.Risk.FeeProfileId || string.IsNullOrWhiteSpace(risk.FeeScheduleVersion)
            || risk.FeeEffectiveFromUtc > input.AsOfUtc
            || (risk.FeeEffectiveToUtc is { } until && until <= input.AsOfUtc) || risk.RoundTripFees is null or < 0m)
            gates.Missing("EFFECTIVE_FEES_UNAVAILABLE", "Effective-date fee breakdown/profile must be known; missing fees never become zero.");
        gates.Require(risk.FillModelVersion == configuration.PaperSimulation.Version
            && risk.EntryAdverseSlippage == configuration.PaperSimulation.EntryAdverseSlippage
            && risk.ExitAdverseSlippage == configuration.PaperSimulation.ExitAdverseSlippage
            && risk.AdverseTickRounding && risk.AssumedEntryPrice is > 0m
            && risk.AssumedEntryPrice == IndicativePlanning.ExecutablePrice(candidate.Quote?.Ask, candidate.Contract.TickSize,
                configuration.PaperSimulation.EntryAdverseSlippage, buy: true),
            "EXECUTION_ASSUMPTIONS", "Shared Risk applies adverse ask/bid slippage/tick rounding, without charging spread twice.");
        if (!risk.CredibleOptionRisk || risk.NetRisk is null or <= 0m || risk.GrossStopTrigger != strategy.Risk.GrossCashStop)
            gates.Missing("OPTION_CASH_RISK_UNKNOWN", "Underlying R or a cash trigger alone does not establish credible option net risk.");
        else gates.Require(risk.NetRisk <= strategy.Risk.MaximumNetRisk, "NET_RISK_BUDGET", "Never widen the budget to fit a whole lot.", risk.NetRisk, strategy.Risk.MaximumNetRisk);
        if (!risk.CredibleOpportunity || risk.ScenarioNetProfit is null)
            gates.Missing("OPPORTUNITY_UNKNOWN", "No option repricing/profit forecast is invented from futures direction.");
        else gates.Require(risk.ScenarioNetProfit >= strategy.Risk.MinimumScenarioNetProfit,
            "SCENARIO_NET_OPPORTUNITY", "An explicitly supplied scenario is a hurdle, not predicted P&L.", risk.ScenarioNetProfit, strategy.Risk.MinimumScenarioNetProfit);
        if (risk.RequiredCash is null or <= 0m || input.Portfolio.AvailableCash is null)
            gates.Missing("CASH_REQUIREMENT_UNKNOWN", "Cash requirement includes effective fees and existing reservations.");
        else gates.Require(input.Portfolio.AvailableCash >= risk.RequiredCash, "WHOLE_LOT_CASH", "Insufficient cash rejects the whole lot.", input.Portfolio.AvailableCash, risk.RequiredCash);
        if (risk.SpreadPercent is null or < 0m || risk.RoundTripFriction is null or < 0m || risk.AbsoluteStrikeDistance is null or < 0m)
            gates.Missing("RANKING_METRICS_UNKNOWN", "Shared calculations supply spread %, round-trip friction and futures-relative strike distance.");
        else gates.Require(risk.SpreadPercent <= strategy.Evidence.MaximumSpreadPercent, "MAXIMUM_SPREAD", "Excessive spread fails before ranking.", risk.SpreadPercent, strategy.Evidence.MaximumSpreadPercent);
        if (strategy.Risk.MaximumFrictionToStopRatio is { } frictionRatio && risk.RoundTripFriction is >= 0m)
            gates.Require(risk.RoundTripFriction <= frictionRatio * strategy.Risk.GrossCashStop, "FRICTION_TO_STOP",
                "Round-trip friction (spread, slippage, fees) must leave room inside the gross stop.",
                risk.RoundTripFriction, frictionRatio * strategy.Risk.GrossCashStop);
        if (risk.RoundTripFees is >= 0m && units is { } quantity)
        {
            var floor = IndicativePlanning.LongQuoteCostFloor(candidate.Quote?.Bid, candidate.Quote?.Ask, candidate.Contract.TickSize,
                configuration.PaperSimulation.EntryAdverseSlippage, configuration.PaperSimulation.ExitAdverseSlippage,
                quantity, risk.RoundTripFees.Value, input.FuturesPrice?.Price, candidate.Contract.Strike);
            if (floor is null)
                gates.Missing("QUOTE_COST_RECONCILIATION", "Executable quote cost arithmetic is unavailable/unrepresentable.");
            else
            {
                gates.Require(risk.SpreadPercent == floor.SpreadPercent && risk.AbsoluteStrikeDistance == floor.AbsoluteStrikeDistance
                    && risk.RoundTripFriction >= floor.ImmediateLiquidationLossWithFees,
                    "QUOTE_COST_RECONCILIATION", "Spread and moneyness match observed prices; friction cannot understate crossing/slippage plus supplied fees.");
                if (risk.RequiredCash is not null)
                    gates.Require(risk.RequiredCash >= floor.PremiumCash, "PREMIUM_CASH_FLOOR", "Reservations cannot be smaller than the whole-lot purchase premium, before entry fees.", risk.RequiredCash, floor.PremiumCash);
                if (risk.NetRisk is not null)
                    gates.Require(risk.NetRisk >= floor.ImmediateLiquidationLossWithFees, "IMMEDIATE_LIQUIDATION_RISK_FLOOR",
                        "Credible initial cash risk cannot understate immediately executable liquidation loss including costs.", risk.NetRisk, floor.ImmediateLiquidationLossWithFees);
            }
        }
    }

    private static IEnumerable<CandidateAssessment> Rank(IEnumerable<CandidateAssessment> candidates,
        IndexStrategyConfiguration strategy)
    {
        IOrderedEnumerable<CandidateAssessment>? ordered = null;
        foreach (var criterion in strategy.Ranking)
        {
            Func<CandidateAssessment, decimal> key = criterion switch
            {
                RankingCriterion.SpreadPercentage => candidate => candidate.FinancialEvidence!.SpreadPercent!.Value,
                RankingCriterion.RoundTripFriction => candidate => candidate.FinancialEvidence!.RoundTripFriction!.Value,
                RankingCriterion.StrikeDistance => candidate => candidate.FinancialEvidence!.AbsoluteStrikeDistance!.Value,
                RankingCriterion.ObservedDepth => candidate => candidate.Gates.Single(gate => gate.Code == "WHOLE_LOT_DEPTH").Observed!.Value,
                _ => throw new InvalidOperationException("Unsupported ranking criterion.")
            };
            var descending = criterion == RankingCriterion.ObservedDepth;
            ordered = ordered is null ? descending ? candidates.OrderByDescending(key) : candidates.OrderBy(key)
                : descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key);
        }
        return ordered!.ThenBy(candidate => candidate.ContractId, StringComparer.Ordinal);
    }

    private IndexEvaluationResult Unconfigured(IndexEvaluationInput input, StrategyState state, GateBook gates)
    {
        var decisions = input.Candidates.OrderBy(candidate => candidate.Contract.Id, StringComparer.Ordinal).Select(candidate =>
            new CandidateAssessment(EvidenceChecks.Key(input.Identity.RunId, input.Identity.EvidenceVersion,
                input.AsOfUtc.ToString("O"), ConfigurationFingerprint, input.Index.Id, candidate.Contract.Id),
                "", input.Index.Id, candidate.Contract.Id, candidate.Contract.Expiry, null, input.Origin,
                input.AsOfUtc, configuration.Version, "UNCONFIGURED", input.Identity, input.UniverseVersion,
                input.Portfolio.Version, input.Session.Id, EntryAction.INSUFFICIENT_DATA, 0, null,
                candidate.Quote?.Id, null, candidate.Risk, gates.Snapshot(), EvidenceChecks.Reasons(gates.Snapshot()), false, null,
                ConfigurationFingerprint)).ToArray();
        return new(state, Array.AsReadOnly(decisions), gates.Snapshot());
    }
}
