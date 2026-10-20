using System.Text;
using System.Text.Json;
using Trading.Domain;

namespace Trading.Data;

public sealed record HistoricalCatalogDocument(string Source, bool IncludesAllOptions, HistoricalDatasetManifest Manifest);
public sealed record ExchangeSessionOverride(DateOnly Date, TimeOnly Open, TimeOnly Close);
public sealed record ExchangeCalendarDocument(int Year, string Source, bool Verified,
    IReadOnlyList<DateOnly> ClosedDates, IReadOnlyList<ExchangeSessionOverride> SpecialSessions,
    HistoricalPublicationEvidence? PublicationEvidence = null);
public sealed record HistoricalCacheEntry(string InstrumentId, DateOnly Date, string Sha256, DateTimeOffset DownloadedAtUtc,
    bool Complete, int Rows, string RawFile, bool RepairedCorruptSource = false);
public sealed record HistoricalAcquisitionPlan(HistoricalDatasetManifest Manifest, bool AllOptionsCovered,
    string Source, IReadOnlyList<string> Warnings);
public sealed class HistoricalStorageException(Exception inner) : IOException("Local historical storage could not publish a checkpoint.", inner);

public sealed partial class HistoricalAcquisitionStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string JobsRoot => Path.Combine(Root, "jobs");
    public string CatalogsRoot => Path.Combine(Root, "catalogs");

    public ExchangeCalendarDocument? Calendar(int year)
    {
        var calendar = Read<ExchangeCalendarDocument>(Path.Combine(Root, "calendars", $"{year}.json"));
        if (calendar is not null && (calendar.Year != year || calendar.ClosedDates is null || calendar.SpecialSessions is null
            || calendar.Verified && string.IsNullOrWhiteSpace(calendar.Source)
            || calendar.ClosedDates.Any(date => date.Year != year)
            || calendar.SpecialSessions.Any(session => session.Date.Year != year || session.Open >= session.Close)
            || calendar.SpecialSessions.Select(session => session.Date).Distinct().Count() != calendar.SpecialSessions.Count))
            throw new InvalidDataException("Invalid exchange calendar metadata.");
        return calendar;
    }

    public HistoricalAcquisitionPlan? Resolve(DateOnly date, TimeOnly open, TimeOnly close)
    {
        if (!Directory.Exists(CatalogsRoot)) return FromMaster(date, open, close);
        var documents = Directory.EnumerateFiles(CatalogsRoot, "*.json").Order(StringComparer.Ordinal)
            .Select(path => Read<HistoricalCatalogDocument>(path))
            .Where(document => document is not null && document.Manifest.TradingDates.Contains(date)
                && document.Manifest.Indices.Any(index => index.Id == "BANKNIFTY"))
            .Select(document => document!).ToArray();
        if (documents.Length > 1) throw new InvalidDataException("Multiple catalogues claim this date; select one active dated catalogue.");
        return documents.Length == 1 ? Select(documents[0], date, open, close) : FromMaster(date, open, close);
    }

    private HistoricalAcquisitionPlan? Select(HistoricalCatalogDocument document, DateOnly date, TimeOnly open, TimeOnly close)
    {
        var source = document.Manifest;
        var index = source.Indices.Single(index => index.Id == "BANKNIFTY");
        var midnight = Midnight(date, index.TimeZone);
        var start = midnight.Add(open.ToTimeSpan());
        var end = midnight.Add(close.ToTimeSpan());
        var futures = source.Instruments.Where(item => item.IndexId == index.Id && item.Role == HistoricalInstrumentRole.Futures
            && item.Expiry > date && item.ValidFromUtc <= start && start < item.ValidToUtc && item.AvailableAtUtc <= start)
            .OrderBy(item => item.Expiry).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var future = futures.FirstOrDefault();
        if (future is null) return null;
        // Preserve declared options, including unresolved identities. Unqualified
        // expiry/availability/validity assertions cannot silently shrink the universe.
        var selected = source.Instruments.Where(item => item.IndexId == index.Id
            && (item.Role != HistoricalInstrumentRole.Futures || item.Id == future.Id))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item with { File = SafeName(item.Id) + ".json" }).ToArray();
        var warnings = new List<string>();
        if (!document.IncludesAllOptions) warnings.Add("FULL_HISTORICAL_OPTION_CATALOGUE_UNAVAILABLE");
        if (selected.Any(item => !item.Verified)) warnings.Add("UNVERIFIED_CONTRACT_METADATA");
        if (!selected.Any(item => item.Role is HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put)) warnings.Add("NO_HISTORICAL_OPTIONS");
        var weights = source.Weights.Where(weights => weights.IndexId == index.Id && weights.EffectiveFromUtc < end
            && (weights.EffectiveToUtc is null || start < weights.EffectiveToUtc) && weights.AvailableAtUtc < end).ToArray();
        var openingWeights = weights.Where(weights => weights.EffectiveFromUtc <= start && weights.AvailableAtUtc <= start).ToArray();
        if (openingWeights.Length != 1 || !openingWeights[0].Verified || openingWeights[0].Members.Count == 0
            || string.IsNullOrWhiteSpace(openingWeights[0].Source) || openingWeights[0].Members.Any(member => member.WeightPercent is null)) warnings.Add("HISTORICAL_WEIGHTS_UNAVAILABLE");
        if (weights.Any(set => set.Members.Any(member => !selected.Any(item => item.Id == member.InstrumentId && item.Role == HistoricalInstrumentRole.Constituent))))
            warnings.Add("HISTORICAL_CONSTITUENT_DEFINITIONS_MISSING");
        return new(source with { DatasetId = $"BANKNIFTY-{date:yyyy-MM-dd}", TradingDates = [date], Indices = [index],
            MarketOpen = open, MarketClose = close, Instruments = selected, Weights = weights, IncludesAllOptions = document.IncludesAllOptions },
            document.IncludesAllOptions, document.Source, warnings.AsReadOnly());
    }

    private HistoricalAcquisitionPlan? FromMaster(DateOnly date, TimeOnly open, TimeOnly close)
    {
        var folder = Path.Combine(Root, "masters", date.ToString("yyyy-MM-dd"));
        if (!Directory.Exists(folder)) return null;
        var start = Midnight(date, "Asia/Kolkata").Add(open.ToTimeSpan());
        var end = Midnight(date, "Asia/Kolkata").Add(close.ToTimeSpan());
        var snapshot = Directory.EnumerateFiles(folder, "*.json").Select(path => Read<KiteMasterSnapshot>(path))
            .Where(item => item is not null && item.CapturedAtUtc <= start).OrderByDescending(item => item!.CapturedAtUtc).FirstOrDefault();
        if (snapshot is null) return null; // a dump captured today cannot assert an earlier day's catalogue
        var weights = Directory.Exists(Path.Combine(Root, "weights"))
            ? Directory.EnumerateFiles(Path.Combine(Root, "weights"), "*.json").Select(path => Read<HistoricalWeightSet>(path))
                .Where(item => item is not null && item.IndexId == "BANKNIFTY" && item.AvailableAtUtc < end
                    && item.EffectiveFromUtc < end && (item.EffectiveToUtc is null || start < item.EffectiveToUtc)).Select(item => item!).ToArray()
            : [];
        var rows = snapshot.Instruments.Where(row => row.Name == "BANKNIFTY" && row.Exchange == "NFO"
            && row.Expiry >= date && row.InstrumentType is "FUT" or "CE" or "PE").ToList();
        var indexRow = snapshot.Instruments.SingleOrDefault(row => row.InstrumentToken == 260105);
        if (indexRow is null) return null;
        rows.Add(indexRow);
        var definitions = new List<HistoricalInstrument>();
        HistoricalInstrument Definition(KiteMasterRow row, string id, HistoricalInstrumentRole role) => new()
        {
            Id = id, IndexId = "BANKNIFTY", InstrumentToken = row.InstrumentToken, Exchange = row.Exchange,
            Role = role, DefinitionVersion = snapshot.Sha256, File = SafeName(id) + ".json", AvailableAtUtc = snapshot.CapturedAtUtc,
            ValidFromUtc = snapshot.CapturedAtUtc, ValidToUtc = Midnight(date.AddDays(1), "Asia/Kolkata"),
            Expiry = row.Expiry, Strike = row.Strike, LotSize = row.LotSize, TickSize = row.TickSize, Verified = true
        };
        foreach (var row in rows)
            definitions.Add(Definition(row, row.Exchange + ":" + row.TradingSymbol, row == indexRow ? HistoricalInstrumentRole.CashIndex
                : row.InstrumentType == "FUT" ? HistoricalInstrumentRole.Futures : row.InstrumentType == "CE" ? HistoricalInstrumentRole.Call : HistoricalInstrumentRole.Put));
        if (weights.Length > 0)
            foreach (var member in weights.SelectMany(set => set.Members).DistinctBy(member => member.InstrumentId))
            {
                var symbol = member.InstrumentId.StartsWith("NSE:", StringComparison.Ordinal) ? member.InstrumentId[4..] : member.InstrumentId;
                var row = snapshot.Instruments.SingleOrDefault(row => row.Exchange == "NSE" && row.TradingSymbol == symbol);
                if (row is not null) definitions.Add(Definition(row, member.InstrumentId, HistoricalInstrumentRole.Constituent));
            }
        var manifest = new HistoricalDatasetManifest
        {
            DatasetId = $"BANKNIFTY-{date:yyyy-MM-dd}", TradingDates = [date], UniverseVersion = snapshot.Sha256,
            UniverseAvailableAtUtc = snapshot.CapturedAtUtc, Instruments = definitions, Weights = weights,
            Indices = [new("BANKNIFTY", "Bank Nifty", "NSE", "Asia/Kolkata", "Banks", snapshot.Sha256,
                snapshot.CapturedAtUtc, null, false, new(true, true, true))]
        };
        return Select(new("Kite master captured before the selected session: " + snapshot.Sha256, true, manifest), date, open, close);
    }

    public async Task ArchiveMasterAsync(byte[] raw, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var snapshot = KiteInstrumentMaster.Parse(raw, now);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")).DateTime);
        var folder = Path.Combine(Root, "masters", date.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(folder);
        var name = now.ToString("HHmmssfffffff") + "-" + snapshot.Sha256;
        await ImmutableBytes(Path.Combine(folder, name + ".csv"), raw, cancellationToken);
        await AtomicJsonAsync(Path.Combine(folder, name + ".json"), snapshot, cancellationToken);
    }
    public bool HasMaster(DateOnly date) => Directory.Exists(Path.Combine(Root, "masters", date.ToString("yyyy-MM-dd")))
        && Directory.EnumerateFiles(Path.Combine(Root, "masters", date.ToString("yyyy-MM-dd")), "*.json").Any();

    public async Task<HistoricalCacheEntry?> CachedAsync(HistoricalInstrument instrument, DateOnly date, TimeOnly open, TimeOnly close, CancellationToken cancellationToken)
    {
        if (instrument.ContinuousTradingClose is { } continuous && continuous < close) close = continuous;
        var folder = CacheFolder(instrument, date, open, close);
        var pointer = Path.Combine(folder, "latest.json");
        IEnumerable<string> candidates = File.Exists(pointer) ? new[] { pointer } : Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.meta.json").OrderDescending(StringComparer.Ordinal) : [];
        foreach (var path in candidates)
        {
            var entry = Read<HistoricalCacheEntry>(path);
            if (entry is null || !entry.Complete && instrument.Role is not (HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put)) continue;
            var raw = Path.Combine(folder, entry.Sha256 + ".json");
            if (!File.Exists(raw)) continue;
            var bytes = await File.ReadAllBytesAsync(raw, cancellationToken);
            if (KiteCandleImporter.Hash(bytes) == entry.Sha256) return entry with { RawFile = raw };
        }
        // Preserve typed DateTimeOffset serialization: a JsonNode round-trip escapes
        // '+' differently and would miss every historical cache entry.
        var legacyInstrument = JsonSerializer.Serialize(new { instrument.Id, instrument.IndexId, instrument.InstrumentToken,
            instrument.Role, instrument.File, instrument.Exchange, instrument.DefinitionVersion, instrument.AvailableAtUtc,
            instrument.ValidFromUtc, instrument.ValidToUtc, instrument.Expiry, instrument.Strike, instrument.LotSize,
            instrument.TickSize, instrument.Verified, instrument.ProviderMappingVerified, instrument.MappingMethod }, KiteCandleImporter.JsonOptions);
        var legacy = Path.Combine(Root, "candles", SafeName(legacyInstrument + $"|{date:yyyy-MM-dd}|{open}|{close}|minute|0|1"));
        if (legacy != folder && Directory.Exists(legacy))
            foreach (var path in Directory.EnumerateFiles(legacy, "*.meta.json").OrderDescending(StringComparer.Ordinal))
            {
                var entry = Read<HistoricalCacheEntry>(path); if (entry is null) continue;
                var raw = Path.Combine(legacy, entry.Sha256 + ".json");
                if (File.Exists(raw) && KiteCandleImporter.Hash(await File.ReadAllBytesAsync(raw, cancellationToken)) == entry.Sha256)
                    return entry with { RawFile = raw };
            }
        return null;
    }
    public async Task<HistoricalCacheEntry> CacheAsync(HistoricalInstrument instrument, DateOnly date, TimeOnly open, TimeOnly close,
        byte[] raw, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (instrument.ContinuousTradingClose is { } continuous && continuous < close) close = continuous;
        var parsed = KiteCandleImporter.Parse(raw, instrument.Id, TimeSpan.Zero);
        var start = Midnight(date, "Asia/Kolkata").Add(open.ToTimeSpan()); var end = Midnight(date, "Asia/Kolkata").Add(close.ToTimeSpan());
        var inSession = parsed.Candles.Where(bar => bar.StartUtc >= start && bar.StartUtc < end).ToArray();
        var complete = parsed.Issues.Count == 0 && inSession.Length == (end - start).TotalMinutes;
        var folder = CacheFolder(instrument, date, open, close); Directory.CreateDirectory(folder);
        var hash = KiteCandleImporter.Hash(raw); var file = Path.Combine(folder, hash + ".json");
        var repaired = await ImmutableBytes(file, raw, cancellationToken);
        var entry = new HistoricalCacheEntry(instrument.Id, date, hash, now, complete, inSession.Length, file, repaired);
        await AtomicJsonAsync(Path.Combine(folder, now.ToString("yyyyMMddHHmmssfffffff") + "-" + hash + ".meta.json"), entry with { RawFile = "" }, cancellationToken);
        await AtomicJsonAsync(Path.Combine(folder, "latest.json"), entry with { RawFile = "" }, cancellationToken);
        return entry;
    }
    private string CacheFolder(HistoricalInstrument instrument, DateOnly date, TimeOnly open, TimeOnly close) => Path.Combine(Root, "candles",
        SafeName(JsonSerializer.Serialize(instrument, KiteCandleImporter.JsonOptions) + $"|{date:yyyy-MM-dd}|{open}|{close}|minute|0|1"));
    public static DateTimeOffset Midnight(DateOnly date, string timezone) => new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.FindSystemTimeZoneById(timezone)));
    public static string SafeName(string text) => KiteCandleImporter.Hash(Encoding.UTF8.GetBytes(text));
    public static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        // Readers must not prevent atomic checkpoint replacement on Windows.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, KiteCandleImporter.JsonOptions);
    }
    public static async Task AtomicJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, KiteCandleImporter.JsonOptions), cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { File.Move(temporary, path, overwrite: true); break; }
                catch (Exception error) when (attempt < 6 && (error is UnauthorizedAccessException
                    || error is IOException && (error.HResult & 0xffff) is 32 or 33))
                {
                    // Short-lived readers/security scanners can deny replacement. Keep the
                    // previous checkpoint intact and retry publication, never the download.
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * (1 << attempt)), cancellationToken);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new HistoricalStorageException(error); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Preserve the publication failure. */ }
        }
    }
    private static async Task<bool> ImmutableBytes(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var expected = KiteCandleImporter.Hash(bytes); var repaired = false;
        if (File.Exists(path))
        {
            if (KiteCandleImporter.Hash(await File.ReadAllBytesAsync(path, cancellationToken)) == expected) return false;
            // Keep evidence of corruption. Only a newly supplied, hash-bound response
            // may repair the shared cache; earlier attempt files are not rewritten.
            File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N")); repaired = true;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                if (KiteCandleImporter.Hash(await File.ReadAllBytesAsync(path, cancellationToken)) != expected)
                    throw new InvalidDataException("Concurrent content-addressed source does not match its declared hash.");
            }
            return repaired;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
