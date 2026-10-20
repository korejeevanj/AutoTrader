using Trading.Risk;

namespace Trading.Engine;

public sealed partial class TradingDecisionEngine
{
    public AllocationResult Allocate(EvaluationIdentity identity, DateTimeOffset asOfUtc,
        EngineSession session, PortfolioEvidence portfolio, IReadOnlyList<IndexEvaluationResult> indices,
        PortfolioPolicyState? previousState = null, IPortfolioReservationAssessor? reservationAssessor = null)
    {
        EvidenceChecks.ValidateContext(identity, asOfUtc, session);
        ArgumentNullException.ThrowIfNull(portfolio);
        ArgumentNullException.ThrowIfNull(indices);
        var state = previousState ?? new PortfolioPolicyState();
        if (state.SessionId is not null && state.SessionId != session.Id)
            throw new ArgumentException("Portfolio policy state belongs to another session.", nameof(previousState));
        if (state.LastEvaluationUtc > asOfUtc)
            throw new ArgumentException("Future portfolio-policy state cannot be used at an earlier evaluation time.", nameof(previousState));
        var portfolioUsable = EvidenceChecks.PortfolioUsable(portfolio, session, asOfUtc);
        var paused = state.PauseLatched || (portfolioUsable && (portfolio.PauseLatched
            || portfolio.NetLiquidationSessionPnl <= -configuration.Portfolio.NetSessionPauseLoss));
        state = new(session.Id, paused, asOfUtc);
        var candidates = indices.SelectMany(index => index.Candidates).ToArray();
        if (candidates.Select(candidate => (candidate.IndexId, candidate.ContractId)).Distinct().Count() != candidates.Length)
            throw new ArgumentException("Supply one canonical candidate set per index.", nameof(indices));
        if (candidates.Any(candidate => candidate.ConfigurationVersion != configuration.Version
            || candidate.ConfigurationFingerprint != ConfigurationFingerprint
            || candidate.Identity != identity || candidate.DecisionTimeUtc != asOfUtc
            || candidate.SessionId != session.Id || candidate.PortfolioVersion != portfolio.Version))
            throw new ArgumentException("Allocation requires same-time decisions bound to this run, configuration and portfolio version.", nameof(indices));
        var results = candidates.ToDictionary(candidate => (candidate.IndexId, candidate.ContractId));
        var selected = new List<CandidateAssessment>();
        var priorities = configuration.Portfolio.IndexPriority.Select((id, i) => (id, i))
            .ToDictionary(item => item.id, item => item.i, StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(candidate => candidate.Eligible)
            .OrderBy(candidate => priorities.GetValueOrDefault(candidate.IndexId, int.MaxValue))
            .ThenBy(candidate => candidate.IndexId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Rank).ThenBy(candidate => candidate.ContractId, StringComparer.Ordinal))
        {
            var gates = new GateBook();
            gates.Append(candidate.Gates);
            EvidenceChecks.Portfolio(gates, portfolio, session, asOfUtc, configuration.Portfolio, candidate.Origin);
            if (paused) gates.Fail("SESSION_PAUSE_LATCH", "Session loss pause is latched until a new session.");
            if (portfolioUsable) CheckCapacity(gates, portfolio, candidate.IndexId, candidate.Origin);
            if (gates.Failed || gates.Unknown)
            {
                results[(candidate.IndexId, candidate.ContractId)] = WithAllocation(candidate, gates,
                    EvidenceChecks.Action(gates), false);
                continue;
            }
            var occupied = portfolio.ActiveOrPendingEntries.Count + selected.Count;
            var indexOccupied = portfolio.ActiveOrPendingEntries.Count(slot => slot.IndexId == candidate.IndexId)
                + selected.Count(proposal => proposal.IndexId == candidate.IndexId);
            var strategyAttempts = portfolio.AcceptedAttempts.DistinctBy(attempt => attempt.IntentId, StringComparer.Ordinal)
                .Count(attempt => attempt.Origin == EntryOrigin.STRATEGY)
                + selected.Count(proposal => proposal.Origin == EntryOrigin.STRATEGY);
            var indexAttempts = portfolio.AcceptedAttempts.DistinctBy(attempt => attempt.IntentId, StringComparer.Ordinal)
                .Count(attempt => attempt.Origin == EntryOrigin.STRATEGY && attempt.IndexId == candidate.IndexId)
                + selected.Count(proposal => proposal.Origin == EntryOrigin.STRATEGY && proposal.IndexId == candidate.IndexId);
            var manualAttempts = portfolio.AcceptedAttempts.DistinctBy(attempt => attempt.IntentId, StringComparer.Ordinal)
                .Count(attempt => attempt.Origin == EntryOrigin.MANUAL_TEST)
                + selected.Count(proposal => proposal.Origin == EntryOrigin.MANUAL_TEST);
            if (occupied >= configuration.Portfolio.MaximumActiveOrPendingEntries
                || indexOccupied >= configuration.Portfolio.MaximumActiveOrPendingEntriesPerIndex
                || selected.Any(proposal => proposal.SetupId == candidate.SetupId)
                || (candidate.Origin == EntryOrigin.STRATEGY && (strategyAttempts >= configuration.Portfolio.MaximumStrategyAttempts
                    || indexAttempts >= configuration.Portfolio.MaximumStrategyAttemptsPerIndex))
                || (candidate.Origin == EntryOrigin.MANUAL_TEST && manualAttempts >= configuration.Portfolio.MaximumManualAttempts))
            {
                gates.Add("ALLOCATION_NOT_SELECTED", GateOutcome.NOT_EVALUATED, "Higher-priority proposals consume this batch's shared capacity/attempt allowance.");
                results[(candidate.IndexId, candidate.ContractId)] = WithAllocation(candidate, gates, EntryAction.WATCH, true, "ALLOCATION_NOT_SELECTED");
                continue;
            }
            var proposedRisk = selected.Select(proposal => proposal.FinancialEvidence!).Append(candidate.FinancialEvidence!).ToArray();
            decimal? requiredCash = candidate.FinancialEvidence!.RequiredCash;
            if (selected.Count != 0)
            {
                var assessment = reservationAssessor?.Assess(portfolio, configuration.Version,
                    Array.AsReadOnly(proposedRisk), asOfUtc);
                if (assessment is null || assessment.PortfolioVersion != portfolio.Version
                    || assessment.ConfigurationVersion != configuration.Version
                    || string.IsNullOrWhiteSpace(assessment.CalculatorVersion)
                    || assessment.RiskAssessmentIds is null
                    || !assessment.RiskAssessmentIds.Order(StringComparer.Ordinal)
                        .SequenceEqual(proposedRisk.Select(risk => risk.Id).Order(StringComparer.Ordinal)))
                    requiredCash = null;
                else requiredCash = assessment.RequiredCash;
            }
            if (requiredCash is null or <= 0m)
            {
                gates.Missing("PORTFOLIO_RESERVATION_ASSESSMENT", "Multiple allocations need shared-Risk aggregate cash assessment, not duplicated Engine money arithmetic.");
                results[(candidate.IndexId, candidate.ContractId)] = WithAllocation(candidate, gates, EntryAction.INSUFFICIENT_DATA, false);
                continue;
            }
            var premiumFloor = IndicativePlanning.LongPremiumCash(proposedRisk.Select(risk => (risk.AssumedEntryPrice ?? 0m, risk.Units)));
            if (premiumFloor is null)
                gates.Missing("PORTFOLIO_PREMIUM_CASH_FLOOR", "Combined whole-lot purchase debit is unavailable/unrepresentable.");
            else gates.Require(requiredCash >= premiumFloor, "PORTFOLIO_PREMIUM_CASH_FLOOR",
                "Long option purchase premiums cannot be netted away as a cross-index hedge or margin credit.", requiredCash, premiumFloor);
            if (gates.Failed || gates.Unknown)
            {
                results[(candidate.IndexId, candidate.ContractId)] = WithAllocation(candidate, gates, EvidenceChecks.Action(gates), false);
                continue;
            }
            if (portfolio.AvailableCash < requiredCash)
            {
                gates.Add("ALLOCATION_CASH_NOT_SELECTED", GateOutcome.NOT_EVALUATED, "Combined proposed reservations exceed available cash.", requiredCash, portfolio.AvailableCash);
                results[(candidate.IndexId, candidate.ContractId)] = WithAllocation(candidate, gates, EntryAction.WATCH, true, "ALLOCATION_CASH_NOT_SELECTED");
                continue;
            }
            gates.Add("PORTFOLIO_SELECTED", GateOutcome.PASS, "Recommendation only: host must atomically revalidate and reserve this portfolio version.", requiredCash, portfolio.AvailableCash);
            var decision = WithAllocation(candidate, gates, EntryAction.ENTER, true, "ENTER_QUALIFIED");
            selected.Add(decision);
            results[(candidate.IndexId, candidate.ContractId)] = decision;
        }
        return new(Array.AsReadOnly(results.Values.OrderBy(candidate => candidate.IndexId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ContractId, StringComparer.Ordinal).ToArray()), state, portfolio.Version,
            paused && portfolioUsable && portfolio.ActiveOrPendingEntries.Count > 0);
    }

    private static CandidateAssessment WithAllocation(CandidateAssessment candidate, GateBook gates,
        EntryAction action, bool eligible, string? reason = null) => candidate with
    {
        Action = action,
        Eligible = eligible,
        Gates = gates.Snapshot(),
        ReasonCodes = reason is null ? EvidenceChecks.Reasons(gates.Snapshot()) : [reason]
    };
}
