namespace Trading.Replay;

// Host-only pacing. Historical event order and decision timestamps never depend on wall time.
public sealed class ReplayPacingClock(TimeProvider clock)
{
    private long anchor;
    private DateTimeOffset? origin;
    private DateTimeOffset? latest;
    public double Speed { get; private set; } = 60;
    public double LastProcessingMilliseconds { get; private set; }
    public void Reanchor(DateTimeOffset? time, double speed)
    {
        if (!double.IsFinite(speed) || speed <= 0 || speed > 100000) throw new ArgumentException("Invalid replay speed.");
        origin = latest = time; anchor = clock.GetTimestamp(); Speed = speed;
    }
    public TimeSpan DelayUntil(DateTimeOffset next)
    {
        if (origin is null) { origin = latest = next; anchor = clock.GetTimestamp(); }
        var remaining = (next - origin.Value).TotalSeconds / Speed - clock.GetElapsedTime(anchor).TotalSeconds;
        return TimeSpan.FromSeconds(Math.Max(0, remaining));
    }
    public void Observe(DateTimeOffset time, double processingMilliseconds)
    {
        if (origin is null) { origin = time; anchor = clock.GetTimestamp(); }
        latest = time; LastProcessingMilliseconds = processingMilliseconds;
    }
    public ReplayTiming Snapshot(bool paused, bool batch)
    {
        var elapsed = clock.GetElapsedTime(anchor).TotalSeconds;
        var simulated = origin is { } start && latest is { } end ? Math.Max(0, (end - start).TotalSeconds) : 0;
        return new(batch ? "Maximum" : "Paced", Speed,
            paused || elapsed <= 0 || simulated == 0 ? null : simulated / elapsed,
            paused || batch || simulated == 0 ? 0 : Math.Max(0, elapsed - simulated / Speed) * 1000,
            LastProcessingMilliseconds);
    }
}
public sealed record ReplayTiming(string Mode, double RequestedSpeed, double? AchievedSpeed, double LagMilliseconds, double ProcessingMilliseconds);
