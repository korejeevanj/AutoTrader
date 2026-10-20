using Trading.Domain;
using Trading.Engine;
using Trading.Risk;

namespace Trading.Replay;

internal sealed record PendingResearchEntry(string Id, string IndexId, HistoricalInstrument Instrument,
    ActiveSetup Setup, DateTimeOffset AcceptedAtUtc, decimal ReservedCash,
    decimal StructuralStop, decimal InitialRisk);
internal sealed class OpenResearchPosition(PaperPositionEvidence position, int tradeIndex)
{
    public PaperPositionEvidence Position { get; } = position;
    public int TradeIndex { get; } = tradeIndex;
    public PositionPolicyState PolicyState { get; set; } = new();
    public string? PendingExitReason { get; set; }
    public DateTimeOffset? ExitRequestedAtUtc { get; set; }
    public decimal? NetMark { get; set; }
    public decimal? GrossMark { get; set; }
    public string LastAction { get; set; } = "HOLD";
    public IReadOnlyList<string> LastReasons { get; set; } = ["ENTRY_FILLED"];
}

public sealed partial class CandleReplaySession
{
    private void AcceptResearchEntries(IEnumerable<(IndexDefinition Index, IndexEvaluationInput Input,
        IndexEvaluationResult Diagnostics, IReadOnlyList<CandleCandidateAssessment> Assessments)> evaluations, DateTimeOffset time)
    {
        if (paperEntriesPaused) return;
        var priorities = engineSettings.Portfolio.IndexPriority.Select((id, i) => (id, i)).ToDictionary(pair => pair.id, pair => pair.i);
        foreach (var evaluation in evaluations.OrderBy(evaluation => priorities.GetValueOrDefault(evaluation.Index.Id, int.MaxValue))
            .ThenBy(evaluation => evaluation.Index.Id, StringComparer.Ordinal))
        {
            var profile = Profile(evaluation.Index.Id)!;
            foreach (var candidate in evaluation.Assessments.Where(candidate => candidate.HypotheticallyEligible).OrderBy(candidate => candidate.Rank))
            {
                var setup = evaluation.Diagnostics.State.Setup!;
                if (lossPaused || attempts.Any(attempt => attempt.SetupId == setup.Id)
                    || positions.Count + pending.Count >= engineSettings.Portfolio.MaximumActiveOrPendingEntries
                    || positions.Count(position => position.Position.IndexId == evaluation.Index.Id)
                        + pending.Count(intent => intent.IndexId == evaluation.Index.Id) >= engineSettings.Portfolio.MaximumActiveOrPendingEntriesPerIndex
                    || attempts.Count >= engineSettings.Portfolio.MaximumStrategyAttempts
                    || attempts.Count(attempt => attempt.IndexId == evaluation.Index.Id) >= engineSettings.Portfolio.MaximumStrategyAttemptsPerIndex) break;
                var instrument = instruments[candidate.ContractId];
                var candle = CurrentCandle(instrument.Id, time)!;
                var signal = evaluation.Input.FuturesBars.Single(bar => bar.Id == setup.SignalBarId);
                var feature = evaluation.Input.FuturesFeatures.Single(feature => feature.BarId == signal.Id);
                var units = IndicativePlanning.WholeLotUnits(profile.Lots, instrument.LotSize!.Value);
                var plan = HistoricalResearchCalculations.Plan(candle.Close, instrument.Strike!.Value, signal.Close, units,
                    instrument.TickSize!.Value, preset.FullSpreadTicks, preset.AdverseSlippageTicks, profile.Risk.GrossCashStop,
                    preset.GapStressRupees, FeeSchedule(profile, time)!);
                if (plan is null || plan.RequiredCash > HistoricalResearchCalculations.AvailableCash(ledger, pending.Select(intent => intent.ReservedCash))) continue;
                var id = ReplayRunner.Key(session.Id, evaluation.Index.Id, setup.Id, candidate.ContractId);
                pending.Add(new(id, evaluation.Index.Id, instrument, setup, time, plan.RequiredCash,
                    setup.Direction == TradeDirection.Bullish ? feature.LongStructuralStop!.Value : feature.ShortStructuralStop!.Value,
                    setup.Direction == TradeDirection.Bullish ? feature.LongInitialRisk!.Value : feature.ShortInitialRisk!.Value));
                AddTimer(time.Add(preset.PendingEntryTimeout));
                attempts.Add(new(id, evaluation.Index.Id, session.Id, setup.Id, EntryOrigin.STRATEGY, time));
                decisionTrace.Add(new(time, evaluation.Index.Id, instrument.Id, "CANDLE_RESEARCH", "HYPOTHETICAL_INTENT_ACCEPTED",
                    setup.Id, ["UNVERIFIED_LIQUIDITY", "UNVERIFIED_STRUCTURAL_OPTION_MAPPING"], []));
                break;
            }
        }
    }

