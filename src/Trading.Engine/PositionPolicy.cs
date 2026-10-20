using Trading.Risk;

namespace Trading.Engine;

public sealed partial class TradingDecisionEngine
{
    public PositionDecision EvaluatePosition(PositionEvaluationInput input, PositionPolicyState? previousState = null)
        => EvaluatePositionCore(input, previousState, null);

    private PositionDecision EvaluatePositionCore(PositionEvaluationInput input, PositionPolicyState? previousState,
        CandlePositionEvidence? candle)
    {
        ArgumentNullException.ThrowIfNull(input);
        EvidenceChecks.ValidateContext(input.Identity, input.AsOfUtc, input.Session);
        var position = input.Position;
        var strategy = position.FrozenStrategy;
        var errors = strategy.Validate();
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(input));
        if (position.IndexId != strategy.IndexId || position.Contract.IndexId != position.IndexId
            || position.SessionId != input.Session.Id || position.EnteredAtUtc > input.AsOfUtc
            || position.EntryPrice <= 0m || position.Units <= 0 || position.Contract.LotSize <= 0
            || position.Contract.TickSize <= 0m || position.ExitAdverseSlippage < 0m || !Enum.IsDefined(position.Direction)
            || position.Units % position.Contract.LotSize != 0 || position.ActualEntryFees < 0m
            || string.IsNullOrWhiteSpace(position.ConfigurationVersion) || string.IsNullOrWhiteSpace(position.EntryFillVersion))
            throw new ArgumentException("Supply a reconciled, whole-lot position and its frozen strategy/fill evidence.", nameof(input));
        var state = previousState ?? new PositionPolicyState();
        if (state.PositionFingerprint is null && state != new PositionPolicyState())
            throw new ArgumentException("Unbound legacy position state must be reconstructed from frozen entry and market evidence.", nameof(previousState));
        var positionFingerprint = FrozenPositionFingerprint.Create(position);
        if (state.PositionId is not null && state.PositionId != position.Id)
            throw new ArgumentException("Position state belongs to another position.", nameof(previousState));
        if (state.PositionFingerprint is not null && state.PositionFingerprint != positionFingerprint)
            throw new ArgumentException("Position state belongs to different frozen position/fill/policy evidence.", nameof(previousState));
        if (state.LastEvaluationUtc > input.AsOfUtc)
            throw new ArgumentException("Future position-policy state cannot be used at an earlier evaluation time.", nameof(previousState));
        state = state with { PositionId = position.Id, LastEvaluationUtc = input.AsOfUtc, PositionFingerprint = positionFingerprint };
        var gates = new GateBook();
        var exits = new List<string>(state.ExitLatched ? state.LatchedExitReasons ?? [] : []);
        var watches = new List<string>();
        var protectionTightened = false;
        decimal? gross = null, net = null;
        var quote = input.OptionQuote;
        var freshBid = candle is null && EvidenceChecks.FreshQuote(quote, position.Contract.Id, input.AsOfUtc, strategy.Evidence)
            && quote!.Bid is > 0m && (quote.Ask is null || (quote.Ask > 0m && quote.Bid <= quote.Ask));
        var executableBid = freshBid ? IndicativePlanning.ExecutablePrice(quote!.Bid, position.Contract.TickSize,
            position.ExitAdverseSlippage, buy: false) : null;
        var observedGross = freshBid ? IndicativePlanning.LongGrossPnl(position.EntryPrice, executableBid ?? quote!.Bid, position.Units) : null;
        // An illiquid or unrepresentable adjusted mark must not hide a loss already seen at a fresh bid.
        // It is a protective observation, not a full-quantity liquidation price or fill.
        if (observedGross <= -strategy.Risk.GrossCashStop)
        {
            exits.Add("GROSS_CASH_STOP");
            gates.Add("GROSS_CASH_STOP_OBSERVED", GateOutcome.PASS,
                "Fresh bid-based loss observation requests exit even if whole-position liquidity is unavailable; no fill is assumed.", observedGross, -strategy.Risk.GrossCashStop);
        }
        var exitDepthKnown = quote?.BidQuantity >= position.Units;
        if (freshBid && !exitDepthKnown)
        {
            gates.Add("POSITION_EXIT_DEPTH_UNAVAILABLE", GateOutcome.UNKNOWN,
                "Observed bid depth must cover the whole position before qualifying liquidation/profit marks.", quote!.BidQuantity, position.Units);
            watches.Add("POSITION_EXIT_DEPTH_UNAVAILABLE");
        }
        var priced = candle is not null ? (candle.OptionCandle is not null || candle.OptionOpening is not null) && candle.GrossPnl is not null
            : executableBid is not null && exitDepthKnown;
        if (priced)
        {
            gross = candle is null ? observedGross : candle.GrossPnl;
            gates.Add("GROSS_LIQUIDATION", GateOutcome.PASS, candle is null ? "Indicative fresh bid less frozen adverse slippage, rounded down to tick; not a fill. Spread is not charged again." : "Declared available candle/open research mark; not observed bid/ask.", gross);
            gates.Add("GROSS_CASH_STOP_THRESHOLD", GateOutcome.PASS, "Frozen gross cash-loss trigger; fees and any separately configured net trigger are distinct.", gross, -strategy.Risk.GrossCashStop);
            if (gross <= -strategy.Risk.GrossCashStop) exits.Add("GROSS_CASH_STOP");
            var liquidation = input.NetLiquidation;
            if (candle is not null)
            {
                net = candle.NetPnl;
                if (net is null) { gates.Missing("NET_LIQUIDATION_UNKNOWN", "No effective research exit-fee assessment."); watches.Add("NET_LIQUIDATION_UNKNOWN"); }
            }
            else if (quote is not null && liquidation is not null && liquidation.PositionId == position.Id
                && liquidation.EntryFillVersion == position.EntryFillVersion && liquidation.QuoteId == quote.Id
                && liquidation.AvailableAtUtc <= input.AsOfUtc && liquidation.AvailableAtUtc >= quote.AvailableAtUtc
                && input.AsOfUtc < liquidation.ValidUntilUtc && liquidation.GrossPnl == gross
                && liquidation.ActualEntryFees == position.ActualEntryFees && liquidation.ProjectedExitFees >= 0m
                && IndicativePlanning.NetLiquidationPnl(liquidation.GrossPnl, liquidation.ActualEntryFees,
                    liquidation.ProjectedExitFees) == liquidation.NetPnl
                && !string.IsNullOrWhiteSpace(liquidation.CalculatorVersion)
                && !string.IsNullOrWhiteSpace(liquidation.FeeScheduleVersion))
            {
                net = liquidation.NetPnl;
            }
            else { gates.Missing("NET_LIQUIDATION_UNKNOWN", "No current reconciled fee/mark assessment; never substitute gross for net."); watches.Add("NET_LIQUIDATION_UNKNOWN"); }
        }
        else { gates.Missing("POSITION_UNPRICED", "Missing/stale bid, unavailable executable mark or inadequate whole-position depth leaves liquidation unknown; no fill is assumed."); watches.Add("POSITION_UNPRICED"); }

