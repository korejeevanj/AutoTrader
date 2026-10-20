using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Trading.Domain;

namespace Trading.Data;

public static class KiteCandleImporter
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static HistoricalDataset Load(string manifestPath, TimeSpan publicationLag)
    {
        if (publicationLag < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(publicationLag));
        var path = Path.GetFullPath(manifestPath);
        var manifestBytes = File.ReadAllBytes(path);
        var manifest = JsonSerializer.Deserialize<HistoricalDatasetManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("Dataset manifest must be an object.");
        ValidateManifest(manifest);
        var root = Path.GetDirectoryName(path)!;
        if (manifest.Instruments.Select(item => Path.GetFullPath(item.File, root)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Instruments.Count)
            throw new InvalidDataException("Each instrument requires its own declared source file; aliases cannot relabel one response as different instruments.");
        var candles = new List<HistoricalCandle>();
        var issues = new List<HistoricalQualityIssue>();
        var files = new List<HistoricalFileProvenance>();
        var coverage = new List<HistoricalCoverage>();
        foreach (var instrument in manifest.Instruments.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            // Files may live elsewhere, but every path must be declared by this local manifest.
            var sourcePath = Path.GetFullPath(instrument.File, root);
            if (!File.Exists(sourcePath))
            {
                issues.Add(new(instrument.Id, instrument.Role is HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put ? "OPTION_FILE_UNAVAILABLE" : "FILE_MISSING", $"Missing declared file: {instrument.File}"));
                continue;
            }
            var bytes = File.ReadAllBytes(sourcePath);
            var imported = Parse(bytes, instrument.Id, publicationLag);
            candles.AddRange(imported.Candles);
            issues.AddRange(imported.Issues);
            files.Add(imported.Provenance with { File = instrument.File });
        }
        foreach (var index in manifest.Indices)
        {
            var timezone = TimeZoneInfo.FindSystemTimeZoneById(index.TimeZone);
            foreach (var instrument in manifest.Instruments.Where(item => item.IndexId == index.Id))
            {
                var instrumentBars = candles.Where(bar => bar.InstrumentId == instrument.Id).ToArray();
                foreach (var bar in instrumentBars)
                {
                    var local = TimeZoneInfo.ConvertTime(bar.StartUtc, timezone);
                    if (!manifest.TradingDates.Contains(DateOnly.FromDateTime(local.DateTime))
                        || TimeOnly.FromDateTime(local.DateTime) < manifest.MarketOpen
                        || TimeOnly.FromDateTime(local.DateTime) >= manifest.MarketClose)
                        issues.Add(new(instrument.Id, "OUTSIDE_DECLARED_SESSION", "Candle is outside declared trading dates/session.", bar.StartUtc));
                }
                foreach (var date in manifest.TradingDates)
                {
                    var midnight = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), timezone));
                    var option = instrument.Role is HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put;
                    var analysis = HistoricalCoverageAnalysis.ForSession(manifest, instrument, date, instrumentBars);
                    var summary = analysis.Coverage;
                    if (!option)
                        foreach (var gap in analysis.MissingIntervals)
                            for (var start = gap.StartUtc; start < gap.EndUtc; start = start.AddMinutes(1))
                                issues.Add(new(instrument.Id, "MISSING_MINUTE", "Required-series candle unavailable; not forward-filled.", start));
                    if (option && summary.MissingMinutes > 0) issues.Add(new(instrument.Id, summary.ReturnedMinutes == 0 ? "NO_OPTION_CANDLES" : "SPARSE_OPTION_CANDLES",
                        $"{summary.ReturnedMinutes} returned / {summary.ExpectedMinutes} possible minutes. Absence is not proof of no trading; candidate needs usable current evidence.", midnight));
                    coverage.Add(summary);
                }
            }
        }
        return new(manifest, Array.AsReadOnly(candles.OrderBy(bar => bar.StartUtc)
            .ThenBy(bar => bar.InstrumentId, StringComparer.Ordinal).ToArray()), issues.AsReadOnly(), files.AsReadOnly(), Hash(manifestBytes), coverage.AsReadOnly());
    }

    public static (IReadOnlyList<HistoricalCandle> Candles, IReadOnlyList<HistoricalQualityIssue> Issues,
        HistoricalFileProvenance Provenance) Parse(byte[] json, string instrumentId, TimeSpan publicationLag)
    {
        if (string.IsNullOrWhiteSpace(instrumentId) || publicationLag < TimeSpan.Zero)
            throw new ArgumentException("Instrument identity and nonnegative publication lag are required.");
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement;
        if (rows.ValueKind == JsonValueKind.Object)
        {
            if (!rows.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "success"
                || !rows.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("candles", out rows))
                throw new InvalidDataException("Expected successful Kite response with data.candles.");
        }
        if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Candles must be an array.");
        var parsed = new List<HistoricalCandle>();
        var issues = new List<HistoricalQualityIssue>();
        var inputRows = 0;
        foreach (var row in rows.EnumerateArray())
        {
            inputRows++;
            try
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() is not (6 or 7))
                    throw new FormatException("Expected timestamp/OHLC/volume and optional OI (6 or 7 fields).");
                var timestamp = row[0].GetString() ?? throw new FormatException("Timestamp is missing.");
                if (!Regex.IsMatch(timestamp, @"(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.CultureInvariant))
                    throw new FormatException("Timestamp must include an explicit UTC offset.");
                timestamp = Regex.Replace(timestamp, @"([+-]\d{2})(\d{2})$", "$1:$2", RegexOptions.CultureInvariant);
                var start = DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None).ToUniversalTime();
                var open = row[1].GetDecimal(); var high = row[2].GetDecimal();
                var low = row[3].GetDecimal(); var close = row[4].GetDecimal(); var volume = row[5].GetDecimal();
                decimal? oi = row.GetArrayLength() == 7 ? row[6].GetDecimal() : null;
                if (start.Ticks % TimeSpan.TicksPerMinute != 0 || low <= 0m || high < low
                    || open < low || open > high || close < low || close > high || volume < 0m || oi < 0m)
                    throw new FormatException("Invalid minute alignment, OHLC relationships or negative volume/OI.");
                parsed.Add(new(instrumentId, start, start.AddMinutes(1), start.AddMinutes(1).Add(publicationLag), open, high, low, close, volume, oi));
            }
            catch (Exception error) when (error is FormatException or InvalidOperationException or OverflowException or ArgumentException)
            { issues.Add(new(instrumentId, "INVALID_ROW", $"Row {inputRows}: {error.Message}")); }
        }
        var accepted = new List<HistoricalCandle>();
        var duplicates = 0;
        foreach (var period in parsed.GroupBy(bar => bar.StartUtc).OrderBy(group => group.Key))
        {
            var distinct = period.Distinct().ToArray();
            if (distinct.Length > 1) issues.Add(new(instrumentId, "CONFLICTING_DUPLICATES", "All conflicting rows at this minute quarantined.", period.Key));
            else { accepted.Add(distinct[0]); duplicates += period.Count() - 1; }
        }
        return (accepted.AsReadOnly(), issues.AsReadOnly(), new(instrumentId, "", Hash(json), inputRows, accepted.Count, duplicates));
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static void ValidateManifest(HistoricalDatasetManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.DatasetId)
            || manifest.Provider != "KITE" || string.IsNullOrWhiteSpace(manifest.UniverseVersion)
            || manifest.MarketOpen >= manifest.MarketClose || manifest.TradingDates is null || manifest.TradingDates.Count == 0
            || manifest.TradingDates.Distinct().Count() != manifest.TradingDates.Count
            || manifest.Indices is null || manifest.Indices.Count == 0 || manifest.Instruments is null || manifest.Weights is null
            || manifest.Indices.Any(index => string.IsNullOrWhiteSpace(index.Id) || string.IsNullOrWhiteSpace(index.TimeZone)
                || index.IsSynthetic != manifest.IsSynthetic)
            || manifest.Indices.Select(index => index.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Indices.Count
            || manifest.Instruments.Any(item => string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.File)
                || item.InstrumentToken <= 0 || !Enum.IsDefined(item.Role) || !manifest.Indices.Any(index => index.Id == item.IndexId))
            || manifest.Instruments.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Instruments.Count
            || manifest.Instruments.Select(item => item.InstrumentToken).Distinct().Count() != manifest.Instruments.Count
            || manifest.Weights.Any(weights => weights.Members is null || string.IsNullOrWhiteSpace(weights.IndexId)
                || !manifest.Indices.Any(index => index.Id == weights.IndexId))
            || manifest.Instruments.GroupBy(item => item.IndexId).Any(group => group.Count(item => item.Role == HistoricalInstrumentRole.Futures) != 1))
            throw new InvalidDataException("Manifest requires unique identities/dates, one declared futures per index, explicit files/tokens and a valid session.");
        foreach (var index in manifest.Indices)
            if (manifest.Instruments.Count(item => item.IndexId == index.Id && item.Role == HistoricalInstrumentRole.Futures) != 1)
                throw new InvalidDataException($"Index {index.Id} requires exactly one selected futures file.");
    }
}