    private void ExpirePendingEntries(DateTimeOffset time)
    {
        foreach (var intent in pending.ToArray())
        {
            var profile = Profile(intent.IndexId)!;
            if (paperEntriesPaused || lossPaused || time >= intent.AcceptedAtUtc.Add(preset.PendingEntryTimeout) || time >= session.At(profile.EntryEnd))
                Cancel(intent, time, paperEntriesPaused ? "PAPER_ENTRIES_PAUSED" : lossPaused ? "SESSION_PAUSE" : "ENTRY_TIMEOUT_OR_WINDOW_END");
        }
    }
    private void Cancel(PendingResearchEntry intent, DateTimeOffset time, string reason)
    {
        pending.Remove(intent);
        decisionTrace.Add(new(time, intent.IndexId, intent.Instrument.Id, "CANDLE_RESEARCH", "HYPOTHETICAL_INTENT_CANCELED", intent.Setup.Id, [reason], []));
    }

    private void ProcessEntryOpenings(ReplayBatch batch)
    {
        foreach (var intent in pending.ToArray())
        {
            if (batch.TimeUtc < intent.AcceptedAtUtc.Add(preset.ExecutionDelay)) continue;
            // Revalidate aggregate loss headroom at execution, not just intent acceptance.
            if (NetSessionPnl() is not { } sessionNet) continue;
            if (sessionNet <= -engineSettings.Portfolio.NetSessionPauseLoss)
            { lossPaused = true; Cancel(intent, batch.TimeUtc, "SESSION_PAUSE"); continue; }
            var opening = batch.Openings.SingleOrDefault(open => open.InstrumentId == intent.Instrument.Id);
            if (opening is null) continue;
            var profile = Profile(intent.IndexId)!;
            var schedule = FeeSchedule(profile, batch.TimeUtc);
            if (schedule is null) continue; // remains pending until timeout; no zero fee fallback
            var future = instruments.Values.Single(item => item.IndexId == intent.IndexId && item.Role == HistoricalInstrumentRole.Futures);
            var underlyingOpen = batch.Openings.SingleOrDefault(open => open.InstrumentId == future.Id);
            if (underlyingOpen is null) { Cancel(intent, batch.TimeUtc, "ENTRY_UNDERLYING_OPEN_MISSING"); continue; }
            if ((intent.Setup.Direction == TradeDirection.Bullish ? underlyingOpen.Open <= intent.StructuralStop : underlyingOpen.Open >= intent.StructuralStop)
                || !future.Verified || future.AvailableAtUtc > batch.TimeUtc || future.ValidFromUtc > batch.TimeUtc
                || future.ValidToUtc <= batch.TimeUtc || string.IsNullOrWhiteSpace(future.DefinitionVersion)
                || !intent.Instrument.Verified || intent.Instrument.AvailableAtUtc > batch.TimeUtc
                || intent.Instrument.ValidFromUtc > batch.TimeUtc || batch.TimeUtc >= intent.Instrument.ValidToUtc
                || (profile.ExcludeExpiryDay && intent.Instrument.Expiry <= session.TradingDate))
            { Cancel(intent, batch.TimeUtc, "PREFILL_INVALIDATION_OR_CONTRACT"); continue; }
            var units = IndicativePlanning.WholeLotUnits(profile.Lots, intent.Instrument.LotSize!.Value);
            var plan = HistoricalResearchCalculations.Plan(opening.Open, intent.Instrument.Strike!.Value, underlyingOpen.Open,
                units, intent.Instrument.TickSize!.Value, preset.FullSpreadTicks, preset.AdverseSlippageTicks,
                profile.Risk.GrossCashStop, preset.GapStressRupees, schedule);
            var cash = HistoricalResearchCalculations.AvailableCash(ledger, pending.Where(other => other.Id != intent.Id).Select(other => other.ReservedCash));
            if (lossPaused || plan is null || plan.RequiredCash > cash || plan.ModelledNetRisk > profile.Risk.MaximumNetRisk
                || plan.SpreadPercent > profile.Evidence.MaximumSpreadPercent)
            { Cancel(intent, batch.TimeUtc, "PREFILL_CASH_COST_OR_RISK"); continue; }
            ledger = HistoricalResearchCalculations.Enter(ledger, plan.FillPrice, units, plan.EntryFees.Total);
            var position = new PaperPositionEvidence(intent.Id, intent.IndexId, session.Id, $"{intent.Id}:candle-entry",
                Contract(intent.Instrument), intent.Setup.Direction, batch.TimeUtc, plan.FillPrice, units, plan.EntryFees.Total,
                engineSettings.Version, profile, future.Id, intent.StructuralStop, intent.InitialRisk, underlyingOpen.Open);
            var entryNet = EstimateNet(position, opening.Open, batch.TimeUtc);
            var opened = new OpenResearchPosition(position, trades.Count)
                { NetMark = entryNet, PolicyState = PositionPolicyState.ForPosition(position) with
                    { PeakNetLiquidationPnl = entryNet, LastEvaluationUtc = batch.TimeUtc } };
            positions.Add(opened);
            AddTimer(batch.TimeUtc.Add(profile.Position.TimeExitAfter));
            trades.Add(new(intent.Id, intent.IndexId, intent.Instrument.Id, intent.Setup.Id, intent.Setup.Direction,
                units, batch.TimeUtc, plan.FillPrice, plan.EntryFees, opening.CandleId,
                ProfitExit: new(profile.Position.ProfitExit, entryNet)));
            pending.Remove(intent);
            decisionTrace.Add(new(batch.TimeUtc, intent.IndexId, intent.Instrument.Id, "CANDLE_RESEARCH", "HYPOTHETICAL_FILLED", intent.Setup.Id,
                ["NEXT_CANDLE_OPEN", "DECLARED_SPREAD_SLIPPAGE"], []));
            var initialMark = HistoricalResearchCalculations.SellMark(opening.Open, position.Contract.TickSize, preset.FullSpreadTicks);
            if (IndicativePlanning.LongGrossPnl(position.EntryPrice, initialMark, units) <= -profile.Risk.GrossCashStop)
            {
                opened.PendingExitReason = "GROSS_CASH_STOP_AFTER_ENTRY";
                opened.ExitRequestedAtUtc = batch.TimeUtc;
                AddTimer(batch.TimeUtc.Add(preset.ExecutionDelay));
            }
            // Initial spread/fees can already breach a net stop. Do not wait for a later candle
            // (which may recover) to observe the first position-policy decision.
            if (opened.PendingExitReason is null) MonitorPositions(batch, [opened]);
            RefreshLossPause();
            if (lossPaused)
            {
                foreach (var remaining in pending.ToArray()) Cancel(remaining, batch.TimeUtc, "SESSION_PAUSE");
                break;
            }
        }
    }

