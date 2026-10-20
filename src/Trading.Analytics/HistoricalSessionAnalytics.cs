using Trading.Domain;

namespace Trading.Analytics;

public sealed record SessionFeature(HistoricalCandle Candle, decimal? Vwap, decimal? PreviousMeanTrueRange,
    bool CompleteCoverage, IReadOnlyList<string> InputBarIds);

public static class HistoricalSessionAnalytics
{
    public const string Version = "session-candle-features-v1";

    public static IReadOnlyList<SessionFeature> Calculate(IEnumerable<HistoricalCandle> source,
        DateTimeOffset sessionStart, DateTimeOffset asOf, int lookback)
    {
        if (lookback <= 0) throw new ArgumentOutOfRangeException(nameof(lookback));
        var bars = source.Where(bar => bar.StartUtc >= sessionStart && bar.EndUtc <= asOf && bar.AvailableAtUtc <= asOf)
            .OrderBy(bar => bar.StartUtc).ToArray();
        var accumulator = new HistoricalSessionFeatureAccumulator(sessionStart, lookback);
        return Array.AsReadOnly(bars.Select(bar => accumulator.Add(bar, asOf)).ToArray());
    }
}

public sealed class HistoricalSessionFeatureAccumulator(DateTimeOffset sessionStart, int lookback)
{
    private readonly List<string> inputIds = [];
    private readonly Queue<decimal> ranges = new();
    private HistoricalCandle? previous;
    private decimal weighted, volume;
    private bool complete = true;
    public SessionFeature Add(HistoricalCandle bar, DateTimeOffset asOf)
    {
        if (lookback <= 0 || bar.AvailableAtUtc > asOf || bar.EndUtc > asOf)
            throw new ArgumentException("Only available completed candles and positive lookback are admitted.");
        if (previous is not null && (bar.InstrumentId != previous.InstrumentId || bar.StartUtc <= previous.StartUtc))
            throw new ArgumentException("Accumulator requires one instrument in strictly increasing canonical order.");
        complete &= bar.StartUtc == sessionStart.AddMinutes(inputIds.Count) && bar.EndUtc == bar.StartUtc.AddMinutes(1)
            && bar.AvailableAtUtc >= bar.EndUtc && bar.Volume >= 0m;
        decimal? mean = ranges.Count == lookback ? ranges.Average() : null;
        var range = previous is null ? bar.High - bar.Low : Math.Max(bar.High - bar.Low,
            Math.Max(Math.Abs(bar.High - previous.Close), Math.Abs(bar.Low - previous.Close)));
        ranges.Enqueue(range);
        if (ranges.Count > lookback) ranges.Dequeue();
        weighted += (bar.High + bar.Low + bar.Close) / 3m * bar.Volume;
        volume += bar.Volume;
        inputIds.Add(bar.Id);
        previous = bar;
        return new(bar, complete && volume > 0m ? weighted / volume : null, complete ? mean : null,
            complete, Array.AsReadOnly(inputIds.ToArray()));
    }
}
