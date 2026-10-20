using Trading.Domain;

namespace Trading.Analytics;

public static class DashboardAnalytics
{
    public static decimal? Spread(Quote quote) =>
        quote.Bid is { } bid && quote.Ask is { } ask ? ask - bid : null;

    public static decimal? AgeSeconds(DateTimeOffset asOfUtc, DateTimeOffset? observedAtUtc) =>
        observedAtUtc is { } time ? (asOfUtc - time).Ticks / (decimal)TimeSpan.TicksPerSecond : null;

    public static ChartContext CompletedFuturesChart(
        string symbol, IEnumerable<FixtureBar> bars, DateTimeOffset asOfUtc,
        DateTimeOffset openingStartUtc, DateTimeOffset openingEndUtc)
    {
        var completed = bars.Where(bar => bar.EndUtc <= asOfUtc && bar.AvailableAtUtc <= asOfUtc)
            .OrderBy(bar => bar.StartUtc).ToArray();
        var opening = completed.Where(bar => bar.StartUtc >= openingStartUtc && bar.EndUtc <= openingEndUtc).ToArray();
        var completeRange = opening.Length == (openingEndUtc - openingStartUtc).TotalMinutes
            && opening.Select((bar, i) => bar.StartUtc == openingStartUtc.AddMinutes(i)
                && bar.EndUtc == bar.StartUtc.AddMinutes(1)).All(valid => valid);
        decimal weightedPrice = 0, volume = 0;
        var vwap = new List<VwapPoint>();
        foreach (var bar in completed)
        {
            weightedPrice += (bar.High + bar.Low + bar.Close) / 3m * bar.Volume;
            volume += bar.Volume;
            if (volume > 0)
            {
                // Plot at the candle's period start; inclusion still requires end/availability <= as-of.
                vwap.Add(new(bar.StartUtc, decimal.Round(weightedPrice / volume, 2)));
            }
        }
        return new(symbol, completed,
            completeRange ? opening.Max(bar => bar.High) : null,
            completeRange ? opening.Min(bar => bar.Low) : null, vwap,
            "Approximate futures VWAP (synthetic one-minute candle typical price × volume)");
    }

    public static ConstituentParticipation Participation(IReadOnlyList<ConstituentRow> constituents)
    {
        var usable = constituents.Where(bank => bank.State == "fresh" && bank.WeightPercent.HasValue
            && bank.LastPrice.HasValue && bank.Vwap.HasValue).ToArray();
        var observed = usable.Sum(bank => bank.WeightPercent!.Value);
        var above = usable.Where(bank => bank.LastPrice > bank.Vwap).Sum(bank => bank.WeightPercent!.Value);
        var below = usable.Where(bank => bank.LastPrice < bank.Vwap).Sum(bank => bank.WeightPercent!.Value);
        return new(usable.Length == 0 ? null : above, usable.Length == 0 ? null : below,
            100m - observed, "Illustrative synthetic weights; missing weight disclosed against 100%, never renormalized");
    }
}
