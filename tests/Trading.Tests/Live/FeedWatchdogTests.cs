using Trading.Data;
using Xunit;

namespace Trading.Tests.Live;

public sealed class FeedWatchdogTests
{
    private static readonly DateTimeOffset Open = new(2026, 10, 22, 3, 45, 0, TimeSpan.Zero), Close = Open.AddHours(6).AddMinutes(15);
    private static readonly TimeSpan Five = TimeSpan.FromSeconds(5);

    [Fact]
    public void Silence_past_the_threshold_inside_the_session_trips()
    {
        var last = Open.AddMinutes(5);
        Assert.False(FeedWatchdog.IsStalled(last.AddSeconds(5), last, Open, Close, Five));
        Assert.True(FeedWatchdog.IsStalled(last.AddSeconds(5.1), last, Open, Close, Five));
    }

    [Fact]
    public void Pre_open_quiet_never_trips_and_the_clock_starts_at_the_open()
    {
        var last = Open.AddMinutes(-30);
        Assert.False(FeedWatchdog.IsStalled(Open.AddSeconds(-1), last, Open, Close, Five));
        Assert.False(FeedWatchdog.IsStalled(Open.AddSeconds(4), last, Open, Close, Five));
        Assert.True(FeedWatchdog.IsStalled(Open.AddSeconds(6), last, Open, Close, Five));
    }

    [Fact]
    public void After_the_close_never_trips()
        => Assert.False(FeedWatchdog.IsStalled(Close.AddMinutes(1), Open, Open, Close, Five));
}
