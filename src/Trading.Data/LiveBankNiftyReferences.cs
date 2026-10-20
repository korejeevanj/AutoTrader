using System.Text;
using Trading.Domain;

namespace Trading.Data;

public sealed record LiveBankReferences(HistoricalWeightSet? Weights, byte[] MembershipCsv, byte[] WeightArchive, IReadOnlyList<string> Issues);

public static class LiveBankNiftyReferences
{
    public static async Task<LiveBankReferences> LoadAsync(PublishedBankInputsClient weightsClient, HttpClient http,
        string cacheRoot, DateOnly date, DateTimeOffset captured, CancellationToken token)
    {
        var issues = new List<string>(); HistoricalWeightSet? weights = null; byte[] membership = []; byte[] archive = [];
        try
        {
            weights = await weightsClient.WeightsAsync(cacheRoot, date, token);
            if (weights?.SnapshotDate is { } snapshotDate)
            {
                var hash = weights.Version.Split(':')[^1];
                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid weight archive identity.");
                archive = await File.ReadAllBytesAsync(Path.Combine(cacheRoot, "published-weights", snapshotDate.ToString("yyyy-MM"), hash + ".zip"), token);
                if (!KiteCandleImporter.Hash(archive).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Weight archive hash mismatch.");
            }
            using var read = new BoundedSourceRead(http.Timeout, TimeProvider.System, token);
            using var response = await http.GetAsync("https://www.niftyindices.com/IndexConstituent/ind_niftybanklist.csv",
                HttpCompletionOption.ResponseHeadersRead, read.Token);
            response.EnsureSuccessStatusCode();
            membership = await read.ResponseAsync(response.Content, 1024 * 1024, () => new InvalidDataException("Membership too large."));
            var rows = KiteInstrumentMaster.Rows(Encoding.UTF8.GetString(membership)).ToArray();
            var symbolColumn = rows.Length == 0 ? -1 : Array.IndexOf(rows[0], "Symbol");
            if (symbolColumn < 0 || rows.Skip(1).Any(row => row.Length <= symbolColumn)) throw new InvalidDataException("Invalid membership schema.");
            var members = rows.Skip(1).Select(row => "NSE:" + row[symbolColumn]).ToHashSet(StringComparer.Ordinal);
            if (weights is null || weights.SnapshotDate is not { } snapshot || date.DayNumber - snapshot.DayNumber is < 0 or > 45
                || !weights.Verified || !members.SetEquals(weights.Members.Select(member => member.InstrumentId)))
            { issues.Add("WEIGHTS_OR_CURRENT_MEMBERSHIP_UNVERIFIED"); weights = null; }
            else
                // For live decisions, our capture (not inferred old publication time) establishes availability.
                weights = weights with { AvailableAtUtc = captured, EffectiveFromUtc = captured,
                    PublicationEvidence = new(HistoricalAvailabilityMethod.ContemporaneousCapture,
                        weights.Source + "; membership captured from official current constituent CSV", captured,
                        KiteCandleImporter.Hash(membership)) };
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException
            || error is OperationCanceledException && !token.IsCancellationRequested)
        { issues.Add("BANK_REFERENCE_DOWNLOAD_FAILED"); weights = null; }
        return new(weights, membership, archive, issues.AsReadOnly());
    }

    public static IReadOnlyList<HistoricalInstrument> Resolve(KiteMasterSnapshot master, DateOnly date, HistoricalWeightSet? weights)
    {
        var midnight = HistoricalAcquisitionStore.Midnight(date, "Asia/Kolkata");
        var candidates = master.Instruments.Where(row => row.Exchange == "NFO" && row.Name == "BANKNIFTY").ToArray();
        var future = candidates.Where(row => row.InstrumentType == "FUT" && row.Expiry > date)
            .OrderBy(row => row.Expiry).FirstOrDefault() ?? throw new InvalidDataException("BANKNIFTY_FUTURE_UNAVAILABLE");
        var index = master.Instruments.SingleOrDefault(row => row.Exchange == "NSE" && row.TradingSymbol == "NIFTY BANK")
            ?? throw new InvalidDataException("BANKNIFTY_INDEX_UNAVAILABLE");
        var selected = new List<(KiteMasterRow Row, HistoricalInstrumentRole Role)> { (index, HistoricalInstrumentRole.CashIndex), (future, HistoricalInstrumentRole.Futures) };
        selected.AddRange(candidates.Where(row => row.Expiry == future.Expiry && row.InstrumentType is "CE" or "PE")
            .Select(row => (row, row.InstrumentType == "CE" ? HistoricalInstrumentRole.Call : HistoricalInstrumentRole.Put)));
        foreach (var member in weights?.Members ?? [])
        {
            var row = master.Instruments.SingleOrDefault(row => row.Exchange == "NSE" && "NSE:" + row.TradingSymbol == member.InstrumentId && row.InstrumentType == "EQ");
            if (row is not null) selected.Add((row, HistoricalInstrumentRole.Constituent));
        }
        return selected.Select(item => new HistoricalInstrument { Id = item.Row.Exchange + ":" + item.Row.TradingSymbol,
            IndexId = "BANKNIFTY", InstrumentToken = item.Row.InstrumentToken, Role = item.Role, Exchange = item.Row.Exchange,
            Expiry = item.Row.Expiry, Strike = item.Row.Strike, LotSize = item.Row.LotSize, TickSize = item.Row.TickSize,
            ValidFromUtc = midnight, ValidToUtc = midnight.AddDays(1), AvailableAtUtc = master.CapturedAtUtc,
            DefinitionVersion = "KITE-MASTER:" + master.Sha256, Verified = true, ProviderMappingVerified = true,
            MappingMethod = "CONTEMPORANEOUS_KITE_MASTER" }).ToArray();
    }
}