        var profitExit = strategy.Position.ProfitExit;
        gates.Add("PROFIT_EXIT_POLICY", profitExit.Mode == ProfitExitMode.Disabled ? GateOutcome.NOT_EVALUATED : GateOutcome.PASS,
            $"{profitExit.Version}: {profitExit.Mode}. Profit exits use known net liquidation; a trigger is not a fill.");
        if (net is { } observedNet)
        {
            if (strategy.Risk.NetCashStop is { } netStop)
            {
                gates.Add("NET_CASH_STOP_THRESHOLD", GateOutcome.PASS, "Frozen optional net cash stop includes paid entry and projected exit charges.", observedNet, -netStop);
                if (observedNet <= -netStop) exits.Add("NET_CASH_STOP");
            }
            state = state with { PeakNetLiquidationPnl = state.PeakNetLiquidationPnl is { } previousPeak ? Math.Max(previousPeak, observedNet) : observedNet };
            gates.Add("NET_LIQUIDATION", GateOutcome.PASS, "Shared Risk subtracts actual entry and projected effective exit costs once.", observedNet);
            gates.Add("PEAK_NET_LIQUIDATION", GateOutcome.PASS, "Highest known net liquidation mark; no future candle high is used.", state.PeakNetLiquidationPnl);
            if (observedNet >= strategy.Position.ProfitMilestone)
                gates.Add("PROFIT_MILESTONE", GateOutcome.PASS, "Informational milestone; the separately configured profit-exit policy decides exits.", observedNet, strategy.Position.ProfitMilestone);
            if (profitExit.Mode == ProfitExitMode.FixedNetTarget)
            {
                var reached = observedNet >= profitExit.NetTarget;
                gates.Add("NET_PROFIT_TARGET", reached ? GateOutcome.PASS : GateOutcome.NOT_EVALUATED,
                    reached ? "Net target reached; request exit using the host execution model." : "Net target has not been reached.", observedNet, profitExit.NetTarget);
                if (reached) exits.Add("NET_PROFIT_TARGET");
            }
            else if (profitExit.Mode == ProfitExitMode.TrailingNetProfit)
            {
                var armed = state.ProfitTrailArmed || observedNet >= profitExit.TrailActivation;
                if (armed)
                {
                    var threshold = Math.Max(profitExit.MinimumLockedNetProfit, state.PeakNetLiquidationPnl!.Value - profitExit.TrailGiveback);
                    protectionTightened = state.ProfitTrailThreshold is null || threshold > state.ProfitTrailThreshold;
                    state = state with { ProfitTrailArmed = true, ProfitTrailThreshold = Math.Max(state.ProfitTrailThreshold ?? threshold, threshold) };
                    if (protectionTightened)
                        gates.Add("NET_PROFIT_TRAIL_TIGHTENED", GateOutcome.PASS,
                            "Tightened net-liquidation-P&L protection, not an underlying-price stop or promised break-even fill.", state.ProfitTrailThreshold);
                    gates.Add("NET_PROFIT_TRAIL_THRESHOLD", GateOutcome.PASS,
                        "Armed net-profit floor only tightens; request exit at or below this threshold.", observedNet, state.ProfitTrailThreshold);
                    if (observedNet <= state.ProfitTrailThreshold) exits.Add("NET_PROFIT_TRAIL");
                }
                gates.Add("NET_PROFIT_TRAIL_ACTIVATION", armed ? GateOutcome.PASS : GateOutcome.NOT_EVALUATED,
                    armed ? "Net-profit trail activated." : "Trail awaits its net-profit activation threshold.", observedNet, profitExit.TrailActivation);
            }
        }

