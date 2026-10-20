using Trading.Domain;

namespace Trading.Analytics;

public sealed record QuoteBarState(IReadOnlyList<HistoricalCandle> Completed, HistoricalCandle? Active,
    long? PreviousVolume, DateTimeOffset? LastEventUtc, bool CompleteCoverage);

// Candle-derived VWAP remains labelled approximate. Exchange cumulative volume is
// differenced, never summed per quote. Finalized bars are never retroactively revised.
public sealed class QuoteSessionBars(string instrumentId, DateTimeOffset sessionStart)
{
    public QuoteBarState State { get; private set; } = new([], null, null, null, true);
    public void Restore(QuoteBarState state) => State = state;
    public void Gap() => State = State with { CompleteCoverage = false };

    public void Add(MarketTick tick)
    {
        if (tick.ExchangeTimeUtc is not { } time || time < sessionStart || tick.LastPrice <= 0 || tick.DayVolume is null) return;
        if (State.LastEventUtc > time || State.Completed.LastOrDefault()?.EndUtc > time)
        { Gap(); return; }
        if (State.LastEventUtc is null && time > sessionStart.AddSeconds(2)) Gap();
        if (State.LastEventUtc is { } previous && time - previous > TimeSpan.FromSeconds(2)) Gap();
        if (tick.DayVolume < State.PreviousVolume) { Gap(); return; }
        var start = new DateTimeOffset(time.UtcDateTime.AddTicks(-(time.UtcTicks % TimeSpan.TicksPerMinute)), TimeSpan.Zero);
        var available = tick.AvailableAtUtc ?? tick.ReceivedAtUtc;
        if (State.Active is { } active && start > active.StartUtc) Complete(available);
        var delta = tick.DayVolume.Value - (State.PreviousVolume ?? 0);
        var current = State.Active;
        State = State with { Active = current is null
            ? new(instrumentId, start, start.AddMinutes(1), available, tick.LastPrice, tick.LastPrice, tick.LastPrice, tick.LastPrice, delta, null)
            : current with { High = Math.Max(current.High, tick.LastPrice), Low = Math.Min(current.Low, tick.LastPrice), Close = tick.LastPrice,
                Volume = current.Volume + delta, AvailableAtUtc = available }, PreviousVolume = tick.DayVolume, LastEventUtc = time };
    }

    public void Advance(DateTimeOffset now)
    {
        if (State.Active is { } active && now >= active.EndUtc) Complete(now);
    }
    private void Complete(DateTimeOffset available)
    {
        if (State.Active is not { } bar) return;
        if (State.LastEventUtc is null || State.LastEventUtc < bar.EndUtc.AddSeconds(-2)) Gap();
        State = State with { Active = null, Completed = [.. State.Completed, bar with { AvailableAtUtc = available < bar.EndUtc ? bar.EndUtc : available }] };
    }
}
