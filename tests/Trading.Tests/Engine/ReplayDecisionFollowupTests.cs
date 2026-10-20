using Trading.Replay;
using Trading.Tests.Replay;
using Xunit;

namespace Trading.Tests.Engine;

public sealed class ReplayDecisionFollowupTests
{
    [Fact]
    public void Net_cash_stop_is_checked_at_the_entry_fill_before_a_later_recovery_can_hide_it()
    {
        using var fixture = new ReplayFixture();
        var profile = fixture.Engine.Strategies[0];
        var configuration = fixture.Engine with { Strategies = [profile with { Risk = profile.Risk with { NetCashStop = 30m } }] };
        var data = fixture.Load();
        var recoveryTime = new DateTimeOffset(2026, 10, 9, 4, 2, 0, TimeSpan.Zero);
        data = data with { Candles = data.Candles.Select(bar => bar.InstrumentId == "call" && bar.StartUtc >= recoveryTime
            ? bar with { Open = 224m, High = 225m, Low = 223m, Close = 224m } : bar).ToArray() };

        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, configuration).RunToEnd().Sessions);
        var trade = Assert.Single(result.Trades);

        Assert.Equal("NET_CASH_STOP", trade.ExitReason);
        Assert.Equal(trade.EntryTimeUtc, trade.ExitKnownAtUtc);
        Assert.Equal(-34m, trade.NetPnl); // immediately executable crossing/slippage and ₹2 fees per side
        Assert.Contains(result.Decisions, row => row.Class == "CANDLE_POSITION_POLICY" && row.TimeUtc == trade.EntryTimeUtc
            && row.Action == "EXIT" && row.Reasons.Contains("NET_CASH_STOP"));
    }

    [Fact]
    public void Replay_schedules_soft_confirmation_expiry_between_candles_and_keeps_exit_pending_until_a_later_open()
    {
        using var fixture = new ReplayFixture();
        var profile = fixture.Engine.Strategies[0];
        var configuration = fixture.Engine with { Strategies = [profile with { Position = profile.Position with
            { MaximumSoftWeakeningDuration = TimeSpan.FromSeconds(90) } }] };
        var data = fixture.Load();
        var entryTime = new DateTimeOffset(2026, 10, 9, 4, 1, 0, TimeSpan.Zero); // 09:31 IST
        data = data with { Candles = data.Candles.Select(bar => bar.InstrumentId == "future" && bar.StartUtc >= entryTime
            ? bar with { Close = 55000m } : bar).ToArray() };

        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, configuration).RunToEnd().Sessions);
        var deadline = entryTime.AddSeconds(150); // first adverse close at 09:32, then 90 seconds
        Assert.Contains(result.Decisions, row => row.Class == "CANDLE_POSITION_POLICY" && row.Action == "EXIT"
            && row.TimeUtc == deadline && row.Reasons.Contains("SOFT_THESIS_TIMEOUT"));
        var trade = Assert.Single(result.Trades);
        Assert.Equal("SOFT_THESIS_TIMEOUT", trade.ExitReason);
        Assert.Equal(entryTime.AddMinutes(3), trade.ExitKnownAtUtc); // 09:34 opening, not a fictitious 09:33:30 fill
    }
}
