using Trading.Analytics;
using Trading.Domain;

namespace Trading.Engine;

public enum DirectionalStatus { UP, DOWN, NO_SETUP, INSUFFICIENT_DATA, NOT_EVALUATED }

public sealed record DirectionalEvidence
{
    public string Version { get; init; } = DirectionalAnalytics.Version;
    public string BreadthPolicyVersion { get; init; } = "";
    public string IndexId { get; init; } = "";
    public string FuturesInstrumentId { get; init; } = "";
    public DateTimeOffset AsOfUtc { get; init; }
    public EvaluationIdentity Identity { get; init; } = null!;
    public string ConfigurationFingerprint { get; init; } = "";
    public DirectionalStatus Status { get; init; }
    public TradeDirection? Direction { get; init; }
    public string? SetupId { get; init; }
    public string? BarId { get; init; }
    public DateTimeOffset? BarEndUtc { get; init; }
    public DateTimeOffset? EvidenceAvailableAtUtc { get; init; }
    public decimal? Close { get; init; }
    public decimal? OpeningHigh { get; init; }
    public decimal? OpeningLow { get; init; }
    public decimal? Vwap { get; init; }
    public decimal? CloseMinusOpeningHigh { get; init; }
    public decimal? CloseMinusOpeningLow { get; init; }
    public decimal? CloseMinusVwap { get; init; }
    public decimal? Return1MinutePercent { get; init; }
    public decimal? Return5MinutePercent { get; init; }
    public decimal? AboveWeightPercent { get; init; }
    public decimal? BelowWeightPercent { get; init; }
    public decimal? MissingWeightPercent { get; init; }
    public int? ConfirmingCount { get; init; }
    public int? OpposingCount { get; init; }
    public int? NeutralCount { get; init; }
    public decimal? TopThreeConfirmingWeightPercent { get; init; }
    public string? ConstituentDefinitionVersion { get; init; }
    public IReadOnlyList<ConstituentRow> Constituents { get; init; } = [];
    public IReadOnlyList<string> InputBarIds { get; init; } = [];
    public IReadOnlyList<GateResult> Gates { get; init; } = [];
    public bool MeetsDirectionalRules { get; init; }
}