        var underlyingFresh = candle is not null ? candle.FuturesCandle is not null
            : EvidenceChecks.FreshUnderlying(input.FuturesPrice, position.FuturesInstrumentId, input.AsOfUtc, strategy.Evidence);
        if (underlyingFresh)
        {
            if (position.StructuralStop is > 0m)
            {
                var underlyingPrice = candle is null ? input.FuturesPrice!.Price : candle.FuturesCandle!.Close;
                var breached = position.Direction == TradeDirection.Bullish
                    ? underlyingPrice <= position.StructuralStop : underlyingPrice >= position.StructuralStop;
                gates.Add("STRUCTURAL_STOP_THRESHOLD", GateOutcome.PASS,
                    $"{position.Direction}: frozen underlying structural invalidation; no translation into guaranteed option rupees.", underlyingPrice, position.StructuralStop);
                if (breached) exits.Add("STRUCTURAL_INVALIDATION");
            }
            else { gates.Missing("STRUCTURAL_STOP_UNKNOWN", "No credible frozen structural stop is invented."); watches.Add("STRUCTURAL_STOP_UNKNOWN"); }
            var progress = input.Progress;
            if (candle is not null)
            {
                if (candle.MaximumProgressR is { } progressR)
                    state = state with { MaximumFavorableProgressR = Math.Max(state.MaximumFavorableProgressR ?? 0m, progressR) };
                else { gates.Missing("UNDERLYING_PROGRESS_UNKNOWN", "No frozen-reference candle excursion calculation."); watches.Add("UNDERLYING_PROGRESS_UNKNOWN"); }
            }
            else if (progress is not null && progress.PositionId == position.Id
                && progress.UnderlyingQuoteId == input.FuturesPrice!.Id
                && progress.AvailableAtUtc <= input.AsOfUtc && progress.AvailableAtUtc >= input.FuturesPrice.AvailableAtUtc
                && progress.InitialRisk == position.InitialUnderlyingRisk && progress.InitialRisk > 0m
                && progress.EntryReference == position.UnderlyingEntryReference && progress.EntryReference > 0m
                && IndicativePlanning.UnderlyingProgressR(progress.EntryReference, input.FuturesPrice.Price!.Value,
                    progress.InitialRisk, position.Direction == TradeDirection.Bullish) == progress.ProgressR
                && !string.IsNullOrWhiteSpace(progress.CalculatorVersion))
            {
                var maximum = state.MaximumFavorableProgressR ?? 0m;
                if (progress.ProgressR > maximum) maximum = progress.ProgressR;
                state = state with { MaximumFavorableProgressR = maximum };
                gates.Add("UNDERLYING_PROGRESS", GateOutcome.PASS, "Shared Risk progress uses frozen signal R and actual-entry underlying reference.", maximum, strategy.Position.RequiredProgressR);
            }
            else { gates.Missing("UNDERLYING_PROGRESS_UNKNOWN", "Require shared progress bound to the current underlying observation and frozen references."); watches.Add("UNDERLYING_PROGRESS_UNKNOWN"); }
        }
        else { gates.Missing("UNDERLYING_UNPRICED", "Fresh packet time is not fresh futures last-trade evidence."); watches.Add("UNDERLYING_UNPRICED"); }

