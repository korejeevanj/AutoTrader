using Trading.Domain;
using Trading.Engine;

namespace Trading.Tests.Engine;

internal static class EngineFixture
{
    public static readonly EngineSession Session = new("2026-10-12", new(2026, 10, 12),
        new(2026, 10, 11, 18, 30, 0, TimeSpan.Zero));
    public static readonly EvaluationIdentity Identity = new("fixture-run", "evidence-v1", "test-code");
    public static readonly DateTimeOffset Start = Session.At(new(9, 15));

    public static IndexEvaluationInput Entry(string indexId = "BANKNIFTY")
    {
        var bars = Enumerable.Range(0, 16).Select(i => Bar(i, i == 15 ? 105m : 100m)).ToArray();
        var asOf = bars[^1].AvailableAtUtc.AddSeconds(1);
        var contract = new OptionContractEvidence("option-a", indexId, "SYNTHETIC", OptionRight.Call,
            new(2026, 10, 29), 100m, 25, 0.05m, Start.AddDays(-1), Start.AddDays(17), Start.AddDays(-1), "contracts-v1", true);
        var quote = Quote(contract.Id, asOf);
        var risk = new EntryRiskEvidence("risk-a", contract.Id, contract.DefinitionVersion, quote.Id,
            bars[^1].Id, "paper-policy-v1", 25, asOf, asOf.AddMinutes(1), "paper-fees", "fees-v1",
            Start.AddDays(-1), null, "Trading.Risk/test-evidence", "paper-fill-v1", 0m, 0m, 200m,
            true, true, true, 200m, 40m, 5040m, 350m, 500m, 0.5m, 65m, 5m,
            Underlying(asOf, bars[^1].Close).Id, $"feature-{bars[^1].Id}");
        var definitions = new ConstituentDefinitionEvidence("weights-v1", Start.AddDays(-1), null,
            Start.AddDays(-1), true, [new("bank-a", 65m), new("bank-b", 35m)]);
        return new(Identity, asOf,
            new IndexDefinition(indexId, indexId, "SYNTHETIC", "Asia/Kolkata", "Constituents",
                "index-v1", Start.AddDays(-1), null, true, new(true, true, true)),
            Session, "futures", bars, Features(bars), definitions, Banks(bars[^1]),
            Underlying(asOf, bars[^1].Close), "universe-v1", Start,
            [new(contract, quote, risk)], Portfolio(asOf), FuturesContract:
            new("futures", indexId, Start.AddDays(-1), Start.AddDays(17), Start.AddDays(-1), "futures-v1"));
    }

    public static CompletedBarEvidence Bar(int minute, decimal close) => new($"bar-{minute}", "futures", Session.Id,
        Start.AddMinutes(minute), Start.AddMinutes(minute + 1), Start.AddMinutes(minute + 1).AddSeconds(1),
        100m, Math.Max(103m, close + 1m), Math.Min(97m, close - 1m), close, 100m);

    public static IReadOnlyList<FuturesFeatureEvidence> Features(IReadOnlyList<CompletedBarEvidence> bars,
        string version = "paper-policy-v1") => bars.Select((bar, i) => new FuturesFeatureEvidence(
            $"feature-{bar.Id}", bar.Id, Session.Id, version, bar.AvailableAtUtc,
            bars.Take(i + 1).Select(input => input.Id).ToArray(), 100m, 14, 0.1m,
            96.4m, bar.Close - 96.4m, 106.6m, 106.6m - bar.Close, "Trading.Analytics+Risk/test-evidence", true)).ToArray();

    public static IReadOnlyList<ConstituentBarEvidence> Banks(CompletedBarEvidence bar, bool adverse = false) =>
        [new("bank-a", Session.Id, bar.StartUtc, bar.EndUtc, bar.AvailableAtUtc, adverse ? 90m : 110m, 100m, true, "analytics-v1"),
         new("bank-b", Session.Id, bar.StartUtc, bar.EndUtc, bar.AvailableAtUtc, adverse ? 110m : 90m, 100m, true, "analytics-v1")];

    public static QuoteEvidence Quote(string instrument, DateTimeOffset asOf, decimal bid = 199m) =>
        new($"quote-{instrument}-{asOf:O}", instrument, bid, bid + 1m, 100, 100,
            asOf.AddMilliseconds(-500), asOf.AddMilliseconds(-100), asOf.AddMilliseconds(-500),
            asOf.AddMilliseconds(-100), "observed-fixture-v1", 1);

    public static UnderlyingPriceEvidence Underlying(DateTimeOffset asOf, decimal price = 105m) =>
        new($"underlying-{asOf:O}", "futures", price, asOf.AddMilliseconds(-500),
            asOf.AddMilliseconds(-100), asOf.AddMilliseconds(-100), "observed-fixture-v1");

    public static PortfolioEvidence Portfolio(DateTimeOffset asOf) => new("portfolio-v1", Session.Id,
        asOf, 500000m, 0m, false, false, true, true, [], []);

    public static IndexEvaluationInput Extend(IndexEvaluationInput input, params decimal[] closes)
    {
        var bars = input.FuturesBars.Concat(closes.Select((close, i) => Bar(input.FuturesBars.Count + i, close))).ToArray();
        var asOf = bars[^1].AvailableAtUtc.AddSeconds(1);
        var candidates = input.Candidates.Select(candidate =>
        {
            var quote = Quote(candidate.Contract.Id, asOf);
            return candidate with { Quote = quote, Risk = candidate.Risk! with
            { QuoteId = quote.Id, SignalBarId = bars[^1].Id, AvailableAtUtc = asOf, ValidUntilUtc = asOf.AddMinutes(1),
                UnderlyingQuoteId = Underlying(asOf, closes[^1]).Id, StructuralFeatureId = $"feature-{bars[^1].Id}" } };
        }).ToArray();
        return input with { AsOfUtc = asOf, FuturesBars = bars, FuturesFeatures = Features(bars),
            Constituents = Banks(bars[^1]), FuturesPrice = Underlying(asOf, closes[^1]), Candidates = candidates,
            Portfolio = input.Portfolio with { AvailableAtUtc = asOf } };
    }

    public static PositionEvaluationInput Position()
    {
        var entry = Entry();
        var option = entry.Candidates[0];
        var strategy = new IndexStrategyConfiguration { IndexId = entry.Index.Id };
        var position = new PaperPositionEvidence("position-1", entry.Index.Id, Session.Id, "entry-fill-v1",
            option.Contract, TradeDirection.Bullish, entry.AsOfUtc, 200m, 25, 20m,
            "paper-policy-v1", strategy, "futures", 96.4m, 8.6m, 105m);
        var quote = Quote(option.Contract.Id, entry.AsOfUtc, 200m);
        return new(Identity, entry.AsOfUtc, Session, position, quote,
            new("net-1", position.Id, position.EntryFillVersion, quote.Id, entry.AsOfUtc,
                entry.AsOfUtc.AddMinutes(1), "risk-v1", "fees-v1", 0m, 20m, 20m, -40m),
            entry.FuturesPrice, new("progress-1", position.Id, entry.FuturesPrice!.Id,
                entry.AsOfUtc, "risk-v1", 8.6m, 105m, 0m), entry.FuturesBars, entry.FuturesFeatures,
            entry.ConstituentDefinitions, entry.Constituents, entry.Portfolio);
    }
}
