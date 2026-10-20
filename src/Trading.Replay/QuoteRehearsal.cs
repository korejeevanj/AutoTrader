using System.Buffers.Binary;
using System.Text.Json;
using Trading.Domain;
using Trading.Engine;
using Trading.Risk;

namespace Trading.Replay;

// Software-only received-packet fixture, never supplied to the live feed/cache.
// Fixed virtual session, fictional contracts/weights/fees; no provider or system clock.
public static class QuoteRehearsal
{
    public static readonly DateTimeOffset Open = DateTimeOffset.Parse("2026-10-21T03:45:00Z");
    public static QuoteRunManifest Manifest()
    {
        var definitions = new List<HistoricalInstrument>();
        HistoricalInstrument Make(string id, long token, HistoricalInstrumentRole role, decimal strike = 100) => new()
        { Id = id, InstrumentToken = token, Role = role, IndexId = "BANKNIFTY", Exchange = "SYNTHETIC", Expiry = new(2026, 10, 27),
            Strike = strike, LotSize = 30, TickSize = .01m, Verified = true, DefinitionVersion = "synthetic-quote-rehearsal-v1",
            AvailableAtUtc = Open.AddMinutes(-1), ValidFromUtc = Open.AddDays(-1), ValidToUtc = Open.AddDays(1) };
        definitions.Add(Make("future", 258, HistoricalInstrumentRole.Futures));
        definitions.Add(Make("bank", 513, HistoricalInstrumentRole.Constituent));
        for (var strike = 93; strike <= 107; strike++)
        {
            definitions.Add(Make("call-" + strike, strike * 512 + 2, HistoricalInstrumentRole.Call, strike));
            definitions.Add(Make("put-" + strike, strike * 512 + 258, HistoricalInstrumentRole.Put, strike));
        }
        var settings = new EngineConfiguration { PaperSimulation = new() { Version = "synthetic-quote-one-tick-v1", EntryAdverseSlippage = .01m, ExitAdverseSlippage = .01m } };
        return new("fixture", new(2026, 10, 21), Open.AddMinutes(-1),
            new("BANKNIFTY", "Bank Nifty software fixture", "SYNTHETIC", "Asia/Kolkata", "Fictional banks", "synthetic-quote-rehearsal-v1",
                Open.AddDays(-1), null, true, new(true, true, true)), definitions,
            new("BANKNIFTY", "fictional-weights", "SYNTHETIC TEST", Open.AddDays(-1), null, Open.AddDays(-1), true, [new("bank", 100)]),
            settings.ToJson(), new ResearchFeeSchedule { ProfileId = "paper-fees", Version = "fictional-one-rupee-fee", Source = "SYNTHETIC TEST worked example",
                IsIllustrative = true, AvailableAtUtc = Open.AddDays(-1), EffectiveFromUtc = Open.AddDays(-1), BrokeragePerOrder = 1 },
            "synthetic-quote-rehearsal-v1", ["SYNTHETIC_PACKETS_CONTRACTS_WEIGHTS_AND_FEES"]);
    }

    public static byte[] Packet(long token, DateTimeOffset time, decimal last, long volume, decimal bid = 1, decimal ask = 1.01m, int depth = 30)
    {
        var message = new byte[188]; BinaryPrimitives.WriteUInt16BigEndian(message, 1); BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 184);
        void Set(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(offset + 4), value);
        Set(0, (uint)token); Set(4, (uint)(last * 100)); Set(16, (uint)volume);
        Set(44, (uint)time.ToUnixTimeSeconds()); Set(60, (uint)time.ToUnixTimeSeconds());
        Set(64, (uint)depth); Set(68, (uint)(bid * 100)); Set(124, (uint)depth); Set(128, (uint)(ask * 100)); return message;
    }

    public static IReadOnlyList<RecordedMarketEvent> Lifecycle()
    {
        var events = new List<RecordedMarketEvent>();
        void Add(string kind, DateTimeOffset time, byte[] bytes) => events.Add(new("fixture", events.Count + 1, time, "synthetic", kind, bytes));
        void Quote(long token, DateTimeOffset time, decimal price, int volume, decimal bid = 1, decimal ask = 1.01m) => Add("Binary", time, Packet(token, time, price, volume, bid, ask));
        Add("Connected", Open, []);
        for (var second = 0; second <= 900; second++)
        {
            var time = Open.AddSeconds(second);
            Quote(258, time, second % 60 == 1 ? 80 : second % 60 == 2 ? 120 : 100, second + 1);
            Quote(513, time, 100, second + 1);
        }
        var now = Open.AddMinutes(15);
        Quote(51202, now, 1, 100);
        Add("Health", now, JsonSerializer.SerializeToUtf8Bytes(new QuoteRecorderHealth(true, events.Count), QuoteTradingSession.Json));
        Add("Control", now, JsonSerializer.SerializeToUtf8Bytes(new QuoteControl("manual", "manual-test", "call-100"), QuoteTradingSession.Json));
        Quote(258, now.AddMilliseconds(300), 100, 902);
        Quote(51202, now.AddMilliseconds(300), 1, 101);
        Add("Control", now.AddMilliseconds(400), JsonSerializer.SerializeToUtf8Bytes(new QuoteControl("exit", "exit-1"), QuoteTradingSession.Json));
        Quote(51202, now.AddMilliseconds(700), 2, 102, 2, 2.01m);
        return events;
    }
}
