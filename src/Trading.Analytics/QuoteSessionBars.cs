using Trading.Domain;

namespace Trading.Analytics;

public sealed record QuoteBarState(IReadOnlyList<HistoricalCandle> Completed, HistoricalCandle? Active,
    long? PreviousVolume, DateTimeOffset? LastEventUtc, bool CompleteCoverage);

// Candle-derived VWAP remains labelled approximate. Exchange cumulative volume is
// differenced, never summed per quote. Finalized bars are never retroactively revised.
// Silence between one instrument's own ticks is not a gap (a quiet stock simply does not trade);
// the host calls Gap() when the feed itself is silent, disconnects or loses data.
// A bar closes finalizationGrace after its minute ends, so a tick stamped inside the minute (exchange time has one-second
// resolution) that arrives just after the boundary still lands in it instead of being lost to a finalized bar.
public sealed class QuoteSessionBars(string instrumentId, DateTimeOffset sessionStart, TimeSpan finalizationGrace = default)
{
    public QuoteBarState State { get; private set; } = new([], null, null, null, true);
    public void Restore(QuoteBarState state) => State = state;
    public void Gap() => State = State with { CompleteCoverage = false };
    // Loss before the session starts cannot damage a session prefix that has not begun.
    public void GapAt(DateTimeOffset at) { if (at >= sessionStart) Gap(); }

    public void Add(MarketTick tick)
    {
        if (tick.ExchangeTimeUtc is not { } time || time < sessionStart || tick.LastPrice <= 0 || tick.DayVolume is null) return;
        if (State.LastEventUtc > time || State.Completed.LastOrDefault()?.EndUtc > time)
        { Gap(); return; }
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
        if (State.Active is { } active && now >= active.EndUtc + finalizationGrace) Complete(now);
    }
    private void Complete(DateTimeOffset available)
    {
        if (State.Active is not { } bar) return;
        State = State with { Active = null, Completed = [.. State.Completed, bar with { AvailableAtUtc = available < bar.EndUtc ? bar.EndUtc : available }] };
    }
}
