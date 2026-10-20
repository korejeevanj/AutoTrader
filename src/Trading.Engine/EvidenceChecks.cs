using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trading.Analytics;
using Trading.Domain;

namespace Trading.Engine;

internal sealed class GateBook
{
    private readonly List<GateResult> gates = [];
    public bool Failed => gates.Any(gate => gate.Outcome == GateOutcome.FAIL);
    public bool Unknown => gates.Any(gate => gate.Outcome == GateOutcome.UNKNOWN);
    public void Add(string code, GateOutcome outcome, string detail, decimal? observed = null, decimal? threshold = null) =>
        gates.Add(new(code, outcome, detail, observed, threshold));
    public void Pass(string code, string detail) => Add(code, GateOutcome.PASS, detail);
    public void Fail(string code, string detail) => Add(code, GateOutcome.FAIL, detail);
    public void Missing(string code, string detail) => Add(code, GateOutcome.UNKNOWN, detail);
    public void Require(bool condition, string code, string detail, decimal? observed = null, decimal? threshold = null) =>
        Add(code, condition ? GateOutcome.PASS : GateOutcome.FAIL, detail, observed, threshold);
    public IReadOnlyList<GateResult> Snapshot() => gates.AsReadOnly();
    public void Append(IEnumerable<GateResult> source) => gates.AddRange(source);
}

