using System.Globalization;
using System.Net;
using System.Text;
using Trading.Domain;

namespace Trading.Data;

public sealed record NseDatedContract(long ExchangeToken, string Symbol, string Kind, DateOnly Expiry,
    decimal? Strike, string Right, int LotSize, decimal TickSize, bool Reserved, bool Deleted,
    int NormalMarketStatus, int NormalMarketEligibility, DateOnly? AdmissionDate, string RecordUpdate)
{
    // NSE's Sep-2026 pre-open FAQ explicitly defines 6 as price discovery for the
    // front month. A daily pre-open marker must not exclude it for the whole day.
    public bool Eligible => !Reserved && !Deleted && NormalMarketStatus is 1 or 2 or 4 or 5 or 6 && NormalMarketEligibility == 1;
}
public sealed record NseContractSnapshot(DateOnly TradingDate, string SourceUrl, string Sha256,
    IReadOnlyList<NseDatedContract> Contracts);

public sealed class NseHistoricalContractClient(HttpClient client, TimeProvider? clock = null)
{
    public async Task<byte[]?> DownloadAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var url = $"https://nsearchives.nseindia.com/content/fo/NSE_FO_contract_{date:ddMMyyyy}.csv.gz";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var read = new BoundedSourceRead(client.Timeout, clock ?? TimeProvider.System, cancellationToken);
        request.Headers.UserAgent.ParseAdd("AutoTrader/1.0");
        // This public exchange client never receives Kite credentials or browser cookies.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, read.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("NSE dated contract archive unavailable.");
        return await read.ResponseAsync(response.Content, 32 * 1024 * 1024, () => new InvalidDataException("NSE archive is too large."));
    }

    public static NseContractSnapshot Parse(byte[] raw, DateOnly date)
    {
        var csv = BoundedSourceRead.ExpandGzip(raw, 128 * 1024 * 1024, () => new InvalidDataException("Expanded NSE archive is too large."));
        using var rows = KiteInstrumentMaster.Rows(Encoding.UTF8.GetString(csv)).GetEnumerator();
        if (!rows.MoveNext()) throw new InvalidDataException("Empty NSE contract archive.");
        var columns = rows.Current;
        var header = columns.Select((value, i) => (value: value.Trim().TrimStart('\uFEFF'), i)).ToDictionary(item => item.value, item => item.i, StringComparer.Ordinal);
        string[] required = ["FinInstrmId", "FinInstrmNm", "TckrSymb", "XpryDt", "StrkPric", "OptnTp", "PrtdToTrad",
            "NewBrdLotQty", "BidIntrvl", "StockNm", "DelFlg", "TradgStsNrmlMkt", "ElgbltyNrmlMkt"];
        if (required.Any(name => !header.ContainsKey(name))) throw new InvalidDataException("Unsupported NSE contract-master schema.");
        var contracts = new List<NseDatedContract>();
        while (rows.MoveNext())
        {
            var row = rows.Current;
            if (row.Length != columns.Length) throw new InvalidDataException("Malformed NSE contract row.");
            string Field(string name) => header.TryGetValue(name, out var index) ? row[index].Trim() : "";
            if (Field("TckrSymb") != "BANKNIFTY" || Field("FinInstrmNm") is not ("FUTIDX" or "OPTIDX")) continue;
            var expiry = NseDate(Field("XpryDt")) ?? throw new InvalidDataException("NSE expiry is unavailable.");
            var token = long.Parse(Field("FinInstrmId"), CultureInfo.InvariantCulture);
            var lot = int.Parse(Field("NewBrdLotQty"), CultureInfo.InvariantCulture);
            var tick = decimal.Parse(Field("BidIntrvl"), CultureInfo.InvariantCulture) / 100m;
            var kind = Field("FinInstrmNm");
            decimal? strike = kind == "OPTIDX" ? decimal.Parse(Field("StrkPric"), CultureInfo.InvariantCulture) / 100m : null;
            if (token <= 0 || lot <= 0 || tick <= 0m || strike <= 0m) throw new InvalidDataException("Invalid NSE contract identity/lot/tick.");
            // MSD55276 Annexure-B: PrtdToTrad is Reserved Identifier, NOT a permission flag.
            contracts.Add(new(token, Field("StockNm"), kind, expiry, strike, Field("OptnTp"), lot, tick,
                Field("PrtdToTrad") != "0", Field("DelFlg") != "N",
                int.Parse(Field("TradgStsNrmlMkt"), CultureInfo.InvariantCulture),
                int.Parse(Field("ElgbltyNrmlMkt"), CultureInfo.InvariantCulture), NseDate(Field("AdmssnDt")), Field("StockRcrdDt")));
        }
        if (contracts.Count == 0 || contracts.Select(item => item.ExchangeToken).Distinct().Count() != contracts.Count)
            throw new InvalidDataException("Missing or conflicting Bank Nifty contracts in the dated NSE file.");
        return new(date, $"https://nsearchives.nseindia.com/content/fo/NSE_FO_contract_{date:ddMMyyyy}.csv.gz",
            KiteCandleImporter.Hash(raw), contracts.AsReadOnly());
    }

    private static DateOnly? NseDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "0" || raw == "-1") return null;
        if (long.TryParse(raw, CultureInfo.InvariantCulture, out var seconds))
            return DateOnly.FromDateTime(new DateTime(1980, 1, 1).AddSeconds(seconds));
        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        throw new InvalidDataException("Unsupported NSE date representation.");
    }
}