        var bars = EvidenceChecks.Bars(input.FuturesBars, position.FuturesInstrumentId, input.Session,
            input.AsOfUtc, strategy, gates);
        foreach (var bar in bars.Where(bar => bar.EndUtc > position.EnteredAtUtc
            && (state.LastProcessedBarEndUtc is null || bar.EndUtc > state.LastProcessedBarEndUtc)))
        {
            CheckSoftDeadline(state, bar.AvailableAtUtc, exits);
            var feature = EvidenceChecks.Feature(input.FuturesFeatures, bar, bars,
                position.ConfigurationVersion, input.AsOfUtc, gates);
            if (feature is null) { watches.Add("THESIS_EVIDENCE_UNKNOWN"); break; }
            var consecutive = state.LastProcessedBarEndUtc == bar.StartUtc;
            var adverse = position.Direction == TradeDirection.Bullish ? bar.Close < feature.Vwap : bar.Close > feature.Vwap;
            state = state with
            {
                WrongSideCloses = adverse ? consecutive ? state.WrongSideCloses + 1 : 1 : 0,
                LastProcessedBarEndUtc = bar.EndUtc
            };
            var historicalConfirmation = EvidenceChecks.Confirmation(input.ConstituentDefinitions, input.Constituents,
                bar, input.AsOfUtc, strategy, gates);
            var against = position.Direction == TradeDirection.Bullish
                ? historicalConfirmation?.BelowVwapWeightPercent : historicalConfirmation?.AboveVwapWeightPercent;
            var thesisKnownAt = ThesisKnownAt(input, bar, feature);
            CheckSoftDeadline(state, thesisKnownAt, exits);
            if (state.WrongSideCloses >= strategy.Position.ThesisFailureCloses && against >= strategy.ConfirmationWeightPercent)
                exits.Add("THESIS_FAILURE");
            var supporting = position.Direction == TradeDirection.Bullish
                ? historicalConfirmation?.AboveVwapWeightPercent : historicalConfirmation?.BelowVwapWeightPercent;
            state = ObserveSoftThesis(state, adverse || against >= strategy.ConfirmationWeightPercent,
                !adverse && bar.Close != feature.Vwap && supporting >= strategy.ConfirmationWeightPercent,
                adverse ? FuturesThesisKnownAt(input, bar, feature) : thesisKnownAt, strategy.Position);
        }
        if (state.WrongSideCloses > 0) watches.Add("VWAP_THESIS_WEAKENING");
        // A fresh option tick does not prove that the completed-bar thesis is still observable.
        // Recheck even at entry and when no new bar has advanced policy state.
        var latest = bars.LastOrDefault();
        if (latest is null || latest.EndUtc < state.LastProcessedBarEndUtc
            || input.AsOfUtc - latest.EndUtc > strategy.BarInterval + strategy.Evidence.MaximumObservationAge)
        {
            gates.Missing("MONITORING_BAR_UNAVAILABLE", "Require current completed-minute monitoring coverage; quote freshness cannot replace it.");
            watches.Add("MONITORING_BAR_UNAVAILABLE");
        }
        else
        {
            var latestFeature = EvidenceChecks.Feature(input.FuturesFeatures, latest, bars,
                position.ConfigurationVersion, input.AsOfUtc, gates);
            if (latestFeature is null)
                watches.Add("THESIS_EVIDENCE_UNKNOWN");
            var participation = EvidenceChecks.Confirmation(input.ConstituentDefinitions, input.Constituents,
                latest, input.AsOfUtc, strategy, gates);
            if (participation is null) watches.Add("THESIS_CONFIRMATION_UNKNOWN");
            var against = position.Direction == TradeDirection.Bullish
                ? participation?.BelowVwapWeightPercent : participation?.AboveVwapWeightPercent;
            if (participation is not null)
            {
                gates.Add("ADVERSE_CONSTITUENT_WEIGHT", GateOutcome.PASS,
                    "Current absolute weight against the frozen position direction; no renormalization or index-point attribution.", against, strategy.ConfirmationWeightPercent);
                if (against >= strategy.ConfirmationWeightPercent)
                {
                    watches.Add("ADVERSE_CONSTITUENT_CONFIRMATION");
                    if (state.LastProcessedBarEndUtc == latest.EndUtc
                        && state.WrongSideCloses >= strategy.Position.ThesisFailureCloses) exits.Add("THESIS_FAILURE");
                }
            }
            bool? adverse = latestFeature is null ? null : position.Direction == TradeDirection.Bullish
                ? latest.Close < latestFeature.Vwap : latest.Close > latestFeature.Vwap;
            if (adverse == true) watches.Add("VWAP_THESIS_WEAKENING");
            var supporting = position.Direction == TradeDirection.Bullish
                ? participation?.AboveVwapWeightPercent : participation?.BelowVwapWeightPercent;
            var thesisKnownAt = ThesisKnownAt(input, latest, latestFeature);
            CheckSoftDeadline(state, thesisKnownAt, exits);
            state = ObserveSoftThesis(state, adverse == true || against >= strategy.ConfirmationWeightPercent,
                adverse == false && latest.Close != latestFeature!.Vwap && supporting >= strategy.ConfirmationWeightPercent,
                adverse == true ? FuturesThesisKnownAt(input, latest, latestFeature) : thesisKnownAt, strategy.Position);
        }
        CheckSoftDeadline(state, input.AsOfUtc, exits);
        if (state.SoftWeakeningDeadlineUtc is { } deadline)
        {
            watches.Add("SOFT_THESIS_CONFIRMATION_PENDING");
            gates.Add("SOFT_THESIS_CONFIRMATION", GateOutcome.NOT_EVALUATED,
                $"{strategy.Position.SoftThesisPolicyVersion}: a completed close strictly with VWAP and at least {strategy.ConfirmationWeightPercent}% directional constituent weight clears weakening; hard thesis failure or deadline {deadline:O} requests exit.",
                state.WrongSideCloses, strategy.Position.ThesisFailureCloses);
        }
        if (input.AsOfUtc - position.EnteredAtUtc >= strategy.Position.TimeExitAfter)
        {
            if (position.InitialUnderlyingRisk is > 0m && position.UnderlyingEntryReference is > 0m)
            {
                // A timer may act on the preserved maximum even if no new quote arrives.
                gates.Add("TIME_EXIT_PROGRESS_THRESHOLD", GateOutcome.PASS, "Elapsed no-progress deadline reached; use maximum observed progress in frozen underlying R.",
                    state.MaximumFavorableProgressR, strategy.Position.RequiredProgressR);
                if ((state.MaximumFavorableProgressR ?? 0m) < strategy.Position.RequiredProgressR) exits.Add("TIME_NO_PROGRESS");
            }
            else { gates.Missing("TIME_EXIT_PROGRESS_UNKNOWN", "Entry underlying reference/R is unavailable; do not invent progress."); watches.Add("TIME_EXIT_PROGRESS_UNKNOWN"); }
        }
        if (input.AsOfUtc >= input.Session.At(strategy.SessionExit)) exits.Add("SESSION_EXIT");
        if (EvidenceChecks.PortfolioUsable(input.Portfolio, input.Session, input.AsOfUtc))
        {
            if (input.Portfolio.PauseLatched || input.Portfolio.NetLiquidationSessionPnl <= -configuration.Portfolio.NetSessionPauseLoss)
                exits.Add("PORTFOLIO_SESSION_PAUSE");
            if (input.Portfolio.NetLiquidationSessionPnl is null) watches.Add("SESSION_NET_PNL_UNKNOWN");
            if (!input.Portfolio.RecoveryReady || !input.Portfolio.RecordingComplete) watches.Add("HOST_DEGRADED");
        }
        else watches.Add("PORTFOLIO_SNAPSHOT_UNKNOWN");
        if (gates.Unknown && watches.Count == 0) watches.Add("POSITION_EVIDENCE_UNKNOWN");
        var exitReasons = Array.AsReadOnly(exits.Distinct(StringComparer.Ordinal).OrderBy(ExitPriority).ThenBy(reason => reason, StringComparer.Ordinal).ToArray());
        state = state with { ExitLatched = state.ExitLatched || exits.Count != 0, LatchedExitReasons = exitReasons };
        var action = state.ExitLatched ? PositionAction.EXIT : watches.Count != 0 ? PositionAction.WATCH_EXIT
            : protectionTightened ? PositionAction.TIGHTEN_STOP : PositionAction.HOLD;
        var reasons = state.ExitLatched ? exitReasons
            : watches.Count != 0 ? Array.AsReadOnly(watches.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
            : (IReadOnlyList<string>)[protectionTightened ? "NET_PROFIT_TRAIL_TIGHTENED" : "POLICY_VALID"];
        return new(EvidenceChecks.Key(input.Identity.RunId, input.Identity.EvidenceVersion, input.AsOfUtc.ToString("O"),
            position.Id, position.ConfigurationVersion), position.Id, input.AsOfUtc, action, gross, net,
            reasons, gates.Snapshot(), position.ConfigurationVersion, input.Identity, state,
            WatchResolutions: action == PositionAction.WATCH_EXIT ? Array.AsReadOnly(reasons.Select(reason =>
                new WatchResolution(reason, reason is "VWAP_THESIS_WEAKENING" or "ADVERSE_CONSTITUENT_CONFIRMATION" or "SOFT_THESIS_CONFIRMATION_PENDING"
                    ? "Next completed close strictly with VWAP and configured directional constituent weight restores thesis; hard failure or soft deadline requests EXIT."
                    : "Restore the named monitoring evidence on the next relevant quote/completed bar or host freshness timer; no safe HOLD until restored.",
                    state.SoftWeakeningDeadlineUtc)).ToArray()) : []);
    }

