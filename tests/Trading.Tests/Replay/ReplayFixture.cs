using System.Text.Json;
using Trading.Data;
using Trading.Domain;
using Trading.Engine;
using Trading.Replay;
using Trading.Risk;

namespace Trading.Tests.Replay;

internal sealed class ReplayFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "opencode", $"replay-{Guid.NewGuid():N}");
    public string ManifestPath => Path.Combine(Root, "dataset.json");
    public static readonly DateOnly Date = new(2026, 10, 9);
    public static readonly DateTimeOffset MetadataTime = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    public EngineConfiguration Engine { get; } = new()
    {
        Strategies = [new() { IndexId = "BANKNIFTY", EntryEnd = new(9, 34), SessionExit = new(9, 35) }]
    };
    public ReplayConfiguration Configuration { get; set; } = new()
    {
        Presets = [new()], FeeSchedules = [new() { ProfileId = "paper-fees", Version = "synthetic-fees-v1",
            Source = "Synthetic fee algebra fixture, not a broker schedule", IsIllustrative = true,
            AvailableAtUtc = MetadataTime, EffectiveFromUtc = MetadataTime, BrokeragePerOrder = 2m }]
    };
    public ReplayFixture(params DateOnly[] dates)
    {
        Directory.CreateDirectory(Root);
        if (dates.Length == 0) dates = [Date];
        var instruments = new List<HistoricalInstrument>();
        void Instrument(string id, HistoricalInstrumentRole role, long token, int? lot = null, decimal? tick = null, decimal? strike = null)
            => instruments.Add(new() { Id = id, IndexId = "BANKNIFTY", Role = role, InstrumentToken = token, File = id + ".json",
                Exchange = "SYNTHETIC", DefinitionVersion = "synthetic-contracts-v1", Verified = true,
                AvailableAtUtc = MetadataTime, ValidFromUtc = MetadataTime, ValidToUtc = MetadataTime.AddMonths(1),
                Expiry = role is HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put ? new(2026, 10, 29) : null,
                Strike = strike, LotSize = lot, TickSize = tick });
        Instrument("index", HistoricalInstrumentRole.CashIndex, 260105);
        Instrument("future", HistoricalInstrumentRole.Futures, 12466946);
        Instrument("call", HistoricalInstrumentRole.Call, 12656898, 25, 0.05m, 55000m);
        Instrument("put", HistoricalInstrumentRole.Put, 12657154, 25, 0.05m, 55000m);
        for (var i = 0; i < 14; i++) Instrument($"bank-{i:00}", HistoricalInstrumentRole.Constituent, 2000000 + i);
        var manifest = new HistoricalDatasetManifest
        {
            DatasetId = "synthetic-replay-fixture", IsSynthetic = true, TradingDates = dates,
            MarketClose = new(9, 36), UniverseAvailableAtUtc = MetadataTime, IncludesAllOptions = true,
            CalendarVerified = true, CalendarSource = "Synthetic session calendar fixture",
            CalendarPublicationEvidence = new(HistoricalAvailabilityMethod.ContemporaneousCapture, "Synthetic calendar availability", MetadataTime, new string('B', 64)),
            Indices = [new("BANKNIFTY", "Synthetic Bank Nifty", "SYNTHETIC", "Asia/Kolkata", "Banks",
                "synthetic-index-v1", MetadataTime, null, true, new(true, true, true))], Instruments = instruments,
            Weights = [new("BANKNIFTY", "synthetic-weights-v1", "Synthetic 14-member fixture", MetadataTime, null, MetadataTime,
                true, Enumerable.Range(0, 14).Select(i => new HistoricalWeight($"bank-{i:00}", i == 13 ? 9m : 7m)).ToArray(),
                PublicationEvidence: new(HistoricalAvailabilityMethod.ContemporaneousCapture, "Synthetic availability fixture", MetadataTime, new string('A', 64)))]
        };
        WriteManifest(manifest);
        foreach (var instrument in instruments)
        {
            var rows = new List<object[]>();
            foreach (var date in dates)
                for (var minute = 0; minute < 21; minute++)
                {
                    var timestamp = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5)).AddMinutes(minute);
                    decimal open, high, low, close, volume;
                    if (instrument.Role is HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put)
                    { open = close = 200m; high = 202m; low = 195m; volume = 100m; }
                    else if (instrument.Role == HistoricalInstrumentRole.Constituent)
                    {
                        var bank = int.Parse(instrument.Id.AsSpan(5), System.Globalization.CultureInfo.InvariantCulture);
                        close = minute < 15 ? 100m : bank < 10 ? 102m : 98m;
                        open = minute < 16 ? 100m : close; high = Math.Max(101m, close + 1m); low = Math.Min(99m, close - 1m); volume = 100m;
                    }
                    else
                    { open = minute < 16 ? 55000m : 55020m; close = minute < 15 ? 55000m : 55020m;
                      high = Math.Max(55010m, close + 1m); low = 54990m; volume = instrument.Role == HistoricalInstrumentRole.CashIndex ? 0m : 100m; }
                    rows.Add([timestamp.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture).Replace("+05:30", "+0530"),
                        open, high, low, close, volume, 0m]);
                }
            File.WriteAllText(Path.Combine(Root, instrument.File), JsonSerializer.Serialize(new { status = "success", data = new { candles = rows } }));
        }
    }
    public HistoricalDataset Load() => KiteCandleImporter.Load(ManifestPath, Configuration.PublicationLag);
    public HistoricalDatasetManifest ReadManifest() => JsonSerializer.Deserialize<HistoricalDatasetManifest>(File.ReadAllText(ManifestPath), KiteCandleImporter.JsonOptions)!;
    public void WriteManifest(HistoricalDatasetManifest manifest) => File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, KiteCandleImporter.JsonOptions));
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}
