namespace Trading.Engine;

public sealed record CandleCandidateFinancialEvidence(string ContractId, string ContractDefinitionVersion,
    CompletedBarEvidence OptionCandle, string SignalBarId, DateTimeOffset AvailableAtUtc,
    string CalculatorVersion, string FeeProfileId, string FeeScheduleVersion, bool FeeScheduleApplicable,
    decimal RequiredCash, decimal ModelledNetRisk, decimal SpreadPercent, decimal Friction, decimal StrikeDistance);
public sealed record CandleCandidateAssessment(string ContractId, string SetupId, TradeDirection? Direction,
    bool HypotheticallyEligible, int? Rank, IReadOnlyList<GateResult> Gates,
    IReadOnlyList<string> ReasonCodes, string EvidenceClass = "CANDLE_RESEARCH_UNVERIFIED_LIQUIDITY_AND_OPTION_MAPPING");

public sealed record CandlePositionEvidence(string PositionId, DateTimeOffset AvailableAtUtc,
    CompletedBarEvidence? OptionCandle, CompletedBarEvidence? FuturesCandle,
    decimal? GrossPnl, decimal? NetPnl, decimal? MaximumProgressR, string CalculatorVersion,
    CandleOpeningPositionEvidence? OptionOpening = null);
public sealed record CandleOpeningPositionEvidence(string CandleId, string InstrumentId, DateTimeOffset TimeUtc, decimal Open);