public sealed partial class HistoricalContractResolution
{
    public static HistoricalAcquisitionPlan Build(NseContractSnapshot day, NseContractSnapshot? previous,
        IReadOnlyList<KiteMasterRow> providerRows, TimeOnly open, TimeOnly close)
    {
        var start = HistoricalAcquisitionStore.Midnight(day.TradingDate, "Asia/Kolkata").Add(open.ToTimeSpan());
        var end = HistoricalAcquisitionStore.Midnight(day.TradingDate, "Asia/Kolkata").Add(close.ToTimeSpan());
        var eligible = day.Contracts.Where(contract => contract.Eligible && contract.Expiry >= day.TradingDate
            && (contract.AdmissionDate is null || contract.AdmissionDate <= day.TradingDate)).ToArray();
        var future = eligible.Where(contract => contract.Kind == "FUTIDX" && contract.Expiry > day.TradingDate)
            .OrderBy(contract => contract.Expiry).ThenBy(contract => contract.ExchangeToken).FirstOrDefault();
        if (future is null) throw new InvalidDataException("Dated NSE master has no eligible non-expiry-day future.");
        var definitions = new List<HistoricalInstrument>
        {
            new() { Id = "NSE:NIFTY BANK", IndexId = "BANKNIFTY", InstrumentToken = 260105, Role = HistoricalInstrumentRole.CashIndex,
                Exchange = "NSE", DefinitionVersion = "banknifty-index-v1", AvailableAtUtc = start,
                ValidFromUtc = start, ValidToUtc = end, Verified = true, File = HistoricalAcquisitionStore.SafeName("NSE:NIFTY BANK") + ".json" }
        };
        var unresolved = 0;
        foreach (var contract in eligible.Where(contract => contract.Kind == "OPTIDX" || contract.ExchangeToken == future.ExchangeToken))
        {
            var id = "NFO:" + contract.Symbol;
            var exact = providerRows.Where(row => row.Exchange == "NFO" && row.TradingSymbol == contract.Symbol
                && row.Expiry == contract.Expiry && (contract.Kind == "FUTIDX" ? row.InstrumentType == "FUT"
                    : row.InstrumentType == contract.Right && row.Strike == contract.Strike)
                && row.InstrumentToken >> 8 == contract.ExchangeToken && (row.InstrumentToken & 0xff) == 2).ToArray();
            var mapped = exact.Length == 1;
            if (!mapped) unresolved++;
            // Staff-confirmed token layout is only a diagnostic candidate without an exact provider join.
            var token = mapped ? exact[0].InstrumentToken : checked(contract.ExchangeToken * 256 + 2);
            if (token > uint.MaxValue) throw new InvalidDataException("NFO token exceeds supported range.");
            var prior = previous?.Contracts.SingleOrDefault(row => row.ExchangeToken == contract.ExchangeToken
                && row.Symbol == contract.Symbol && row.Expiry == contract.Expiry && row.Strike == contract.Strike
                && row.LotSize == contract.LotSize && row.TickSize == contract.TickSize && row.Eligible);
            var pointInTime = prior is not null && mapped;
            definitions.Add(new() { Id = id, IndexId = "BANKNIFTY", InstrumentToken = token, Exchange = "NFO",
                Role = contract.Kind == "FUTIDX" ? HistoricalInstrumentRole.Futures : contract.Right == "CE" ? HistoricalInstrumentRole.Call : HistoricalInstrumentRole.Put,
                Expiry = contract.Expiry, Strike = contract.Strike, LotSize = contract.LotSize, TickSize = contract.TickSize,
                DefinitionVersion = "NSE:" + day.Sha256, AvailableAtUtc = pointInTime ? start : end,
                ValidFromUtc = start, ValidToUtc = end, Verified = pointInTime, ProviderMappingVerified = mapped,
                MappingMethod = mapped ? "EXACT_DATED_CONTRACT_IDENTITY_JOIN" : "ARITHMETIC_TOKEN_CANDIDATE_NOT_REQUESTED",
                File = HistoricalAcquisitionStore.SafeName(id) + ".json" });
        }
        var warnings = new List<string> { "HISTORICAL_CONTRACTS_FROM_DATED_NSE_MASTER", "HISTORICAL_WEIGHTS_UNAVAILABLE" };
        if (previous is null) warnings.Add("PRESESSION_CATALOGUE_UNAVAILABLE_END_OF_DAY_IDENTITIES_ONLY");
        else warnings.Add("PRESESSION_AVAILABILITY_MODELLED_FROM_PREVIOUS_DATED_MASTER");
        if (unresolved != 0) warnings.Add($"KITE_MAPPING_CANDIDATES_UNVERIFIED:{unresolved}");
        if (definitions.Any(item => !item.Verified)) warnings.Add("UNVERIFIED_CONTRACT_METADATA");
        return new(new() { DatasetId = $"BANKNIFTY-{day.TradingDate:yyyy-MM-dd}", TradingDates = [day.TradingDate],
            UniverseVersion = "NSE:" + day.Sha256, UniverseAvailableAtUtc = start, IncludesAllOptions = true, MarketOpen = open, MarketClose = close,
            Instruments = definitions.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            Indices = [new("BANKNIFTY", "Bank Nifty", "NSE", "Asia/Kolkata", "Banks", "NSE:" + day.Sha256,
                start, null, false, new(true, true, true))] }, true,
            day.SourceUrl + $"; {day.Contracts.Count} inventory records, {eligible.Length} eligible records; exchange lot/tick values, not current broker values",
            warnings.AsReadOnly());
    }
}
