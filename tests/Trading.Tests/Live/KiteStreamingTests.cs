using System.Buffers.Binary;
using Trading.Data;
using Xunit;

namespace Trading.Tests.Live;

public sealed class KiteStreamingTests
{
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
