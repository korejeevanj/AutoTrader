using Microsoft.Extensions.Options;
using Trading.Domain;

namespace Trading.Api;

public sealed class SyntheticDashboardOptions
{
    public List<SyntheticIndexSeed> Indices { get; set; } = [];
}

public sealed class SyntheticIndexSeed
{
    public IndexDefinition Definition { get; set; } = null!;
    public string FuturesSymbol { get; set; } = "";
    public string ContractPrefix { get; set; } = "";
    public decimal FuturesBasePrice { get; set; }
    public decimal IndexValue { get; set; }
    public decimal FirstStrike { get; set; }
    public decimal StrikeStep { get; set; }
    public decimal OptionBaseBid { get; set; }
    public int LotSize { get; set; }
    public decimal TickSize { get; set; }
    public string Expiry { get; set; } = "";
    public DateTimeOffset ValidFromUtc { get; set; }
    public DateTimeOffset ValidToUtc { get; set; }
    public List<SyntheticConstituentSeed> Constituents { get; set; } = [];
}

public sealed record SyntheticConstituentSeed(string Id, string Name, decimal WeightPercent,
    decimal LastPrice, decimal Vwap, decimal ChangePercent);

// Configuration and fixture details are hidden behind identity lookup. No symbol-specific workflow.
public sealed class SyntheticIndexCatalog : IIndexCatalog
{
    private readonly Dictionary<string, SyntheticIndexSeed> seeds;
    public IReadOnlyList<IndexDefinition> Indices { get; }

    public SyntheticIndexCatalog(IOptions<SyntheticDashboardOptions> options)
    {
        var configured = options.Value.Indices;
        var errors = new List<string>();
        if (configured.Count == 0) errors.Add("At least one synthetic index is required.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in configured)
        {
            var d = seed.Definition;
            if (d is null || string.IsNullOrWhiteSpace(d.Id) || !ids.Add(d.Id)
                || string.IsNullOrWhiteSpace(d.DisplayName) || string.IsNullOrWhiteSpace(d.Exchange)
                || d.TimeZone != "Asia/Kolkata" || string.IsNullOrWhiteSpace(d.ConstituentLabel)
                || string.IsNullOrWhiteSpace(d.DefinitionVersion) || !d.IsSynthetic || d.Capabilities is null
                || d.EffectiveFromUtc == default || d.EffectiveToUtc <= d.EffectiveFromUtc)
                errors.Add("Index definitions require unique identities and complete synthetic metadata.");
            if (string.IsNullOrWhiteSpace(seed.ContractPrefix) || !prefixes.Add(seed.ContractPrefix)
                || string.IsNullOrWhiteSpace(seed.FuturesSymbol) || !symbols.Add(seed.FuturesSymbol)
                || seed.FuturesBasePrice <= 0 || seed.IndexValue <= 0 || seed.FirstStrike <= 0
                || seed.StrikeStep <= 0 || seed.OptionBaseBid <= 0 || seed.LotSize <= 0
                || seed.LotSize > int.MaxValue / 5 || seed.TickSize <= 0
                || !DateOnly.TryParseExact(seed.Expiry, "yyyy-MM-dd", out _)
                || seed.ValidFromUtc == default || seed.ValidToUtc <= seed.ValidFromUtc
                || seed.Constituents.Count < 4 || seed.Constituents.Sum(x => x.WeightPercent) != 100m
                || seed.Constituents.Select(x => x.Id).Distinct().Count() != seed.Constituents.Count
                || seed.Constituents.Any(x => string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Name)
                    || x.WeightPercent <= 0 || x.LastPrice <= 0 || x.Vwap <= 0))
                errors.Add($"Invalid synthetic fixture seeds for {d?.Id}.");
        }
        if (errors.Count > 0) throw new OptionsValidationException(nameof(SyntheticDashboardOptions), typeof(SyntheticDashboardOptions), errors);
        seeds = configured.ToDictionary(x => x.Definition.Id, StringComparer.Ordinal);
        Indices = Array.AsReadOnly(configured.Select(x => x.Definition).ToArray());
    }

    public IndexDefinition? Find(string indexId) => seeds.GetValueOrDefault(indexId)?.Definition;
    internal SyntheticIndexSeed? Fixture(string indexId) => seeds.GetValueOrDefault(indexId);
}
