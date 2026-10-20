namespace Trading.Domain;

// Public synthetic-dashboard wire contracts. Nullable evidence is intentionally retained in JSON.
public sealed record ScenarioDescriptor(string Id, string Label, string Description);
public sealed record IndexCapabilities(bool FuturesChart, bool OptionComparison, bool WeightedConstituents);
public sealed record IndexDefinition(
    string Id, string DisplayName, string Exchange, string TimeZone, string ConstituentLabel,
    string DefinitionVersion, DateTimeOffset EffectiveFromUtc, DateTimeOffset? EffectiveToUtc,
    bool IsSynthetic, IndexCapabilities Capabilities)
{
    public IndexDefinition() : this("", "", "", "", "", "", default, null, false, null!) { }
}
public sealed record FixtureContract(
    string Id, string Symbol, string Exchange, string Underlying, string OptionType,
    string Expiry, decimal Strike, int LotSize, decimal TickSize,
    DateTimeOffset ValidFromUtc, DateTimeOffset ValidToUtc, bool IsSynthetic);
public sealed record Quote(
    decimal? LastPrice, decimal? Bid, decimal? Ask, int? BidQuantity, int? AskQuantity,
    DateTimeOffset? EventTimeUtc, DateTimeOffset? ReceivedAtUtc,
    DateTimeOffset? LastTradeTimeUtc, string State);
public sealed record IllustrativeDecision(string Action, IReadOnlyList<string> ReasonCodes);
public sealed record OptionRow(
    FixtureContract Contract, Quote Quote, decimal? Spread, decimal? QuoteAgeSeconds,
    IllustrativeDecision Decision);
public sealed record FixtureBar(
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset AvailableAtUtc,
    decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);
public sealed record BankRow(
    string Id, string Name, decimal? WeightPercent, decimal? LastPrice, decimal? Vwap,
    decimal? ChangePercent, bool? AboveVwap, string State,
    DateTimeOffset? EventTimeUtc, DateTimeOffset? ReceivedAtUtc);
public sealed record ConstituentRow(
    string Id, string Name, decimal? WeightPercent, decimal? LastPrice, decimal? Vwap,
    decimal? ChangePercent, bool? AboveVwap, string State,
    DateTimeOffset? EventTimeUtc, DateTimeOffset? ReceivedAtUtc);
public sealed record SnapshotFreshness(
    string State, decimal? QuoteAgeSeconds, decimal? ReceiptAgeSeconds, string Message);
public sealed record MarketContext(
    string IndexName, decimal? IndexValue, string FuturesSymbol,
    decimal? FuturesValue, decimal? ChangePercent);
public sealed record VwapPoint(DateTimeOffset TimeUtc, decimal Value);
public sealed record ChartContext(
    string Symbol, IReadOnlyList<FixtureBar> Bars, decimal? OpeningRangeHigh,
    decimal? OpeningRangeLow, IReadOnlyList<VwapPoint> Vwap, string VwapLabel);
public sealed record ConstituentParticipation(
    decimal? AboveVwapWeightPercent, decimal? BelowVwapWeightPercent,
    decimal MissingWeightPercent, string CoverageLabel);
public sealed record PresetPosition(
    string State, string? PositionId, string? ContractId, int? Lots, int? Units,
    decimal? EntryPrice, decimal? CurrentBid, decimal? GrossPnl, decimal? NetPnl, string Message);
public sealed record PlanningAvailability(
    decimal? RoundTripFees, decimal? RequiredCash, decimal? NetRisk, string AvailabilityReason);
public sealed record DashboardSnapshot(
    string SchemaVersion, string FixtureVersion, string SnapshotId, string ScenarioId,
    string ScenarioLabel, int Sequence, DateTimeOffset AsOfUtc, string Source, string Mode,
    SnapshotFreshness Freshness, MarketContext Market, ChartContext Chart,
    IReadOnlyList<ConstituentRow> Banks, ConstituentParticipation Participation,
    IReadOnlyList<OptionRow> Options, PresetPosition Position, PlanningAvailability Planning,
    string? IndexId = null, IReadOnlyList<ConstituentRow>? Constituents = null);

public sealed record PlanSelection(string IndexId, string ScenarioId, string SnapshotId, string ContractId, int Lots);
public sealed record PlanPreview(
    int Lots, int Units, decimal? ReferenceAsk, string QuoteState, PlanningAvailability Planning);
public sealed record SavePlanRequest(
    string IndexId, string ScenarioId, string SnapshotId, string ContractId, int Lots, string Status,
    string Thesis, string StopPlan, string Invalidation, string Note);
public sealed record JournalEntry(
    string Id, DateTimeOffset SavedAtUtc, string Mode, string ScenarioId, string SnapshotId,
    string ContractId, int Lots, int Units, string Status, string Thesis, string StopPlan,
    string Invalidation, string Note, DashboardSnapshot Snapshot, string? IndexId = null);
