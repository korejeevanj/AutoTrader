using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Trading.Domain;
using UglyToad.PdfPig;

namespace Trading.Data;

public sealed class PublishedBankInputsClient(HttpClient client, TimeProvider? clock = null)
{
    public async Task<HistoricalWeightSet?> WeightsAsync(string root, DateOnly date, CancellationToken token, int maximumSnapshotAgeDays = 45)
    {
        if (maximumSnapshotAgeDays <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSnapshotAgeDays));
        var sessionStart = HistoricalAcquisitionStore.Midnight(date, "Asia/Kolkata").AddHours(9).AddMinutes(15);
        for (var months = 1; months <= 12; months++)
        {
            var month = new DateOnly(date.Year, date.Month, 1).AddMonths(-months);
            var snapshotDate = month.AddMonths(1).AddDays(-1);
            if (date.DayNumber - snapshotDate.DayNumber > maximumSnapshotAgeDays) break;
            var folder = Path.Combine(root, "published-weights", month.ToString("yyyy-MM"));
            var record = Path.Combine(folder, "weights.json");
            var saved = HistoricalAcquisitionStore.Read<HistoricalWeightSet>(record);
            if (saved is not null && saved.AvailableAtUtc <= sessionStart) return saved;
            var suffix = month.ToString("MMMyyyy", CultureInfo.InvariantCulture);
            var url = $"https://www.niftyindices.com/Indices_-_Market_Capitalisation_and_Weightage/indices_data{suffix}.zip";
            using var read = new BoundedSourceRead(client.Timeout, clock ?? TimeProvider.System, token);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, read.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            if (!response.IsSuccessStatusCode) return null;
            // HTTP modification time is retained for diagnostic research only. It does
            // not establish the original publication time of a historical snapshot.
            var published = response.Content.Headers.LastModified;
            if (published is null || published > sessionStart) continue;
            var raw = await read.ResponseAsync(response.Content, 64 * 1024 * 1024, () => new InvalidDataException("Weight archive too large."));
            var members = ParseWeightArchive(raw, suffix, snapshotDate);
            var available = published.Value;
            var weights = new HistoricalWeightSet("BANKNIFTY", "published-" + snapshotDate.ToString("yyyy-MM-dd") + ":" + KiteCandleImporter.Hash(raw),
                url + "; full constituent table; published month-end weights carried forward, not daily weights",
                available, null, available, true, members, snapshotDate, "LATEST_PUBLISHED_MONTH_END_CARRIED_FORWARD",
                new(HistoricalAvailabilityMethod.HttpLastModified, url, available, KiteCandleImporter.Hash(raw)));
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, KiteCandleImporter.Hash(raw) + ".zip"), raw, token);
            await HistoricalAcquisitionStore.AtomicJsonAsync(record, weights, token);
            return weights;
        }
        return null;
    }

    public static IReadOnlyList<HistoricalWeight> ParseWeightArchive(byte[] raw, string monthSuffix, DateOnly snapshotDate)
    {
        using var zip = new ZipArchive(new MemoryStream(raw));
        var entry = zip.Entries.SingleOrDefault(entry => entry.Name.Equals($"NIFTY_Bank_{monthSuffix}.pdf", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Full Nifty Bank constituent PDF is missing.");
        if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("Weight PDF too large.");
        using var stream = entry.Open();
        using var pdf = PdfDocument.Open(BoundedSourceRead.Read(stream, 8 * 1024 * 1024, () => new InvalidDataException("Weight PDF too large.")));
        var members = new List<HistoricalWeight>();
        var expected = snapshotDate.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture).Replace(" ", "");
        var text = string.Concat(pdf.GetPages().Select(page => Regex.Replace(page.Text, @"\s", "")));
        if (!text.Contains(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Weight PDF snapshot date mismatch.");
        foreach (var page in pdf.GetPages())
        {
            var words = page.GetWords().ToArray();
            var symbols = words.Where(word => word.BoundingBox.Left < page.Width * 0.17
                && Regex.IsMatch(word.Text, "^[A-Z][A-Z0-9&-]{1,20}$", RegexOptions.CultureInvariant));
            foreach (var symbol in symbols)
            {
                var candidates = words.Where(word => word.BoundingBox.Left > page.Width * 0.86
                    && Math.Abs(word.BoundingBox.Bottom - symbol.BoundingBox.Bottom) <= 5
                    && Regex.IsMatch(word.Text, @"^\d{1,3}\.\d{2}$", RegexOptions.CultureInvariant)).ToArray();
                if (candidates.Length == 1)
                    members.Add(new("NSE:" + symbol.Text, decimal.Parse(candidates[0].Text, CultureInfo.InvariantCulture)));
            }
        }
        if (members.Count < 2 || members.Select(member => member.InstrumentId).Distinct().Count() != members.Count
            || members.Any(member => member.WeightPercent is <= 0m or > 100m)
            || Math.Abs(members.Sum(member => member.WeightPercent!.Value) - 100m) > 0.1m)
            throw new InvalidDataException("Published weight table is incomplete or inconsistent; no weights are invented/renormalized.");
        return members.AsReadOnly();
    }

    public async Task<(byte[] Raw, IReadOnlyList<NseEquitySecurity> Rows)?> EquitiesAsync(string root, DateOnly date, CancellationToken token)
    {
        var folder = Path.Combine(root, "equity-securities", date.ToString("yyyy-MM-dd"));
        var saved = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.csv.gz").FirstOrDefault() : null;
        var raw = saved is null ? await DownloadEquities(date, token) : await File.ReadAllBytesAsync(saved, token);
        if (raw is null) return null;
        var rows = ParseEquities(raw);
        Directory.CreateDirectory(folder);
        if (saved is null) await File.WriteAllBytesAsync(Path.Combine(folder, KiteCandleImporter.Hash(raw) + ".csv.gz"), raw, token);
        return (raw, rows);
    }

    private async Task<byte[]?> DownloadEquities(DateOnly date, CancellationToken token)
    {
        using var read = new BoundedSourceRead(client.Timeout, clock ?? TimeProvider.System, token);
        using var response = await client.GetAsync($"https://nsearchives.nseindia.com/content/cm/NSE_CM_security_{date:ddMMyyyy}.csv.gz",
            HttpCompletionOption.ResponseHeadersRead, read.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("Dated equity master unavailable.");
        return await read.ResponseAsync(response.Content, 32 * 1024 * 1024, () => new InvalidDataException("Equity master too large."));
    }
    public static IReadOnlyList<NseEquitySecurity> ParseEquities(byte[] raw)
    {
        var csv = BoundedSourceRead.ExpandGzip(raw, 128 * 1024 * 1024, () => new InvalidDataException("Expanded equity master too large."));
        using var rows = KiteInstrumentMaster.Rows(Encoding.UTF8.GetString(csv)).GetEnumerator();
        if (!rows.MoveNext()) throw new InvalidDataException("Empty equity master.");
        var columns = rows.Current; var header = columns.Select((name, i) => (name, i)).ToDictionary(item => item.name, item => item.i);
        string[] required = ["FinInstrmId", "TckrSymb", "SctySrs", "ISIN", "NewBrdLotQty", "BidIntrvl", "DelFlg", "ElgbltyNrmlMkt", "SctyStsNrmlMkt"];
        if (required.Any(name => !header.ContainsKey(name))) throw new InvalidDataException("Unsupported CM master schema.");
        var result = new List<NseEquitySecurity>();
        while (rows.MoveNext())
        {
            var row = rows.Current; if (row.Length != columns.Length) throw new InvalidDataException("Malformed CM row.");
            string Field(string name) => header.TryGetValue(name, out var i) ? row[i].Trim() : "";
            if (Field("SctySrs") != "EQ" || Field("DelFlg") != "N" || Field("ElgbltyNrmlMkt") != "1") continue;
            if (!int.TryParse(Field("SctyStsNrmlMkt"), out var status) || status is not (1 or 2 or 4 or 5 or 6)) continue;
            result.Add(new(long.Parse(Field("FinInstrmId"), CultureInfo.InvariantCulture), Field("TckrSymb"), Field("ISIN"),
                int.Parse(Field("NewBrdLotQty"), CultureInfo.InvariantCulture), decimal.Parse(Field("BidIntrvl"), CultureInfo.InvariantCulture) / 100m,
                Field("ElgbltyClsgAuctnSsn") == "1"));
        }
        return result.AsReadOnly();
    }
}
public sealed record NseEquitySecurity(long ExchangeToken, string Symbol, string Isin, int LotSize, decimal TickSize, bool ClosingAuction);

public sealed partial class HistoricalAcquisitionStore
{
    public async Task<HistoricalAcquisitionPlan> EnrichBanksAsync(HistoricalAcquisitionPlan plan, PublishedBankInputsClient client, DateOnly date,
        CancellationToken token, int maximumSnapshotAgeDays = 45)
    {
        if (plan.Manifest.Weights.Count > 0 && plan.Manifest.Instruments.Any(item => item.Role == HistoricalInstrumentRole.Constituent)) return plan;
        var weights = await client.WeightsAsync(Root, date, token, maximumSnapshotAgeDays);
        if (weights is null) return plan;
        var day = await client.EquitiesAsync(Root, date, token);
        if (day is null) return plan;
        IReadOnlyList<NseEquitySecurity> previous = [];
        for (var i = 1; i <= 7 && previous.Count == 0; i++)
        {
            var priorDate = date.AddDays(-i); if (priorDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var prior = await client.EquitiesAsync(Root, priorDate, token); if (prior is not null) previous = prior.Value.Rows;
        }
        var broker = ArchivedBrokerMappings(weights.Members.Select(member => member.InstrumentId.StartsWith("NSE:", StringComparison.Ordinal)
            ? member.InstrumentId[4..] : member.InstrumentId).ToHashSet(StringComparer.Ordinal));
        var start = Midnight(date, "Asia/Kolkata").Add(plan.Manifest.MarketOpen.ToTimeSpan());
        var end = Midnight(date, "Asia/Kolkata").Add(plan.Manifest.MarketClose.ToTimeSpan());
        var instruments = plan.Manifest.Instruments.ToList();
        foreach (var member in weights.Members)
        {
            var symbol = member.InstrumentId.StartsWith("NSE:", StringComparison.Ordinal) ? member.InstrumentId[4..] : member.InstrumentId;
            var matches = day.Value.Rows.Where(row => row.Symbol == symbol).ToArray(); if (matches.Length != 1) continue;
            var row = matches[0];
            var mappings = broker.Where(item => item.Exchange == "NSE" && item.InstrumentType == "EQ" && item.TradingSymbol == symbol
                && item.InstrumentToken >> 8 == row.ExchangeToken && (item.InstrumentToken & 0xff) == 1).ToArray();
            var mapped = mappings.Length == 1;
            var known = previous.Any(item => item.Symbol == symbol && item.ExchangeToken == row.ExchangeToken && item.Isin == row.Isin
                && item.LotSize == row.LotSize && item.TickSize == row.TickSize);
            instruments.Add(new() { Id = member.InstrumentId, IndexId = "BANKNIFTY", InstrumentToken = mapped ? mappings[0].InstrumentToken : checked(row.ExchangeToken * 256 + 1),
                Role = HistoricalInstrumentRole.Constituent, Exchange = "NSE", File = SafeName(member.InstrumentId) + ".json",
                DefinitionVersion = "NSE-CM:" + KiteCandleImporter.Hash(day.Value.Raw), AvailableAtUtc = mapped && known ? start : end,
                ValidFromUtc = start, ValidToUtc = end, LotSize = row.LotSize, TickSize = row.TickSize,
                Verified = mapped && known, ProviderMappingVerified = mapped, MappingMethod = "DATED_NSE_CM_EXACT_SYMBOL_TOKEN_JOIN",
                ContinuousTradingClose = row.ClosingAuction ? new TimeOnly(15, 15) : null });
        }
        var warnings = plan.Warnings.Where(warning => warning != "HISTORICAL_WEIGHTS_UNAVAILABLE").Append(
            $"WEIGHTS_PUBLISHED_SNAPSHOT:{weights.SnapshotDate:yyyy-MM-dd}:CARRIED_FORWARD_NOT_DAILY").ToList();
        if (weights.Members.Any(member => !instruments.Any(item => item.Id == member.InstrumentId))) warnings.Add("HISTORICAL_CONSTITUENT_DEFINITIONS_MISSING");
        return plan with { Manifest = plan.Manifest with { Instruments = instruments, Weights = [weights] }, Warnings = warnings.AsReadOnly() };
    }
}