public sealed partial class TradingDecisionEngine
{
    // Explicit research seam: never turns an insufficient strict assessment into ENTER.
    public IReadOnlyList<CandleCandidateAssessment> AssessCandleResearch(IndexEvaluationInput input,
        IndexEvaluationResult diagnostics, IReadOnlyList<CandleCandidateFinancialEvidence> financials)
    {
        if (input.Identity.Mode == EngineDataMode.PAPER_LIVE_DATA)
            throw new ArgumentException("Candle research is not a live-data execution qualification.", nameof(input));
        var strategy = configuration.Strategies.SingleOrDefault(profile => profile.IndexId == input.Index.Id);
        if (strategy is null) return Array.AsReadOnly(diagnostics.Candidates.Select(candidate =>
            new CandleCandidateAssessment(candidate.ContractId, "", null, false, null, candidate.Gates, candidate.ReasonCodes)).ToArray());
        if (diagnostics.State.ConfigurationFingerprint != ConfigurationFingerprint
            || diagnostics.State.IndexId != input.Index.Id || diagnostics.State.SessionId != input.Session.Id
            || diagnostics.Candidates.Count != input.Candidates.Count
            || diagnostics.Candidates.Any(candidate => candidate.DecisionTimeUtc != input.AsOfUtc || candidate.Identity != input.Identity
                || candidate.UniverseVersion != input.UniverseVersion || candidate.ConfigurationFingerprint != ConfigurationFingerprint)
            || diagnostics.Candidates.Select(candidate => candidate.ContractId).Order(StringComparer.Ordinal)
                .SequenceEqual(input.Candidates.Select(candidate => candidate.Contract.Id).Order(StringComparer.Ordinal)) == false)
            throw new ArgumentException("Research must use this engine's same-session diagnostic result.", nameof(diagnostics));
        // Preserve the strict uniqueness rejection before constructing identity-keyed lookups.
        // Neither conflicting record may be selected as the canonical contract by file order.
        if (input.Candidates.Select(candidate => candidate.Contract.Id).Distinct(StringComparer.Ordinal).Count() != input.Candidates.Count)
            return Array.AsReadOnly(diagnostics.Candidates.Select(candidate => new CandleCandidateAssessment(
                candidate.ContractId, candidate.SetupId, candidate.Direction, false, null,
                candidate.Gates, candidate.ReasonCodes)).ToArray());
        var selected = new List<CandleCandidateAssessment>();
        var optionsById = input.Candidates.ToDictionary(option => option.Contract.Id, StringComparer.Ordinal);
        var modelsById = financials.GroupBy(financial => financial.ContractId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var candidate in diagnostics.Candidates)
        {
            var gates = new GateBook();
            gates.Append(diagnostics.ContextGates.Where(gate => gate.Code is not
                ("PORTFOLIO_SNAPSHOT" or "RECOVERY_READY" or "RECORDER_COMPLETE" or "PAPER_AUTO_ENABLED"
                 or "SESSION_PAUSE_LATCH" or "SESSION_NET_LOSS" or "SESSION_NET_PNL" or "AVAILABLE_CASH"
                 or "PORTFOLIO_CAPACITY" or "INDEX_CAPACITY" or "PORTFOLIO_ATTEMPTS" or "INDEX_ATTEMPTS" or "SETUP_NOT_CONSUMED")));
            EvidenceChecks.Portfolio(gates, input.Portfolio, input.Session, input.AsOfUtc, configuration.Portfolio, EntryOrigin.STRATEGY);
            if (EvidenceChecks.PortfolioUsable(input.Portfolio, input.Session, input.AsOfUtc))
                CheckCapacity(gates, input.Portfolio, input.Index.Id, EntryOrigin.STRATEGY);
            gates.Append(candidate.Gates.Where(gate => gate.Code is "OPTION_CONTRACT_VALIDITY" or "OPTION_DEFINITION_UNAVAILABLE"
                or "CONTRACT_DATA_MODE" or "OPTION_DIRECTION" or "WHOLE_LOT_QUANTITY"));
            var setup = diagnostics.State.Setup;
            var matches = modelsById.GetValueOrDefault(candidate.ContractId) ?? [];
            var financial = matches.Length == 1 ? matches[0] : null;
            if (setup is not null)
            {
                var signal = input.FuturesBars.SingleOrDefault(bar => bar.Id == setup.SignalBarId);
                if (signal is null || signal.AvailableAtUtc > input.AsOfUtc || signal.EndUtc > input.AsOfUtc
                    || input.AsOfUtc >= setup.ExpiresAtUtc)
                    gates.Missing("RESEARCH_SIGNAL_UNAVAILABLE", "No future/expired setup may be borrowed from another snapshot.");
                gates.Require(!input.Portfolio.AcceptedAttempts.Any(attempt => attempt.SetupId == setup.Id),
                    "SETUP_NOT_CONSUMED", "Accepted research intent consumes this crossing even if canceled.");
                var contract = optionsById[candidate.ContractId].Contract;
                gates.Require(contract.IndexId == input.Index.Id && contract.ValidFromUtc <= input.AsOfUtc
                    && input.AsOfUtc < contract.ValidToUtc && contract.AvailableAtUtc <= input.AsOfUtc
                    && Enum.IsDefined(contract.Right) && contract.Strike > 0m
                    && ((setup.Direction == TradeDirection.Bullish) == (contract.Right == OptionRight.Call))
                    && contract.Expiry >= input.Session.TradingDate
                    && (!strategy.ExcludeExpiryDay || contract.Expiry > input.Session.TradingDate)
                    && contract.LotSize > 0 && contract.TickSize > 0m,
                    "RESEARCH_CURRENT_CONTRACT", "Recheck current contract, not a borrowed passing diagnostic gate.");
                if (financial is null || financial.AvailableAtUtc > input.AsOfUtc
                    || financial.AvailableAtUtc < financial.OptionCandle.AvailableAtUtc
                    || financial.OptionCandle.AvailableAtUtc > input.AsOfUtc || financial.OptionCandle.EndUtc > input.AsOfUtc
                    || input.AsOfUtc - financial.OptionCandle.EndUtc >= strategy.BarInterval
                    || financial.OptionCandle.InstrumentId != contract.Id || financial.OptionCandle.Volume <= 0m
                    || financial.ContractDefinitionVersion != contract.DefinitionVersion
                    || financial.SignalBarId != setup.SignalBarId || string.IsNullOrWhiteSpace(financial.CalculatorVersion))
                    gates.Missing("RESEARCH_FINANCIAL_EVIDENCE", "Require current completed, traded option candle and bound shared model calculations.");
                else
                {
                    if (!financial.FeeScheduleApplicable || financial.FeeProfileId != strategy.Risk.FeeProfileId
                        || string.IsNullOrWhiteSpace(financial.FeeScheduleVersion))
                        gates.Missing("RESEARCH_FEES_UNAVAILABLE", "Effective fee schedule required for cost-qualified research.");
                    gates.Require(financial.RequiredCash > 0m && input.Portfolio.AvailableCash >= financial.RequiredCash,
                        "RESEARCH_CASH", "Whole-lot model cash includes fees.", financial.RequiredCash, input.Portfolio.AvailableCash);
                    gates.Require(financial.ModelledNetRisk > 0m && financial.ModelledNetRisk <= strategy.Risk.MaximumNetRisk,
                        "RESEARCH_MODELLED_RISK", "Cash-stop/fee/slippage/gap-stress hypothesis; not a guaranteed risk bound.", financial.ModelledNetRisk, strategy.Risk.MaximumNetRisk);
                    gates.Require(financial.SpreadPercent >= 0m && financial.SpreadPercent <= strategy.Evidence.MaximumSpreadPercent,
                        "RESEARCH_MODELLED_SPREAD", "Declared spread, not observed liquidity.", financial.SpreadPercent, strategy.Evidence.MaximumSpreadPercent);
                    if (strategy.Risk.MaximumFrictionToStopRatio is { } ratio)
                        gates.Require(financial.Friction >= 0m && financial.Friction <= ratio * strategy.Risk.GrossCashStop,
                            "RESEARCH_FRICTION_TO_STOP", "Round-trip modelled friction must leave room inside the gross stop.",
                            financial.Friction, ratio * strategy.Risk.GrossCashStop);
                }
            }
            gates.Add("UNVERIFIED_EXECUTION", GateOutcome.NOT_EVALUATED,
                "Observed depth, bid/ask, structural option mapping and predicted opportunity are unavailable; no production ENTER.");
            var eligible = setup is not null && !gates.Failed && !gates.Unknown;
            selected.Add(new(candidate.ContractId, setup?.Id ?? "", setup?.Direction, eligible, null, gates.Snapshot(),
                gates.Failed || gates.Unknown ? EvidenceChecks.Reasons(gates.Snapshot()) : [eligible ? "HYPOTHETICAL_ELIGIBLE" : "NO_ACTIVE_SETUP"]));
        }
        var byId = financials.GroupBy(financial => financial.ContractId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        IOrderedEnumerable<CandleCandidateAssessment>? ranked = null;
        var eligibleCandidates = selected.Where(candidate => candidate.HypotheticallyEligible);
        foreach (var criterion in strategy.Ranking.Where(criterion => criterion != RankingCriterion.ObservedDepth))
        {
            Func<CandleCandidateAssessment, decimal> metric = criterion switch
            {
                RankingCriterion.SpreadPercentage => candidate => byId[candidate.ContractId].SpreadPercent,
                RankingCriterion.RoundTripFriction => candidate => byId[candidate.ContractId].Friction,
                _ => candidate => byId[candidate.ContractId].StrikeDistance
            };
            ranked = ranked is null ? eligibleCandidates.OrderBy(metric) : ranked.ThenBy(metric);
        }
        var ranks = (ranked is null ? eligibleCandidates.OrderBy(candidate => candidate.ContractId, StringComparer.Ordinal)
            : ranked.ThenBy(candidate => candidate.ContractId, StringComparer.Ordinal))
            .Select((candidate, i) => (candidate.ContractId, Rank: i + 1)).ToDictionary(item => item.ContractId, item => item.Rank);
        return Array.AsReadOnly(selected.Select(candidate => ranks.TryGetValue(candidate.ContractId, out var rank)
            ? candidate with { Rank = rank } : candidate).ToArray());
    }

    public PositionDecision EvaluateCandlePosition(PositionEvaluationInput input, CandlePositionEvidence evidence,
        PositionPolicyState? previousState = null)
    {
        if (input.Identity.Mode == EngineDataMode.PAPER_LIVE_DATA || input.OptionQuote is not null
            || input.FuturesPrice is not null || input.Progress is not null || evidence.PositionId != input.Position.Id
            || evidence.AvailableAtUtc > input.AsOfUtc || string.IsNullOrWhiteSpace(evidence.CalculatorVersion)
            || (evidence.OptionCandle is not null && evidence.OptionOpening is not null)
            || (evidence.OptionCandle is { } option && (option.InstrumentId != input.Position.Contract.Id
                || option.AvailableAtUtc > evidence.AvailableAtUtc || option.EndUtc > input.AsOfUtc || option.Volume <= 0m
                || option.StartUtc < input.Position.EnteredAtUtc))
            || (evidence.FuturesCandle is { } future && (future.InstrumentId != input.Position.FuturesInstrumentId
                || future.AvailableAtUtc > evidence.AvailableAtUtc || future.EndUtc > input.AsOfUtc
                || future.EndUtc <= input.Position.EnteredAtUtc
                || !input.FuturesBars.Contains(future)))
            || (evidence.OptionOpening is { } opening && (opening.InstrumentId != input.Position.Contract.Id
                || opening.TimeUtc < input.Position.EnteredAtUtc || opening.TimeUtc > evidence.AvailableAtUtc || opening.Open <= 0m)))
            throw new ArgumentException("Supply available candle research evidence separately from observed quotes.", nameof(evidence));
        var interval = input.Position.FrozenStrategy.BarInterval;
        var optionCurrent = evidence.OptionCandle is { } optionBar && CurrentResearchBar(optionBar, input.Session.Id, input.AsOfUtc, interval);
        var openingCurrent = evidence.OptionOpening is { } optionOpening && input.AsOfUtc - optionOpening.TimeUtc < interval;
        var futureCurrent = evidence.FuturesCandle is { } futureBar && CurrentResearchBar(futureBar, input.Session.Id, input.AsOfUtc, interval);
        // Republishing an old mark now cannot refresh its source price or excursion.
        // Keep timers and outstanding exits active, with unavailable monetary values.
        var currentEvidence = evidence with
        {
            OptionCandle = optionCurrent ? evidence.OptionCandle : null,
            OptionOpening = openingCurrent ? evidence.OptionOpening : null,
            GrossPnl = optionCurrent || openingCurrent ? evidence.GrossPnl : null,
            NetPnl = optionCurrent || openingCurrent ? evidence.NetPnl : null,
            FuturesCandle = futureCurrent ? evidence.FuturesCandle : null,
            MaximumProgressR = futureCurrent ? evidence.MaximumProgressR : null
        };
        return EvaluatePositionCore(input, previousState, currentEvidence) with { EvidenceClass = "CANDLE_RESEARCH" };
    }

    private static bool CurrentResearchBar(CompletedBarEvidence bar, string sessionId, DateTimeOffset asOf, TimeSpan interval) =>
        bar.SessionId == sessionId && bar.EndUtc - bar.StartUtc == interval && bar.AvailableAtUtc >= bar.EndUtc
        && bar.AvailableAtUtc <= asOf && bar.EndUtc <= asOf && asOf - bar.EndUtc < interval
        && bar.Volume > 0m && bar.Low > 0m && bar.High >= bar.Low
        && bar.Open >= bar.Low && bar.Open <= bar.High && bar.Close >= bar.Low && bar.Close <= bar.High;
}