internal static class EvidenceChecks
{
    public static string Key(params string[] parts) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));

    public static void ValidateContext(EvaluationIdentity identity, DateTimeOffset asOf, EngineSession session)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(identity.RunId) || string.IsNullOrWhiteSpace(identity.EvidenceVersion)
            || string.IsNullOrWhiteSpace(identity.CodeVersion) || !Enum.IsDefined(identity.Mode)
            || string.IsNullOrWhiteSpace(session.Id) || asOf.Offset != TimeSpan.Zero
            || session.LocalMidnightUtc.Offset != TimeSpan.Zero
            || asOf < session.LocalMidnightUtc || asOf >= session.LocalMidnightUtc.AddDays(1))
            throw new ArgumentException("Supply nonempty run/evidence/code/session identities and UTC time within the supplied session.");
    }

    public static CompletedBarEvidence[] Bars(IReadOnlyList<CompletedBarEvidence> source,
        string instrument, EngineSession session, DateTimeOffset asOf,
        IndexStrategyConfiguration strategy, GateBook gates)
    {
        ArgumentNullException.ThrowIfNull(source);
        var start = session.At(strategy.OpeningStart);
        var bars = source.Where(bar => bar.InstrumentId == instrument && bar.SessionId == session.Id
            && bar.StartUtc >= start && bar.EndUtc <= asOf && bar.AvailableAtUtc <= asOf)
            .Distinct().OrderBy(bar => bar.EndUtc).ThenBy(bar => bar.Id, StringComparer.Ordinal).ToArray();
        if (bars.Any(bar => string.IsNullOrWhiteSpace(bar.Id) || bar.EndUtc - bar.StartUtc != strategy.BarInterval
            || bar.AvailableAtUtc < bar.EndUtc || bar.Volume < 0m || bar.Low <= 0m
            || bar.High < bar.Low || bar.Open < bar.Low || bar.Open > bar.High
            || bar.Close < bar.Low || bar.Close > bar.High)
            || bars.GroupBy(bar => bar.EndUtc).Any(group => group.Count() > 1)
            || bars.GroupBy(bar => bar.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            gates.Missing("INVALID_OR_CONFLICTING_BARS", "Available bars have invalid fields or conflicting identities/periods.");
            return [];
        }
        if (bars.Length != 0 && bars.Select((bar, i) => bar.StartUtc == start.AddTicks(strategy.BarInterval.Ticks * i)).Any(valid => !valid))
            gates.Missing("INCOMPLETE_SESSION_BARS", "Current-session bar coverage contains a gap; no synthetic/forward-filled bars are admitted.");
        return bars;
    }

    public static FuturesFeatureEvidence? Feature(IReadOnlyList<FuturesFeatureEvidence> source,
        CompletedBarEvidence bar, IReadOnlyList<CompletedBarEvidence> bars,
        string configurationVersion, DateTimeOffset asOf, GateBook gates)
    {
        var matches = source.Where(feature => feature.BarId == bar.Id && feature.SessionId == bar.SessionId
            && feature.AvailableAtUtc <= asOf).ToArray();
        if (matches.Length != 1)
        {
            gates.Missing("FUTURES_FEATURE_UNAVAILABLE", "Require one available, canonical feature per completed bar.");
            return null;
        }
        var feature = matches[0];
        var prefix = bars.Where(input => input.EndUtc <= bar.EndUtc).ToArray();
        if (!feature.CompleteSessionCoverage || feature.ConfigurationVersion != configurationVersion
            || string.IsNullOrWhiteSpace(feature.CalculatorVersion) || string.IsNullOrWhiteSpace(feature.Id)
            || feature.InputBarIds is null || feature.InputBarIds.Count != prefix.Length
            || feature.InputBarIds.Distinct(StringComparer.Ordinal).Count() != prefix.Length
            || !feature.InputBarIds.Order(StringComparer.Ordinal).SequenceEqual(prefix.Select(input => input.Id).Order(StringComparer.Ordinal))
            || prefix.Any(input => input.AvailableAtUtc > feature.AvailableAtUtc)
            || prefix.Select((input, i) => input.StartUtc == prefix[0].StartUtc.AddTicks((bar.EndUtc - bar.StartUtc).Ticks * i)).Any(valid => !valid)
            || feature.Vwap is null or <= 0m)
        {
            gates.Missing("FUTURES_FEATURE_LINEAGE", "VWAP must bind the complete available session prefix, configuration and calculator provenance.");
            return null;
        }
        return feature;
    }

    public static ConstituentParticipation? Confirmation(ConstituentDefinitionEvidence? definitions,
        IReadOnlyList<ConstituentBarEvidence> observations, CompletedBarEvidence bar,
        DateTimeOffset asOf, IndexStrategyConfiguration strategy, GateBook gates)
        => Confirmation(definitions, observations, bar, asOf, strategy, gates, out _);

    public static ConstituentParticipation? Confirmation(ConstituentDefinitionEvidence? definitions,
        IReadOnlyList<ConstituentBarEvidence> observations, CompletedBarEvidence bar,
        DateTimeOffset asOf, IndexStrategyConfiguration strategy, GateBook gates, out IReadOnlyList<ConstituentRow> evidenceRows)
    {
        evidenceRows = [];
        if (definitions is null || !definitions.Verified || definitions.AvailableAtUtc > asOf
            || definitions.EffectiveFromUtc > bar.EndUtc
            || (definitions.EffectiveToUtc is { } end && end <= bar.EndUtc)
            || string.IsNullOrWhiteSpace(definitions.Version) || definitions.Members is null
            || definitions.Members.Count == 0
            || definitions.Members.Any(member => string.IsNullOrWhiteSpace(member.InstrumentId)
                || member.WeightPercent is null or <= 0m or > 100m)
            || definitions.Members.Select(member => member.InstrumentId).Distinct(StringComparer.Ordinal).Count() != definitions.Members.Count)
        {
            gates.Missing("CONSTITUENT_DEFINITIONS", "Verified point-in-time membership and every required weight are mandatory.");
            return null;
        }
        var rows = new List<ConstituentRow>();
        var missing = new List<string>();
        foreach (var member in definitions.Members.OrderBy(member => member.InstrumentId, StringComparer.Ordinal))
        {
            var matches = observations.Where(observation => observation.InstrumentId == member.InstrumentId
                && observation.SessionId == bar.SessionId && observation.StartUtc == bar.StartUtc
                && observation.EndUtc == bar.EndUtc && observation.AvailableAtUtc <= asOf).Distinct().ToArray();
            var observation = matches.Length == 1 ? matches[0] : null;
            var usable = observation is { Close: > 0m, Vwap: > 0m, CompleteSessionCoverage: true }
                && observation.AvailableAtUtc >= observation.EndUtc
                && !string.IsNullOrWhiteSpace(observation.CalculatorVersion);
            if (!usable) missing.Add(member.InstrumentId);
            rows.Add(new(member.InstrumentId, member.InstrumentId, member.WeightPercent,
                usable ? observation!.Close : null, usable ? observation!.Vwap : null,
                null, null, usable ? "fresh" : "missing", null, null));
        }
        var participation = DashboardAnalytics.Participation(rows);
        evidenceRows = rows.AsReadOnly();
        if (missing.Count != 0)
        {
            gates.Add("MISSING_CONSTITUENT_EVIDENCE", GateOutcome.UNKNOWN,
                $"Missing matching completed close/VWAP: {string.Join(", ", missing)}. No renormalization.",
                participation.MissingWeightPercent, 0m);
            return null;
        }
        if (participation.MissingWeightPercent < -strategy.WeightTotalTolerancePercent
            || participation.MissingWeightPercent > strategy.WeightTotalTolerancePercent)
        {
            gates.Missing("INVALID_WEIGHT_TOTAL", "Verified weights must total 100% within the configured publication tolerance.");
            return null;
        }
        gates.Pass("COMPLETE_CONSTITUENT_EVIDENCE", $"Matched every required constituent; definitions {definitions.Version}.");
        return participation;
    }

    public static void DirectionalConfirmation(ConstituentParticipation participation, TradeDirection direction,
        IndexStrategyConfiguration strategy, GateBook gates)
    {
        var weight = direction == TradeDirection.Bullish ? participation.AboveVwapWeightPercent : participation.BelowVwapWeightPercent;
        gates.Require(weight >= strategy.ConfirmationWeightPercent, "CONSTITUENT_CONFIRMATION",
            "Use absolute verified weight, never an observed-weight renormalization.", weight, strategy.ConfirmationWeightPercent);
    }

    public static bool Fresh(DateTimeOffset? observed, DateTimeOffset? received,
        DateTimeOffset available, DateTimeOffset asOf, EvidenceConfiguration configuration)
        => observed is { } observation && received is { } receipt && available <= asOf
        && observation <= receipt && receipt <= asOf && receipt <= available
        && asOf - observation <= configuration.MaximumObservationAge
        && asOf - receipt <= configuration.MaximumReceiptAge;

    public static bool FreshQuote(QuoteEvidence? quote, string instrument, DateTimeOffset asOf,
        EvidenceConfiguration configuration) => quote is not null && quote.InstrumentId == instrument
        && !string.IsNullOrWhiteSpace(quote.Id) && !string.IsNullOrWhiteSpace(quote.SourceVersion)
        && quote.Sequence >= 0 && quote.EventTimeUtc is { } eventTime && eventTime <= asOf
        && eventTime <= quote.ReceivedAtUtc
        && asOf - eventTime <= configuration.MaximumObservationAge
        && Fresh(quote.BidAskObservedAtUtc, quote.ReceivedAtUtc, quote.AvailableAtUtc, asOf, configuration);

    public static bool FreshUnderlying(UnderlyingPriceEvidence? quote, string instrument,
        DateTimeOffset asOf, EvidenceConfiguration configuration) => quote is { Price: > 0m }
        && quote.InstrumentId == instrument && !string.IsNullOrWhiteSpace(quote.Id)
        && !string.IsNullOrWhiteSpace(quote.SourceVersion)
        && Fresh(quote.LastTradeTimeUtc, quote.ReceivedAtUtc, quote.AvailableAtUtc, asOf, configuration);

    public static void Portfolio(GateBook gates, PortfolioEvidence portfolio, EngineSession session,
        DateTimeOffset asOf, PortfolioConfiguration configuration, EntryOrigin origin)
    {
        if (!PortfolioUsable(portfolio, session, asOf))
        {
            gates.Missing("PORTFOLIO_SNAPSHOT", "Require a canonical, available portfolio snapshot for this session.");
            return;
        }
        gates.Require(portfolio.RecoveryReady, "RECOVERY_READY", "Host must recover paper state before entry.");
        gates.Require(portfolio.RecordingComplete, "RECORDER_COMPLETE", "Recorder pressure/gaps inhibit entry.");
        if (origin == EntryOrigin.STRATEGY) gates.Require(!portfolio.AutoPaused, "PAPER_AUTO_ENABLED", "Strategy entry requires enabled paper-auto.");
        gates.Require(!portfolio.PauseLatched, "SESSION_PAUSE_LATCH", "A session pause cannot be cleared by favorable recovery.");
        if (portfolio.NetLiquidationSessionPnl is { } pnl)
            gates.Require(pnl > -configuration.NetSessionPauseLoss, "SESSION_NET_LOSS", "Aggregate session liquidation includes fees once.", pnl, -configuration.NetSessionPauseLoss);
        else gates.Missing("SESSION_NET_PNL", "Unknown open-position liquidation cannot be treated as zero session loss.");
        if (portfolio.AvailableCash is null) gates.Missing("AVAILABLE_CASH", "Host supplies cash net of reservations.");
        else gates.Require(portfolio.AvailableCash >= 0m, "AVAILABLE_CASH", "Available cash cannot be negative.");
    }

    public static bool PortfolioUsable(PortfolioEvidence portfolio, EngineSession session, DateTimeOffset asOf) =>
        portfolio.SessionId == session.Id && portfolio.AvailableAtUtc <= asOf
        && !string.IsNullOrWhiteSpace(portfolio.Version)
        && portfolio.ActiveOrPendingEntries.All(slot => !string.IsNullOrWhiteSpace(slot.Id) && !string.IsNullOrWhiteSpace(slot.IndexId))
        && portfolio.ActiveOrPendingEntries.Select(slot => slot.Id).Distinct(StringComparer.Ordinal).Count() == portfolio.ActiveOrPendingEntries.Count
        && portfolio.AcceptedAttempts.All(attempt => attempt.AcceptedAtUtc <= asOf && attempt.SessionId == session.Id
            && !string.IsNullOrWhiteSpace(attempt.IntentId) && !string.IsNullOrWhiteSpace(attempt.SetupId)
            && !string.IsNullOrWhiteSpace(attempt.IndexId) && Enum.IsDefined(attempt.Origin))
        && portfolio.AcceptedAttempts.GroupBy(attempt => attempt.IntentId, StringComparer.Ordinal)
            .All(group => group.Distinct().Count() == 1);

    public static EntryAction Action(GateBook gates) => gates.Failed ? EntryAction.SKIP
        : gates.Unknown ? EntryAction.INSUFFICIENT_DATA : EntryAction.WATCH;

    public static IReadOnlyList<string> Reasons(IEnumerable<GateResult> gates) => Array.AsReadOnly(gates
        .Where(gate => gate.Outcome is GateOutcome.FAIL or GateOutcome.UNKNOWN)
        .OrderBy(gate => gate.Outcome == GateOutcome.FAIL ? 0 : 1)
        .Select(gate => gate.Code).Distinct(StringComparer.Ordinal).ToArray());
}
