using System.Buffers.Binary;
using Trading.Data;
using Xunit;

namespace Trading.Tests.Live;

public sealed class KiteStreamingTests
{
    [Fact]
    public void Full_packet_also_carries_order_book_totals_open_interest_and_day_open_and_previous_close()
    {
        var bytes = new byte[188];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 184);
        void Set(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4 + offset), value);
        Set(0, 123394); Set(4, 10025); Set(8, 75); Set(12, 10010); Set(16, 500); Set(20, 4200); Set(24, 3900); Set(28, 9900); Set(32, 10100);
        Set(36, 9800); Set(40, 9950); Set(44, 1792556999); Set(48, 125000); Set(52, 130000); Set(56, 118000); Set(60, 1792557001);
        var tick = Assert.Single(KiteStreamingDecoder.Decode(bytes, DateTimeOffset.FromUnixTimeSeconds(1792557002), 1));
        Assert.Equal(75, tick.LastTradedQuantity); Assert.Equal(4200, tick.TotalBuyQuantity); Assert.Equal(3900, tick.TotalSellQuantity);
        Assert.Equal(99m, tick.DayOpen); Assert.Equal(101m, tick.DayHigh); Assert.Equal(98m, tick.DayLow); Assert.Equal(99.5m, tick.PreviousClose);
        Assert.Equal(125000, tick.OpenInterest); Assert.Equal(130000, tick.OpenInterestDayHigh); Assert.Equal(118000, tick.OpenInterestDayLow);
    }

    [Fact]
    public void Index_packet_decodes_price_day_range_open_previous_close_and_exchange_time_and_no_trade_fields()
    {
        var bytes = new byte[36];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 32);
        void Set(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4 + offset), value);
        Set(0, 264969); Set(4, 1423); Set(8, 1450); Set(12, 1400); Set(16, 1410); Set(20, 1405); Set(24, 18); Set(28, 1792557001);
        var tick = Assert.Single(KiteStreamingDecoder.Decode(bytes, DateTimeOffset.FromUnixTimeSeconds(1792557002), 2));
        Assert.Equal(14.23m, tick.LastPrice); Assert.Equal(14.5m, tick.DayHigh); Assert.Equal(14m, tick.DayLow);
        Assert.Equal(14.1m, tick.DayOpen); Assert.Equal(14.05m, tick.PreviousClose);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792557001), tick.ExchangeTimeUtc);
        Assert.Null(tick.OpenInterest); Assert.Null(tick.TotalBuyQuantity); Assert.Null(tick.DayVolume);
    }

    [Fact]
    public void Full_packet_preserves_separate_trade_exchange_receipt_times_and_whole_depth()
    {
        var received = DateTimeOffset.FromUnixTimeSeconds(1792557002);
        var bytes = new byte[188];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 184);
        void Set(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4 + offset), value);
        Set(0, 123394); Set(4, 10025); Set(16, 500); Set(44, 1792556999); Set(60, 1792557001);
        Set(64, 60); Set(68, 10000); Set(124, 90); Set(128, 10050);
        var tick = Assert.Single(KiteStreamingDecoder.Decode(bytes, received, 7));
        Assert.Equal(100.25m, tick.LastPrice);
        Assert.Equal(100m, tick.Bids[0].Price);
        Assert.Equal(90, tick.Asks[0].Quantity);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792556999), tick.LastTradeTimeUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792557001), tick.ExchangeTimeUtc);
        Assert.Equal(received, tick.ReceivedAtUtc);
        Assert.Equal(7, tick.Sequence);
    }
}
