using Trading.Domain;

namespace Trading.Engine;

public enum EntryAction { ENTER, WATCH, SKIP, INSUFFICIENT_DATA }
public enum PositionAction { HOLD, WATCH_EXIT, EXIT, TIGHTEN_STOP }
public enum GateOutcome { PASS, FAIL, UNKNOWN, NOT_EVALUATED }
public enum TradeDirection { Bullish, Bearish }
public enum EntryOrigin { STRATEGY, MANUAL_TEST }
public enum OptionRight { Call, Put }
public enum EngineDataMode { SYNTHETIC_TEST, PAPER_LIVE_DATA, PAPER_REPLAY }

public sealed record GateResult(string Code, GateOutcome Outcome, string Detail,
    decimal? Observed = null, decimal? Threshold = null);

// The host resolves the exchange calendar/timezone. No timezone or wall clock is read here.
public sealed record EngineSession(string Id, DateOnly TradingDate, DateTimeOffset LocalMidnightUtc)
{
    public DateTimeOffset At(TimeOnly localTime) => LocalMidnightUtc.Add(localTime.ToTimeSpan());
}

public sealed record EvaluationIdentity(string RunId, string EvidenceVersion, string CodeVersion,
    EngineDataMode Mode = EngineDataMode.SYNTHETIC_TEST);

