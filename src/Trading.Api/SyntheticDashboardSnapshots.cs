using Trading.Analytics;
using Trading.Domain;
using Trading.Risk;

namespace Trading.Api;

public sealed class SyntheticDashboardSnapshots(SyntheticIndexCatalog catalog) : IDashboardSnapshots
{
    private static readonly DateTimeOffset SessionStart = new(2026, 10, 12, 3, 45, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf = SessionStart.AddMinutes(60).AddSeconds(5);

    public IReadOnlyList<ScenarioDescriptor> Scenarios { get; } = Array.AsReadOnly(new[]
    {
        new ScenarioDescriptor("normal", "Normal", "Usable synthetic observations; no position."),
        new ScenarioDescriptor("missing-inputs", "Missing inputs", "Incomplete synthetic constituent coverage and option observations."),
        new ScenarioDescriptor("stale-quotes", "Stale quotes", "Old synthetic observations; preset position is unpriced."),
        new ScenarioDescriptor("feed-loss", "Feed loss", "Disconnected synthetic observations; preset position is unpriced."),
        new ScenarioDescriptor("open-position", "Open position", "Preset synthetic position with indicative gross liquidation only.")
    });

    public DashboardSnapshot? Get(string scenarioId, string indexId = "BANKNIFTY")
    {
        var descriptor = Scenarios.FirstOrDefault(scenario => scenario.Id == scenarioId);
        var seed = catalog.Fixture(indexId);
        if (descriptor is null || seed is null) return null;
        var capabilities = seed.Definition.Capabilities;
        var quoteState = scenarioId switch
        {
            "stale-quotes" => "stale",
            "feed-loss" => "disconnected",
            _ => "fresh"
        };
        var observationAge = quoteState switch { "stale" => 300, "disconnected" => 600, _ => 2 };
        var bars = Enumerable.Range(0, capabilities.FuturesChart ? 60 : 0).Select(i =>
        {
            var start = SessionStart.AddMinutes(i);
            var open = seed.FuturesBasePrice + i * 3m + (i % 6 - 3) * 4m;
            var close = open + (i % 2 == 0 ? 4m : -4m);
            return new FixtureBar(start, start.AddMinutes(1), start.AddMinutes(1).AddSeconds(1),
                open, Math.Max(open, close) + 8m, Math.Min(open, close) - 8m, close, 100m + i * 10m);
        }).ToArray();
        var options = Enumerable.Range(0, capabilities.OptionComparison ? 24 : 0).Select(i =>
        {
            var strike = seed.FirstStrike + i / 2 * seed.StrikeStep;
            var type = i % 2 == 0 ? "CE" : "PE";
            var strikeText = strike.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var contract = new FixtureContract($"{seed.ContractPrefix}-{seed.Expiry}-{strikeText}-{type}",
                $"SYNTH-{indexId}-{seed.Expiry}-{strikeText}-{type}", "SYNTHETIC", indexId, type,
                seed.Expiry, strike, seed.LotSize, seed.TickSize, seed.ValidFromUtc, seed.ValidToUtc, true);
            var bid = seed.OptionBaseBid + i * 7.5m;
            var quote = new Quote(bid + 0.5m, bid, bid + 1m, seed.LotSize * 4, seed.LotSize * 5,
                AsOf.AddSeconds(-observationAge), AsOf.AddSeconds(-observationAge + 1),
                AsOf.AddSeconds(-observationAge - 1), quoteState);
            if (scenarioId == "missing-inputs" && i < 2)
            {
                quote = i == 0
                    ? new(null, null, null, null, null, null, null, null, "missing")
                    : quote with { Bid = null, BidQuantity = null, State = "missing" };
            }
            var decision = quote.State switch
            {
                "missing" => new IllustrativeDecision("INSUFFICIENT_DATA", ["SYNTHETIC_ILLUSTRATION", "MISSING_QUOTE_INPUTS"]),
                "stale" => new IllustrativeDecision("SKIP", ["SYNTHETIC_ILLUSTRATION", "STALE_QUOTE"]),
                "disconnected" => new IllustrativeDecision("SKIP", ["SYNTHETIC_ILLUSTRATION", "FEED_DISCONNECTED"]),
                _ => new IllustrativeDecision("WATCH", ["SYNTHETIC_ILLUSTRATION", "NO_SELECTION_POLICY"])
            };
            return new OptionRow(contract, quote, DashboardAnalytics.Spread(quote),
                DashboardAnalytics.AgeSeconds(AsOf, quote.EventTimeUtc), decision);
        }).ToArray();
        var banks = seed.Constituents.Select(x => new ConstituentRow(x.Id, x.Name,
            capabilities.WeightedConstituents ? x.WeightPercent : null,
            x.LastPrice, x.Vwap, x.ChangePercent, x.LastPrice > x.Vwap, "fresh",
            AsOf.AddSeconds(-2), AsOf.AddSeconds(-1))).ToArray();
        if (scenarioId == "missing-inputs")
        {
            banks[2] = banks[2] with { LastPrice = null, Vwap = null, ChangePercent = null,
                AboveVwap = null, State = "missing", EventTimeUtc = null, ReceivedAtUtc = null };
            banks[3] = banks[3] with { WeightPercent = null };
        }
        else if (quoteState != "fresh")
        {
            banks = banks.Select(bank => bank with { State = quoteState, AboveVwap = null,
                EventTimeUtc = AsOf.AddSeconds(-observationAge), ReceivedAtUtc = AsOf.AddSeconds(-observationAge + 1) }).ToArray();
        }
        var position = new PresetPosition("none", null, null, null, null, null, null, null, null,
            capabilities.OptionComparison
                ? "No preset position. Journal plans are not orders or fills."
                : "Option comparison unavailable for this configured index; no contracts or preset position are supplied. Journal plans are not orders or fills.");
        if (capabilities.OptionComparison && scenarioId is ("open-position" or "stale-quotes" or "feed-loss"))
        {
            var currentBid = quoteState == "fresh" ? options[0].Quote.Bid : null;
            var units = IndicativePlanning.WholeLotUnits(1, options[0].Contract.LotSize);
            var entryPrice = seed.OptionBaseBid - 10m;
            position = new(currentBid.HasValue ? "open" : "unpriced", $"synth-{indexId}-position-1", options[0].Contract.Id,
                1, units, entryPrice, currentBid, IndicativePlanning.LongGrossPnl(entryPrice, currentBid, units), null,
                currentBid.HasValue
                    ? "Preset synthetic position; gross indicative bid-side liquidation only. Fees/net P&L unavailable."
                    : "Preset synthetic position is unpriced: stale/disconnected quotes cannot mark current liquidation or execute exits.");
        }
        var chart = capabilities.FuturesChart
            ? DashboardAnalytics.CompletedFuturesChart(seed.FuturesSymbol, bars, AsOf, SessionStart, SessionStart.AddMinutes(15))
            : new ChartContext(seed.FuturesSymbol, [], null, null, [], "Futures chart unavailable for this configured index; completed bars, opening range and VWAP are not supplied.");
        var participation = capabilities.WeightedConstituents
            ? DashboardAnalytics.Participation(banks)
            : new ConstituentParticipation(null, null, 100m,
                "Weighted participation unavailable for this configured index; constituent observations are illustrative and unweighted.");
        var version = $"synthetic-dashboard-v2:{indexId}:{seed.Definition.DefinitionVersion}";
        return new("2", version, $"{version}:{scenarioId}",
            scenarioId, descriptor.Label, 1, AsOf, "SYNTHETIC_FIXTURE", "SYNTHETIC TEST",
            new(scenarioId == "missing-inputs" ? "partial" : quoteState, observationAge, observationAge - 1,
                "Fixed synthetic as-of; quote ages are not evaluated against wall clock. API connectivity is not a live feed. "
                    + (capabilities.FuturesChart ? "Retained candles are fixture history." : "Futures chart unavailable for this configured index.")),
            new($"{seed.Definition.DisplayName} (synthetic cash context)", seed.IndexValue, seed.FuturesSymbol,
                capabilities.FuturesChart ? bars[^1].Close : null, 0.27m),
            chart,
            indexId == "BANKNIFTY" ? banks : [], participation, options,
            position,
            new(null, null, null, "Unavailable: synthetic fixture has no effective-date fee, cash or net-risk model. Paper execution is disabled."),
            indexId, banks);
    }
}
