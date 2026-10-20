namespace Trading.Domain;

public enum HistoricalInstrumentRole { CashIndex, Futures, Constituent, Call, Put }
public sealed record HistoricalInstrument
{
    public string Id { get; init; } = "";
    public string IndexId { get; init; } = "";
    public long InstrumentToken { get; init; }
    public HistoricalInstrumentRole Role { get; init; }
    public string File { get; init; } = "";
    public string Exchange { get; init; } = "";
    public string DefinitionVersion { get; init; } = "";
    public DateTimeOffset AvailableAtUtc { get; init; }
    public DateTimeOffset ValidFromUtc { get; init; }
    public DateTimeOffset ValidToUtc { get; init; }
    public DateOnly? Expiry { get; init; }
    public decimal? Strike { get; init; }
    public int? LotSize { get; init; }
    public decimal? TickSize { get; init; }
    public bool Verified { get; init; }
    public bool? ProviderMappingVerified { get; init; }
    public string? MappingMethod { get; init; }
    public TimeOnly? ContinuousTradingClose { get; init; }
}

public sealed record HistoricalWeight(string InstrumentId, decimal? WeightPercent);
public enum HistoricalAvailabilityMethod { HttpLastModified, OfficialPublication, ContemporaneousCapture }
public sealed record HistoricalPublicationEvidence(HistoricalAvailabilityMethod Method, string Source,
    DateTimeOffset EstablishedAtUtc, string Sha256);
public sealed record HistoricalWeightSet(string IndexId, string Version, string Source,
    DateTimeOffset EffectiveFromUtc, DateTimeOffset? EffectiveToUtc,
    DateTimeOffset AvailableAtUtc, bool Verified, IReadOnlyList<HistoricalWeight> Members,
    DateOnly? SnapshotDate = null, string WeightConvention = "Dated", HistoricalPublicationEvidence? PublicationEvidence = null);

public sealed record HistoricalDatasetManifest
{
    public string Version { get; init; } = "kite-minute-dataset-v1";
    public string DatasetId { get; init; } = "";
    public string Provider { get; init; } = "KITE";
    public bool IsSynthetic { get; init; }
    public DateTimeOffset? DownloadedAtUtc { get; init; }
    public string UniverseVersion { get; init; } = "universe-v1";
    public DateTimeOffset UniverseAvailableAtUtc { get; init; }
    public bool? IncludesAllOptions { get; init; }
    public bool CalendarVerified { get; init; }
    public string? CalendarSource { get; init; }
    public HistoricalPublicationEvidence? CalendarPublicationEvidence { get; init; }
    public TimeOnly MarketOpen { get; init; } = new(9, 15);
    public TimeOnly MarketClose { get; init; } = new(15, 30);
    public IReadOnlyList<DateOnly> TradingDates { get; init; } = [];
    public IReadOnlyList<IndexDefinition> Indices { get; init; } = [];
    public IReadOnlyList<HistoricalInstrument> Instruments { get; init; } = [];
    public IReadOnlyList<HistoricalWeightSet> Weights { get; init; } = [];
}

public sealed record HistoricalCandle(string InstrumentId, DateTimeOffset StartUtc,
    DateTimeOffset EndUtc, DateTimeOffset AvailableAtUtc, decimal Open, decimal High,
    decimal Low, decimal Close, decimal Volume, decimal? OpenInterest)
{
    public string Id => $"{InstrumentId}:{StartUtc:O}";
}
public sealed record HistoricalQualityIssue(string InstrumentId, string Code, string Detail,
    DateTimeOffset? StartUtc = null);
public sealed record HistoricalFileProvenance(string InstrumentId, string File, string Sha256,
    int InputRows, int AcceptedRows, int IdenticalDuplicates);
public sealed record HistoricalDataset(HistoricalDatasetManifest Manifest,
    IReadOnlyList<HistoricalCandle> Candles, IReadOnlyList<HistoricalQualityIssue> Issues,
    IReadOnlyList<HistoricalFileProvenance> Files, string ManifestSha256,
    IReadOnlyList<HistoricalCoverage>? Coverage = null);
public sealed record HistoricalCoverage(string InstrumentId, string Role, int ExpectedMinutes, int ReturnedMinutes,
    int MissingMinutes, int ZeroVolumeMinutes, string State);