    private void MonitorPositions(ReplayBatch batch, IReadOnlyList<OpenResearchPosition>? scope = null)
    {
        var monitored = (scope ?? positions).ToArray();
        var marks = new Dictionary<string, (HistoricalCandle? Option, CandleOpening? Opening, decimal? Gross)>();
        foreach (var state in monitored)
        {
            var position = state.Position;
            var profile = position.FrozenStrategy;
            var optionCompleted = batch.Completed.SingleOrDefault(bar => bar.InstrumentId == position.Contract.Id
                && bar.StartUtc >= position.EnteredAtUtc && bar.Volume > 0m);
            var optionOpening = batch.Openings.SingleOrDefault(open => open.InstrumentId == position.Contract.Id);
            // Cash-stop imputation uses only a now-completed candle, or the current opening slice.
            if (optionCompleted is not null && HistoricalResearchCalculations.SellMark(optionCompleted.Low,
                position.Contract.TickSize, preset.FullSpreadTicks) <= HistoricalResearchCalculations.StopMark(position.EntryPrice, profile.Risk.GrossCashStop, position.Units))
            {
                var fill = HistoricalResearchCalculations.StopFill(position.EntryPrice, profile.Risk.GrossCashStop, position.Units,
                    optionCompleted.Open, position.Contract.TickSize, preset.FullSpreadTicks, preset.AdverseSlippageTicks);
                if (preset.ExecutionDelay == TimeSpan.Zero
                    && TryClose(state, fill, batch.TimeUtc, optionCompleted.StartUtc, optionCompleted.EndUtc, "GROSS_CASH_STOP_INTRABAR_MODEL")) continue;
                state.PendingExitReason = "GROSS_CASH_STOP_PENDING";
                state.ExitRequestedAtUtc ??= batch.TimeUtc;
                AddTimer(state.ExitRequestedAtUtc.Value.Add(preset.ExecutionDelay));
            }
            else if (optionOpening is not null && HistoricalResearchCalculations.SellMark(optionOpening.Open,
                position.Contract.TickSize, preset.FullSpreadTicks) <= HistoricalResearchCalculations.StopMark(position.EntryPrice, profile.Risk.GrossCashStop, position.Units))
            {
                var fill = HistoricalResearchCalculations.Fill(optionOpening.Open, position.Contract.TickSize,
                    preset.FullSpreadTicks, preset.AdverseSlippageTicks, false);
                if (preset.ExecutionDelay == TimeSpan.Zero
                    && TryClose(state, fill, batch.TimeUtc, batch.TimeUtc, batch.TimeUtc, "GROSS_CASH_STOP_GAP_OPEN")) continue;
                state.PendingExitReason = "GROSS_CASH_STOP_PENDING";
                state.ExitRequestedAtUtc ??= batch.TimeUtc;
                AddTimer(state.ExitRequestedAtUtc.Value.Add(preset.ExecutionDelay));
            }
            var option = CurrentCandle(position.Contract.Id, batch.TimeUtc);
            if (option is not null && (option.StartUtc < position.EnteredAtUtc || option.Volume <= 0m)) option = null;
            var opening = knownOpenings.GetValueOrDefault(position.Contract.Id);
            if (opening is not null && (opening.StartUtc < position.EnteredAtUtc || batch.TimeUtc - opening.StartUtc >= TimeSpan.FromMinutes(1)
                || (option is not null && opening.StartUtc < option.EndUtc))) opening = null;
            if (opening is not null) option = null;
            decimal? gross = null; state.NetMark = null;
            var reference = opening?.Open ?? option?.Close;
            if (reference is { } price)
            {
                var mark = HistoricalResearchCalculations.SellMark(price, position.Contract.TickSize, preset.FullSpreadTicks);
                if (mark > 0m) gross = IndicativePlanning.LongGrossPnl(position.EntryPrice, mark, position.Units);
                state.NetMark = EstimateNet(position, price, batch.TimeUtc);
            }
            marks[position.Id] = (option, opening, gross);
            state.GrossMark = gross;
        }
        // Every mark must be refreshed before any policy consumes aggregate portfolio P&L.
        foreach (var state in monitored.Where(state => positions.Contains(state)))
        {
            var position = state.Position;
            var (option, opening, gross) = marks[position.Id];
            var index = dataset.Manifest.Indices.Single(index => index.Id == position.IndexId);
            var market = BuildInput(index, Identity(batch.TimeUtc), Portfolio(batch.TimeUtc));
            var future = CurrentCandle(position.FuturesInstrumentId, batch.TimeUtc);
            if (future is not null && future.EndUtc <= position.EnteredAtUtc) future = null;
            decimal? progress = null;
            if (future is not null && future.StartUtc >= position.EnteredAtUtc
                && position.InitialUnderlyingRisk is > 0m && position.UnderlyingEntryReference is > 0m)
                progress = HistoricalResearchCalculations.Progress(position.UnderlyingEntryReference.Value,
                    position.Direction == TradeDirection.Bullish ? future.High : future.Low,
                    position.InitialUnderlyingRisk.Value, position.Direction == TradeDirection.Bullish);
            var policyInput = new PositionEvaluationInput(market.Identity, batch.TimeUtc, session, position, null, null, null, null,
                market.FuturesBars, market.FuturesFeatures, market.ConstituentDefinitions, market.Constituents, Portfolio(batch.TimeUtc));
            var decision = engine.EvaluateCandlePosition(policyInput, new(position.Id, batch.TimeUtc,
                option is null ? null : Bar(option, session.Id), future is null ? null : Bar(future, session.Id),
                gross, state.NetMark, progress, HistoricalResearchCalculations.Version,
                opening is null ? null : new(opening.CandleId, opening.InstrumentId, opening.StartUtc, opening.Open)), state.PolicyState);
            state.PolicyState = decision.State;
            if (decision.State.SoftWeakeningDeadlineUtc is { } softDeadline) AddTimer(softDeadline);
            var profitEvidence = trades[state.TradeIndex].ProfitExit ?? new(position.FrozenStrategy.Position.ProfitExit);
            profitEvidence = profitEvidence with { PeakNetLiquidationPnl = decision.State.PeakNetLiquidationPnl };
            state.LastAction = decision.Action.ToString(); state.LastReasons = decision.ReasonCodes;
            if (decision.Action == PositionAction.EXIT)
            {
                var firstExitRequest = state.PendingExitReason is null;
                if (state.PendingExitReason?.StartsWith("GROSS_CASH_STOP", StringComparison.Ordinal) != true)
                    state.PendingExitReason = decision.ReasonCodes[0];
                if (firstExitRequest && state.PendingExitReason is "NET_PROFIT_TARGET" or "NET_PROFIT_TRAIL")
                    profitEvidence = profitEvidence with { TriggeredAtUtc = batch.TimeUtc, TriggerNetLiquidationPnl = decision.NetLiquidationPnl,
                        TriggerThreshold = state.PendingExitReason == "NET_PROFIT_TARGET" ? position.FrozenStrategy.Position.ProfitExit.NetTarget : decision.State.ProfitTrailThreshold };
                state.ExitRequestedAtUtc ??= batch.TimeUtc;
                AddTimer(state.ExitRequestedAtUtc.Value.Add(preset.ExecutionDelay));
            }
            trades[state.TradeIndex] = trades[state.TradeIndex] with { ProfitExit = profitEvidence };
            decisionTrace.Add(new(batch.TimeUtc, position.IndexId, position.Contract.Id, "CANDLE_POSITION_POLICY", decision.Action.ToString(),
                trades[state.TradeIndex].SetupId, decision.ReasonCodes, decision.Gates));
        }
    }

