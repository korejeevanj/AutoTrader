namespace Trading.Domain;

public sealed record MarketDepthLevel(decimal Price, int Quantity, int Orders);
public sealed record MarketTick(long InstrumentToken, decimal LastPrice, long? DayVolume,
    decimal? AveragePrice, DateTimeOffset? LastTradeTimeUtc, DateTimeOffset? ExchangeTimeUtc,
    DateTimeOffset ReceivedAtUtc, long Sequence, int PacketIndex,
    IReadOnlyList<MarketDepthLevel> Bids, IReadOnlyList<MarketDepthLevel> Asks, DateTimeOffset? AvailableAtUtc = null,
    decimal? DayHigh = null, decimal? DayLow = null,
    // Fields the exchange packet carries that the engine does not use yet; recorded and decoded for research (order-book pressure, open interest).
    long? LastTradedQuantity = null, long? TotalBuyQuantity = null, long? TotalSellQuantity = null, decimal? DayOpen = null, decimal? PreviousClose = null,
    long? OpenInterest = null, long? OpenInterestDayHigh = null, long? OpenInterestDayLow = null);

// One ingress sequence covers messages and host controls. PacketIndex preserves wire order.
// Payload is the original complete message; normalized ticks are reproducible from it.
public sealed record RecordedMarketEvent(string RunId, long Sequence, DateTimeOffset ReceivedAtUtc,
    string ConnectionId, string Kind, byte[] Payload, MarketAdmission? Admission = null);
public sealed record MarketAdmission(DateTimeOffset AvailableAtUtc, bool RecordingHealthy, long MarketCommittedThrough,
    bool PaperHealthy, long PaperCommittedThrough);
public sealed record RecordedQuoteRun(string Id, DateTimeOffset CreatedAtUtc, string ManifestJson,
    bool Closed, long LastSequence, string? CheckpointJson);