public sealed partial class TradingDecisionEngine
{
    private DirectionalEvidence DirectionalSnapshot(IndexEvaluationInput input, IndexStrategyConfiguration strategy,
        StrategyState state, IReadOnlyList<CompletedBarEvidence> bars, ChartContext? chart)
    {
        var snapshot = new DirectionalEvidence { IndexId = input.Index.Id, FuturesInstrumentId = input.FuturesInstrumentId,
            AsOfUtc = input.AsOfUtc, Identity = input.Identity, ConfigurationFingerprint = ConfigurationFingerprint,
            SetupId = state.Setup?.Id, Direction = state.Setup?.Direction, BreadthPolicyVersion = strategy.BreadthPolicyVersion };
        if (input.Origin != EntryOrigin.STRATEGY) return snapshot with { Status = DirectionalStatus.NOT_EVALUATED };
        var gates = new GateBook();
        var checkedBars = EvidenceChecks.Bars(bars, input.FuturesInstrumentId, input.Session, input.AsOfUtc, strategy, gates);
        if (checkedBars.Length == 0 || gates.Unknown)
        {
            gates.Missing("DIRECTIONAL_BARS_UNAVAILABLE", "Require canonical available completed session bars.");
            return snapshot with { Status = DirectionalStatus.INSUFFICIENT_DATA, Gates = gates.Snapshot() };
        }
        chart ??= DashboardAnalytics.CompletedFuturesChart(input.FuturesInstrumentId,
            checkedBars.Select(bar => new FixtureBar(bar.StartUtc, bar.EndUtc, bar.AvailableAtUtc,
                bar.Open, bar.High, bar.Low, bar.Close, bar.Volume)), input.AsOfUtc,
            input.Session.At(strategy.OpeningStart), input.Session.At(strategy.OpeningEnd));
        if (chart.OpeningRangeHigh is null || chart.OpeningRangeLow is null)
            gates.Missing("OPENING_RANGE_INCOMPLETE", "Require the complete opening range.");
        else
            // Cash/portfolio uncertainty may prevent entry-state advancement. Explain the available
            // crossing independently using the same detector, without authorizing or consuming it.
            state = AdvanceSetup(input, strategy, state, checkedBars, chart.OpeningRangeHigh.Value, chart.OpeningRangeLow.Value, gates);
        if (state.Setup is { } expired && input.AsOfUtc >= expired.ExpiresAtUtc) state = state with { Setup = null };
        snapshot = snapshot with { SetupId = state.Setup?.Id, Direction = state.Setup?.Direction };
        var bar = state.Setup is { } setup ? checkedBars.SingleOrDefault(bar => bar.Id == setup.SignalBarId) : checkedBars[^1];
        if (bar is null)
        {
            gates.Missing("DIRECTIONAL_BARS_UNAVAILABLE", "The frozen signal bar must remain in the available prefix.");
            return snapshot with { Status = DirectionalStatus.INSUFFICIENT_DATA, Gates = gates.Snapshot() };
        }
        if (input.AsOfUtc - bar.EndUtc >= strategy.BarInterval + strategy.Evidence.MaximumObservationAge)
            gates.Missing("DIRECTIONAL_BAR_STALE", "An old completed bar is not current directional evidence.");
        var feature = EvidenceChecks.Feature(input.FuturesFeatures, bar, checkedBars, configuration.Version, input.AsOfUtc, gates);
        var participation = EvidenceChecks.Confirmation(input.ConstituentDefinitions, input.Constituents,
            bar, input.AsOfUtc, strategy, gates, out var rows);
        var partial = rows.Count == 0 ? null : DashboardAnalytics.Participation(rows);
        var breadth = participation is not null && state.Setup is { } active
            ? DirectionalAnalytics.Breadth(rows, active.Direction == TradeDirection.Bullish) : null;
        if (participation is not null && state.Setup is { } signal)
            EvidenceChecks.DirectionalConfirmation(participation, signal.Direction, strategy, gates);
        if (state.Setup is not null && strategy.MinimumConfirmingConstituents is { } minimum)
        {
            if (breadth is null) gates.Missing("CONSTITUENT_BREADTH", "Complete same-period constituent evidence is required for the optional count rule.");
            else gates.Require(breadth.ConfirmingCount >= minimum, "CONSTITUENT_BREADTH",
                $"{strategy.BreadthPolicyVersion}: minimum confirming count supplements absolute weighted confirmation.", breadth.ConfirmingCount, minimum);
        }
        var prices = DirectionalAnalytics.Prices(bar.Close, chart.OpeningRangeHigh, chart.OpeningRangeLow, feature?.Vwap,
            checkedBars.SingleOrDefault(previous => previous.EndUtc == bar.EndUtc.AddMinutes(-1))?.Close,
            checkedBars.SingleOrDefault(previous => previous.EndUtc == bar.EndUtc.AddMinutes(-5))?.Close);
        var qualifies = state.Setup is not null && !gates.Unknown && !gates.Failed;
        return snapshot with
        {
            Status = gates.Unknown ? DirectionalStatus.INSUFFICIENT_DATA : !qualifies ? DirectionalStatus.NO_SETUP
                : state.Setup!.Direction == TradeDirection.Bullish ? DirectionalStatus.UP : DirectionalStatus.DOWN,
            BarId = bar.Id, BarEndUtc = bar.EndUtc,
            EvidenceAvailableAtUtc = new[] { bar.AvailableAtUtc, feature?.AvailableAtUtc ?? bar.AvailableAtUtc,
                rows.Count == 0 ? bar.AvailableAtUtc : input.ConstituentDefinitions!.AvailableAtUtc }
                .Concat(input.Constituents.Where(row => row.StartUtc == bar.StartUtc && row.EndUtc == bar.EndUtc
                    && row.AvailableAtUtc <= input.AsOfUtc && rows.Any(member => member.Id == row.InstrumentId))
                    .Select(row => row.AvailableAtUtc)).Max(),
            Close = bar.Close, OpeningHigh = chart.OpeningRangeHigh, OpeningLow = chart.OpeningRangeLow, Vwap = feature?.Vwap,
            CloseMinusOpeningHigh = prices.CloseMinusOpeningHigh, CloseMinusOpeningLow = prices.CloseMinusOpeningLow,
            CloseMinusVwap = prices.CloseMinusVwap, Return1MinutePercent = prices.Return1MinutePercent, Return5MinutePercent = prices.Return5MinutePercent,
            AboveWeightPercent = partial?.AboveVwapWeightPercent, BelowWeightPercent = partial?.BelowVwapWeightPercent,
            MissingWeightPercent = partial?.MissingWeightPercent, ConfirmingCount = breadth?.ConfirmingCount,
            OpposingCount = breadth?.OpposingCount, NeutralCount = breadth?.NeutralCount,
            TopThreeConfirmingWeightPercent = breadth?.TopThreeConfirmingWeightPercent,
            ConstituentDefinitionVersion = rows.Count == 0 ? null : input.ConstituentDefinitions?.Version,
            Constituents = rows, InputBarIds = Array.AsReadOnly(checkedBars.Where(input => input.EndUtc <= bar.EndUtc).Select(input => input.Id).ToArray()),
            Gates = gates.Snapshot(), MeetsDirectionalRules = qualifies
        };
    }
}
