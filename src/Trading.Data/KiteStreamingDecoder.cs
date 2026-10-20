using System.Buffers.Binary;
using Trading.Domain;

namespace Trading.Data;

public static class KiteStreamingDecoder
{
    public const string Version = "kite-full-binary-v1";

    public static IReadOnlyList<MarketTick> Decode(ReadOnlySpan<byte> message, DateTimeOffset receivedAt, long sequence)
    {
        if (message.Length == 1) return []; // transport heartbeat, never a quote
        if (message.Length < 2) throw new InvalidDataException("TRUNCATED_PACKET_COUNT");
        var count = BinaryPrimitives.ReadUInt16BigEndian(message);
        var offset = 2;
        var ticks = new List<MarketTick>(count);
        for (var i = 0; i < count; i++)
        {
            if (offset + 2 > message.Length) throw new InvalidDataException("TRUNCATED_PACKET_LENGTH");
            var length = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]); offset += 2;
            if (length is not (8 or 28 or 32 or 44 or 184) || offset + length > message.Length)
                throw new InvalidDataException("UNSUPPORTED_OR_TRUNCATED_PACKET");
            try { ticks.Add(Packet(message.Slice(offset, length), receivedAt, sequence, i)); }
            catch (OverflowException) { throw new InvalidDataException("UNREPRESENTABLE_DEPTH_QUANTITY"); }
            offset += length;
        }
        if (offset != message.Length) throw new InvalidDataException("TRAILING_PACKET_BYTES");
        return ticks.AsReadOnly();
    }

    private static uint Number(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
    private static DateTimeOffset? Time(uint seconds) => seconds == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds);
    private static MarketTick Packet(ReadOnlySpan<byte> packet, DateTimeOffset receivedAt, long sequence, int index)
    {
        var token = Number(packet, 0);
        // This adapter is intentionally limited to the agreed NSE cash/NFO universe.
        if ((token & 255) is not (1 or 2 or 9)) throw new InvalidDataException("UNSUPPORTED_EXCHANGE_SEGMENT");
        var bids = new List<MarketDepthLevel>(); var asks = new List<MarketDepthLevel>();
        if (packet.Length == 184)
            for (var i = 0; i < 10; i++)
            {
                var offset = 64 + i * 12;
                var level = new MarketDepthLevel(Number(packet, offset + 4) / 100m,
                    checked((int)Number(packet, offset)), BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]));
                (i < 5 ? bids : asks).Add(level);
            }
        return new(token, Number(packet, 4) / 100m,
            packet.Length is 44 or 184 ? Number(packet, 16) : null,
            packet.Length is 44 or 184 ? Number(packet, 12) / 100m : null,
            packet.Length == 184 ? Time(Number(packet, 44)) : null,
            packet.Length == 184 ? Time(Number(packet, 60)) : packet.Length == 32 ? Time(Number(packet, 28)) : null,
            receivedAt, sequence, index, bids.AsReadOnly(), asks.AsReadOnly());
    }
}
