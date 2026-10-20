using System.Text.Json;
using Trading.Domain;
using Trading.Portfolio;
using Trading.Risk;
using Xunit;

namespace Trading.Tests.Live;

public sealed class QuotePaperTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-21T04:30:00Z");
    private static readonly ResearchFeeSchedule Fees = new() { ProfileId = "paper-fees", Version = "test", Source = "independent test example",
        AvailableAtUtc = DateTimeOffset.UnixEpoch, EffectiveFromUtc = DateTimeOffset.UnixEpoch, BrokeragePerOrder = 1 };

    [Fact]
    public void New_quote_after_delay_fills_once_and_restart_preserves_pending_exit_and_cash_reconciliation()
    {
        var broker = new QuotePaperBroker(500000m);
        var contract = new HistoricalInstrument { Id = "NFO:CALL", InstrumentToken = 258, Role = HistoricalInstrumentRole.Call,
            IndexId = "BANKNIFTY", Exchange = "NFO", Expiry = new(2026, 10, 27), Strike = 52000m, LotSize = 30, TickSize = .05m,
            ValidFromUtc = Start.AddDays(-1), ValidToUtc = Start.AddDays(1), AvailableAtUtc = Start.AddDays(-1), Verified = true, DefinitionVersion = "test" };
        var approval = new QuoteEntryApproval("manual-1", "setup", "MANUAL_TEST", contract, Start, 1, 5000m, 400m, 200m,
            52000m, 51990m, 10m, "{}", Fees);
        broker.Accept(approval);
        broker.Accept(approval);
        MarketTick Tick(int ms, long seq, decimal bid = 100m, decimal ask = 100.5m, int depth = 30) => new(258, 100m, 1000, 100m,
            Start.AddMilliseconds(ms), Start.AddMilliseconds(ms), Start.AddMilliseconds(ms), seq, 0,
            [new(bid, depth, 1)], [new(ask, depth, 1)]);
        QuoteFillAssessment? Recheck(QuoteEntryApproval _, MarketTick tick) => new(400m, Fees);
        broker.OnQuote(Tick(249, 2), Start.AddMilliseconds(249), true, Recheck);
        Assert.Null(broker.State.Position);
        broker.OnQuote(Tick(250, 3), Start.AddMilliseconds(250), true, Recheck);
        Assert.Equal(100.55m, broker.State.Position!.EntryPrice);
        broker.RequestExit(Start.AddMilliseconds(300), 3, "MANUAL_EXIT");
        var recovered = new QuotePaperBroker(JsonSerializer.Deserialize<QuotePaperState>(JsonSerializer.Serialize(broker.State))!);
        recovered.OnQuote(Tick(600, 4, depth: 29), Start.AddMilliseconds(600), true, Recheck);
        Assert.NotNull(recovered.State.Position);
        recovered.OnQuote(Tick(700, 5, 102m, 102.5m), Start.AddMilliseconds(700), true, Recheck);
        Assert.Null(recovered.State.Position);
        var trade = Assert.Single(recovered.State.Trades);
        Assert.Equal(101.95m, trade.ExitPrice);
        Assert.Equal(42m, trade.GrossPnl);
        Assert.Equal(40m, trade.NetPnl);
        Assert.Equal(500040m, recovered.State.Ledger.Cash);
        Assert.Single(recovered.State.Attempts);
    }
}