    private static PositionPolicyState ObserveSoftThesis(PositionPolicyState state, bool weakening,
        bool recovered, DateTimeOffset observedAt, PositionPolicyConfiguration policy)
    {
        if (weakening && state.SoftWeakeningSinceUtc is null)
            return state with { SoftWeakeningSinceUtc = observedAt, SoftWeakeningDeadlineUtc = observedAt + policy.MaximumSoftWeakeningDuration };
        // Neutral or missing confirmation cannot reset a running warning/deadline.
        return !weakening && recovered ? state with { SoftWeakeningSinceUtc = null, SoftWeakeningDeadlineUtc = null } : state;
    }

    private static DateTimeOffset FuturesThesisKnownAt(PositionEvaluationInput input, CompletedBarEvidence bar, FuturesFeatureEvidence? feature) =>
        new[] { input.Position.EnteredAtUtc, bar.AvailableAtUtc, feature?.AvailableAtUtc ?? bar.AvailableAtUtc }.Max();

    private static DateTimeOffset ThesisKnownAt(PositionEvaluationInput input, CompletedBarEvidence bar, FuturesFeatureEvidence? feature)
    {
        var time = FuturesThesisKnownAt(input, bar, feature);
        if (input.ConstituentDefinitions is { Members: not null } definitions && definitions.AvailableAtUtc <= input.AsOfUtc)
        {
            if (definitions.AvailableAtUtc > time) time = definitions.AvailableAtUtc;
            var members = definitions.Members.Select(member => member.InstrumentId).ToHashSet(StringComparer.Ordinal);
            foreach (var observation in input.Constituents.Where(observation => members.Contains(observation.InstrumentId)
                && observation.SessionId == bar.SessionId && observation.StartUtc == bar.StartUtc && observation.EndUtc == bar.EndUtc
                && observation.AvailableAtUtc <= input.AsOfUtc))
                if (observation.AvailableAtUtc > time) time = observation.AvailableAtUtc;
        }
        return time;
    }

    private static void CheckSoftDeadline(PositionPolicyState state, DateTimeOffset time, List<string> exits)
    {
        if (state.SoftWeakeningDeadlineUtc <= time) exits.Add("SOFT_THESIS_TIMEOUT");
    }

    private static int ExitPriority(string reason) => reason switch
    {
        "GROSS_CASH_STOP" => 0, "NET_CASH_STOP" => 1, "STRUCTURAL_INVALIDATION" => 2, "PORTFOLIO_SESSION_PAUSE" => 3,
        "SESSION_EXIT" => 4, "THESIS_FAILURE" => 5, "TIME_NO_PROGRESS" => 6,
        "SOFT_THESIS_TIMEOUT" => 7, "NET_PROFIT_TARGET" => 8, "NET_PROFIT_TRAIL" => 9, _ => 10
    };
}