public sealed record CompletedBarEvidence(string Id, string InstrumentId, string SessionId,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset AvailableAtUtc,
    decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

// Internal, host-supplied Analytics/Risk results; never accept these as client assertions.
// InputBarIds binds a feature to its complete available session prefix, not chart coordinates.
public sealed record FuturesFeatureEvidence(string Id, string BarId, string SessionId,
    string ConfigurationVersion, DateTimeOffset AvailableAtUtc,
    IReadOnlyList<string> InputBarIds, decimal? Vwap,
    int StructuralLookbackBars, decimal StructuralBufferMultiplier,
    decimal? LongStructuralStop, decimal? LongInitialRisk,
    decimal? ShortStructuralStop, decimal? ShortInitialRisk,
    string CalculatorVersion, bool CompleteSessionCoverage);

public sealed record ConstituentMember(string InstrumentId, decimal? WeightPercent);
public sealed record ConstituentDefinitionEvidence(string Version, DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc, DateTimeOffset AvailableAtUtc, bool Verified,
    IReadOnlyList<ConstituentMember> Members);
public sealed record ConstituentBarEvidence(string InstrumentId, string SessionId,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset AvailableAtUtc,
    decimal? Close, decimal? Vwap, bool CompleteSessionCoverage, string CalculatorVersion);

public sealed record QuoteEvidence(string Id, string InstrumentId,
    decimal? Bid, decimal? Ask, int? BidQuantity, int? AskQuantity,
    DateTimeOffset? EventTimeUtc, DateTimeOffset? ReceivedAtUtc,
    DateTimeOffset? BidAskObservedAtUtc, DateTimeOffset AvailableAtUtc,
    string SourceVersion, long Sequence);

// Futures last-trade age is explicit: a new packet is not a new underlying trade.
public sealed record UnderlyingPriceEvidence(string Id, string InstrumentId, decimal? Price,
    DateTimeOffset? LastTradeTimeUtc, DateTimeOffset? ReceivedAtUtc,
    DateTimeOffset AvailableAtUtc, string SourceVersion);

public sealed record OptionContractEvidence(string Id, string IndexId, string Exchange,
    OptionRight Right, DateOnly Expiry, decimal Strike, int LotSize, decimal TickSize,
    DateTimeOffset ValidFromUtc, DateTimeOffset ValidToUtc, DateTimeOffset AvailableAtUtc,
    string DefinitionVersion, bool IsSynthetic);
public sealed record FuturesContractEvidence(string Id, string IndexId, DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidToUtc, DateTimeOffset AvailableAtUtc, string DefinitionVersion);

// Financial arithmetic lives in shared Risk. Assessment validity must be rechecked per quote/config.
public sealed record EntryRiskEvidence(string Id, string ContractId, string ContractDefinitionVersion,
    string QuoteId, string? SignalBarId, string ConfigurationVersion, int Units,
    DateTimeOffset AvailableAtUtc, DateTimeOffset ValidUntilUtc,
    string FeeProfileId, string FeeScheduleVersion, DateTimeOffset FeeEffectiveFromUtc,
    DateTimeOffset? FeeEffectiveToUtc, string CalculatorVersion, string FillModelVersion,
    decimal EntryAdverseSlippage, decimal ExitAdverseSlippage, decimal GrossStopTrigger,
    bool CredibleOptionRisk, bool CredibleOpportunity, bool AdverseTickRounding,
    decimal? AssumedEntryPrice, decimal? RoundTripFees, decimal? RequiredCash,
    decimal? NetRisk, decimal? ScenarioNetProfit, decimal? SpreadPercent,
    decimal? RoundTripFriction, decimal? AbsoluteStrikeDistance,
    string? UnderlyingQuoteId = null, string? StructuralFeatureId = null);

public sealed record OptionCandidateEvidence(OptionContractEvidence Contract, QuoteEvidence? Quote,
    EntryRiskEvidence? Risk);
public sealed record ActivePortfolioSlot(string Id, string IndexId, EntryOrigin Origin);
public sealed record AcceptedEntryAttempt(string IntentId, string IndexId, string SessionId,
    string SetupId, EntryOrigin Origin, DateTimeOffset AcceptedAtUtc);

// AvailableCash is already net of existing reservations. Session net P&L includes all fees once.
public sealed record PortfolioEvidence(string Version, string SessionId, DateTimeOffset AvailableAtUtc,
    decimal? AvailableCash, decimal? NetLiquidationSessionPnl, bool PauseLatched,
    bool AutoPaused, bool RecoveryReady, bool RecordingComplete,
    IReadOnlyList<ActivePortfolioSlot> ActiveOrPendingEntries,
    IReadOnlyList<AcceptedEntryAttempt> AcceptedAttempts);

public sealed record IndexEvaluationInput(EvaluationIdentity Identity, DateTimeOffset AsOfUtc,
    IndexDefinition Index, EngineSession Session, string FuturesInstrumentId,
    IReadOnlyList<CompletedBarEvidence> FuturesBars,
    IReadOnlyList<FuturesFeatureEvidence> FuturesFeatures,
    ConstituentDefinitionEvidence? ConstituentDefinitions,
    IReadOnlyList<ConstituentBarEvidence> Constituents,
    UnderlyingPriceEvidence? FuturesPrice, string UniverseVersion,
    DateTimeOffset UniverseAvailableAtUtc, IReadOnlyList<OptionCandidateEvidence> Candidates,
    PortfolioEvidence Portfolio, EntryOrigin Origin = EntryOrigin.STRATEGY,
    string? ManualRequestId = null, string? ManualContractId = null,
    FuturesContractEvidence? FuturesContract = null);

public sealed record ActiveSetup(string Id, string SignalBarId, TradeDirection Direction,
    DateTimeOffset SignalEndUtc, DateTimeOffset ExpiresAtUtc);
public sealed record StrategyState(string? IndexId = null, string? SessionId = null,
    string? ConfigurationVersion = null, DateTimeOffset? LastProcessedBarEndUtc = null,
    bool Armed = true, ActiveSetup? Setup = null, string? ConfigurationFingerprint = null,
    DateTimeOffset? LastEvaluationUtc = null);

public sealed record CandidateAssessment(string DecisionId, string SetupId, string IndexId,
    string ContractId, DateOnly Expiry, TradeDirection? Direction, EntryOrigin Origin,
    DateTimeOffset DecisionTimeUtc, string ConfigurationVersion, string StrategyKind,
    EvaluationIdentity Identity, string UniverseVersion, string PortfolioVersion,
    string SessionId, EntryAction Action, int Lots, int? Units,
    string? QuoteId, decimal? QuoteAgeSeconds, EntryRiskEvidence? FinancialEvidence,
    IReadOnlyList<GateResult> Gates, IReadOnlyList<string> ReasonCodes,
    bool Eligible, int? Rank, string ConfigurationFingerprint = "");

public sealed record IndexEvaluationResult(StrategyState State,
    IReadOnlyList<CandidateAssessment> Candidates, IReadOnlyList<GateResult> ContextGates,
    DirectionalEvidence? DirectionalEvidence = null);
public sealed record PortfolioPolicyState(string? SessionId = null, bool PauseLatched = false,
    DateTimeOffset? LastEvaluationUtc = null);
public sealed record AllocationResult(IReadOnlyList<CandidateAssessment> Decisions,
    PortfolioPolicyState State, string ExpectedPortfolioVersion, bool RequestPositionClosure);

public sealed record ReservationAssessment(string PortfolioVersion, string ConfigurationVersion,
    IReadOnlyList<string> RiskAssessmentIds, decimal? RequiredCash, string CalculatorVersion);

// A host adapter to shared Risk; evaluation must be synchronous, deterministic and I/O-free.
// Needed only when allocating more than one new reservation against the same snapshot.
public interface IPortfolioReservationAssessor
{
    ReservationAssessment Assess(PortfolioEvidence portfolio, string configurationVersion,
        IReadOnlyList<EntryRiskEvidence> proposedReservations, DateTimeOffset asOfUtc);
}

public sealed record PaperPositionEvidence(string Id, string IndexId, string SessionId,
    string EntryFillVersion, OptionContractEvidence Contract, TradeDirection Direction,
    DateTimeOffset EnteredAtUtc, decimal EntryPrice, int Units, decimal ActualEntryFees,
    string ConfigurationVersion, IndexStrategyConfiguration FrozenStrategy,
    string FuturesInstrumentId, decimal? StructuralStop, decimal? InitialUnderlyingRisk,
    decimal? UnderlyingEntryReference, decimal ExitAdverseSlippage = 0m);

public sealed record NetLiquidationEvidence(string Id, string PositionId, string EntryFillVersion,
    string QuoteId, DateTimeOffset AvailableAtUtc, DateTimeOffset ValidUntilUtc,
    string CalculatorVersion, string FeeScheduleVersion, decimal GrossPnl,
    decimal ActualEntryFees, decimal ProjectedExitFees, decimal NetPnl);

public sealed record UnderlyingProgressEvidence(string Id, string PositionId, string UnderlyingQuoteId,
    DateTimeOffset AvailableAtUtc, string CalculatorVersion, decimal InitialRisk,
    decimal EntryReference, decimal ProgressR);

public sealed record PositionEvaluationInput(EvaluationIdentity Identity, DateTimeOffset AsOfUtc,
    EngineSession Session, PaperPositionEvidence Position, QuoteEvidence? OptionQuote,
    NetLiquidationEvidence? NetLiquidation, UnderlyingPriceEvidence? FuturesPrice,
    UnderlyingProgressEvidence? Progress, IReadOnlyList<CompletedBarEvidence> FuturesBars,
    IReadOnlyList<FuturesFeatureEvidence> FuturesFeatures,
    ConstituentDefinitionEvidence? ConstituentDefinitions,
    IReadOnlyList<ConstituentBarEvidence> Constituents, PortfolioEvidence Portfolio);

public sealed record PositionPolicyState(string? PositionId = null,
    DateTimeOffset? LastProcessedBarEndUtc = null, int WrongSideCloses = 0,
    decimal? MaximumFavorableProgressR = null, bool ExitLatched = false,
    IReadOnlyList<string>? LatchedExitReasons = null, decimal? PeakNetLiquidationPnl = null,
    bool ProfitTrailArmed = false, decimal? ProfitTrailThreshold = null, DateTimeOffset? LastEvaluationUtc = null,
    string? PositionFingerprint = null, DateTimeOffset? SoftWeakeningSinceUtc = null,
    DateTimeOffset? SoftWeakeningDeadlineUtc = null)
{
    // Hosts seeding entry-time state must bind it to the same frozen evidence as later evaluations.
    public static PositionPolicyState ForPosition(PaperPositionEvidence position) => new(
        PositionId: position.Id, PositionFingerprint: FrozenPositionFingerprint.Create(position));
}
public sealed record PositionDecision(string DecisionId, string PositionId, DateTimeOffset DecisionTimeUtc,
    PositionAction Action, decimal? GrossLiquidationPnl, decimal? NetLiquidationPnl,
    IReadOnlyList<string> ReasonCodes, IReadOnlyList<GateResult> Gates,
    string ConfigurationVersion, EvaluationIdentity Identity, PositionPolicyState State,
    string EvidenceClass = "OBSERVED_QUOTE_POLICY", IReadOnlyList<WatchResolution>? WatchResolutions = null);

public sealed record WatchResolution(string ReasonCode, string ResolvingEvent, DateTimeOffset? DeadlineUtc);
