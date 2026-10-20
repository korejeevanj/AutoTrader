namespace Trading.Data;

public sealed partial class HistoricalAcquisitionStore
{
    public async Task<HistoricalAcquisitionPlan?> ResolveNseAsync(NseHistoricalContractClient client, DateOnly date,
        TimeOnly open, TimeOnly close, CancellationToken token)
    {
        var day = await ExchangeSnapshot(client, date, token);
        if (day is null) return null;
        NseContractSnapshot? previous = null;
        for (var i = 1; i <= 7 && previous is null; i++)
        {
            var prior = date.AddDays(-i);
            if (prior.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            previous = await ExchangeSnapshot(client, prior, token);
        }
        // Broker rows are used ONLY for an exact identifier join. Universe/lot/tick
        // selection always comes from the chosen day's exchange contract file.
        return HistoricalContractResolution.Build(day, previous, ArchivedBrokerMappings(day.Contracts.Select(contract => contract.Symbol).ToHashSet(StringComparer.Ordinal)), open, close);
    }

    private async Task<NseContractSnapshot?> ExchangeSnapshot(NseHistoricalContractClient client, DateOnly date, CancellationToken token)
    {
        var folder = Path.Combine(Root, "exchange-contracts", date.ToString("yyyy-MM-dd"));
        if (Directory.Exists(folder))
        {
            var file = Directory.EnumerateFiles(folder, "*.csv.gz").Order(StringComparer.Ordinal).FirstOrDefault();
            if (file is not null) return NseHistoricalContractClient.Parse(await File.ReadAllBytesAsync(file, token), date);
        }
        var raw = await client.DownloadAsync(date, token);
        if (raw is null) return null;
        var snapshot = NseHistoricalContractClient.Parse(raw, date);
        Directory.CreateDirectory(folder);
        await ImmutableBytes(Path.Combine(folder, snapshot.Sha256 + ".csv.gz"), raw, token);
        await AtomicJsonAsync(Path.Combine(folder, snapshot.Sha256 + ".json"), snapshot, token);
        return snapshot;
    }

    private IReadOnlyList<KiteMasterRow> ArchivedBrokerMappings(IReadOnlySet<string> symbols)
    {
        var root = Path.Combine(Root, "masters");
        if (!Directory.Exists(root)) return [];
        IEnumerable<KiteMasterRow> Rows()
        {
            foreach (var folder in Directory.EnumerateDirectories(root).OrderDescending(StringComparer.Ordinal))
                foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderDescending(StringComparer.Ordinal))
                {
                    var snapshot = Read<KiteMasterSnapshot>(file);
                    if (snapshot is not null)
                        foreach (var row in snapshot.Instruments.Where(row => symbols.Contains(row.TradingSymbol))) yield return row;
                }
        }
        // Later masters may drop expired contracts. Preserve exact old identities;
        // coalesce repeated mappings without using current lots/ticks as history.
        return Rows().DistinctBy(row => (row.InstrumentToken, row.Exchange, row.TradingSymbol, row.InstrumentType, row.Expiry, row.Strike)).ToArray();
    }
}
