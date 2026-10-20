using Trading.Domain;

namespace Trading.Data;

public sealed record HistoricalCoverageGap(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
public sealed record HistoricalCoverageResult(HistoricalCoverage Coverage, IReadOnlyList<HistoricalCoverageGap> MissingIntervals);

public static class HistoricalCoverageAnalysis
{
    public static HistoricalCoverageResult ForSession(HistoricalDatasetManifest manifest, HistoricalInstrument instrument,
        DateOnly date, IReadOnlyList<HistoricalCandle> observations)
    {
        var timezone = manifest.Indices.Single(index => index.Id == instrument.IndexId).TimeZone;
        var midnight = HistoricalAcquisitionStore.Midnight(date, timezone);
        var daily = observations.Where(bar => bar.InstrumentId == instrument.Id && bar.StartUtc >= midnight && bar.StartUtc < midnight.AddDays(1)).ToArray();
        var present = daily.Select(bar => bar.StartUtc).ToHashSet();
        var close = instrument.ContinuousTradingClose is { } continuous && continuous < manifest.MarketClose ? continuous : manifest.MarketClose;
        var gaps = new List<HistoricalCoverageGap>();
        var expected = 0; var missing = 0;
        for (var start = midnight.Add(manifest.MarketOpen.ToTimeSpan()); start < midnight.Add(close.ToTimeSpan()); start = start.AddMinutes(1))
        {
            if (instrument.Verified && (start < instrument.ValidFromUtc || start >= instrument.ValidToUtc)) continue;
            expected++;
            if (present.Contains(start)) continue;
            missing++;
            if (gaps.Count > 0 && gaps[^1].EndUtc == start) gaps[^1] = gaps[^1] with { EndUtc = start.AddMinutes(1) };
            else gaps.Add(new(start, start.AddMinutes(1)));
        }
        return new(new(instrument.Id, instrument.Role.ToString(), expected, daily.Length, missing,
            daily.Count(bar => bar.Volume == 0m), daily.Length == 0 ? "Unavailable" : missing > 0 ? "Sparse" : "Complete"), gaps.AsReadOnly());
    }
}