    private void ProcessExistingExits(ReplayBatch batch)
    {
        foreach (var position in positions.ToArray())
        {
            if (position.PendingExitReason is null) continue;
            position.ExitRequestedAtUtc ??= batch.TimeUtc;
            if (batch.TimeUtc < position.ExitRequestedAtUtc.Value.Add(preset.ExecutionDelay)) continue;
            var opening = batch.Openings.SingleOrDefault(open => open.InstrumentId == position.Position.Contract.Id);
            if (opening is null) continue;
            var fill = HistoricalResearchCalculations.Fill(opening.Open, position.Position.Contract.TickSize,
                preset.FullSpreadTicks, preset.AdverseSlippageTicks, false);
            TryClose(position, fill, batch.TimeUtc, batch.TimeUtc, batch.TimeUtc, position.PendingExitReason);
        }
    }

    private bool TryClose(OpenResearchPosition state, decimal fill, DateTimeOffset knownAt,
        DateTimeOffset intervalStart, DateTimeOffset intervalEnd, string reason)
    {
        var position = state.Position;
        if (fill <= 0m) return false;
        var feeResolution = HistoricalResearchCalculations.ResolveExitFees(settings.FeeSchedules, position.FrozenStrategy.Risk.FeeProfileId,
            fill, position.Units, intervalStart, intervalEnd, knownAt, dataset.Manifest.IsSynthetic);
        if (feeResolution is null) return false;
        var (exitFees, attribution) = feeResolution.Value;
        var gross = IndicativePlanning.LongGrossPnl(position.EntryPrice, fill, position.Units)!.Value;
        var net = HistoricalResearchCalculations.Net(position.EntryPrice, fill, position.Units, position.ActualEntryFees, exitFees.Total);
        ledger = HistoricalResearchCalculations.Exit(ledger, position.EntryPrice, fill, position.Units, position.ActualEntryFees, exitFees.Total);
        var profitEvidence = trades[state.TradeIndex].ProfitExit;
        if (profitEvidence?.TriggerThreshold is { } threshold)
            profitEvidence = profitEvidence with { RealizedShortfall = HistoricalResearchCalculations.ProfitExitShortfall(net, threshold) };
        trades[state.TradeIndex] = trades[state.TradeIndex] with { ExitKnownAtUtc = knownAt, ExitIntervalStartUtc = intervalStart,
            ExitIntervalEndUtc = intervalEnd, ExitPrice = fill, ExitFees = exitFees, GrossPnl = gross, NetPnl = net,
            ExitReason = reason, ExitFeeAttribution = attribution, ProfitExit = profitEvidence, GrossStopOverrun = reason.StartsWith("GROSS_CASH_STOP", StringComparison.Ordinal)
                ? HistoricalResearchCalculations.GrossStopOverrun(gross, position.FrozenStrategy.Risk.GrossCashStop) : 0m };
        positions.Remove(state);
        decisionTrace.Add(new(knownAt, position.IndexId, position.Contract.Id, "CANDLE_RESEARCH", "HYPOTHETICAL_EXIT_FILLED",
            trades[state.TradeIndex].SetupId, [reason, "NO_SUBSECOND_EXECUTION_CLAIM"], []));
        return true;
    }

    private decimal? NetSessionPnl() => HistoricalResearchCalculations.SessionNet(ledger, positions.Select(position => position.NetMark));
    private decimal? EstimateNet(PaperPositionEvidence position, decimal reference, DateTimeOffset time)
    {
        var exit = HistoricalResearchCalculations.Fill(reference, position.Contract.TickSize, preset.FullSpreadTicks, preset.AdverseSlippageTicks, false);
        var schedule = FeeSchedule(position.FrozenStrategy, time);
        return exit > 0m && schedule is not null ? HistoricalResearchCalculations.Net(position.EntryPrice, exit, position.Units,
            position.ActualEntryFees, HistoricalResearchCalculations.Fees(exit, position.Units, false, schedule).Total) : null;
    }
    private void RefreshLossPause()
    {
        var pnl = NetSessionPnl();
        if (pnl <= -engineSettings.Portfolio.NetSessionPauseLoss) lossPaused = true;
    }
}
