using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trading.Engine;

public enum StrategyKind { FuturesOpeningRangeVwap }
public enum ConfirmationKind { WeightedConstituentVwap }
public enum RankingCriterion { SpreadPercentage, RoundTripFriction, StrikeDistance, ObservedDepth }

// These settings are host-loaded values, not IOptions or an ambient configuration source.
public sealed record EngineConfiguration
{
    public string Version { get; init; } = "paper-policy-v1";
    public IReadOnlyList<IndexStrategyConfiguration> Strategies { get; init; } =
        [new() { IndexId = "BANKNIFTY" }];
    public PortfolioConfiguration Portfolio { get; init; } = new();
    public PaperSimulationConfiguration PaperSimulation { get; init; } = new();

    public static EngineConfiguration FromJson(string json)
    {
        var configuration = JsonSerializer.Deserialize<EngineConfiguration>(json, JsonOptions)
            ?? throw new ArgumentException("Configuration must be an object.", nameof(json));
        configuration.ThrowIfInvalid();
        return configuration.Freeze();
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Version)) errors.Add("Version is required.");
        if (Strategies is null) errors.Add("Strategies is required.");
        else
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var strategy in Strategies)
            {
                if (strategy is null) { errors.Add("Strategies cannot contain null."); continue; }
                if (!ids.Add(strategy.IndexId)) errors.Add($"Duplicate strategy index: {strategy.IndexId}.");
                errors.AddRange(strategy.Validate());
            }
        }
        if (Portfolio is null) errors.Add("Portfolio is required.");
        else errors.AddRange(Portfolio.Validate());
        if (PaperSimulation is null) errors.Add("PaperSimulation is required.");
        else errors.AddRange(PaperSimulation.Validate());
        return errors.AsReadOnly();
    }

    internal void ThrowIfInvalid()
    {
        var errors = Validate();
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(EngineConfiguration));
    }

    internal EngineConfiguration Freeze() => this with
    {
        Strategies = Array.AsReadOnly(Strategies.Select(strategy => strategy.Freeze()).ToArray()),
        Portfolio = Portfolio with { IndexPriority = Array.AsReadOnly(Portfolio.IndexPriority.ToArray()) }
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
}

public sealed record IndexStrategyConfiguration
{
    public string IndexId { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public StrategyKind Kind { get; init; } = StrategyKind.FuturesOpeningRangeVwap;
    public ConfirmationKind Confirmation { get; init; } = ConfirmationKind.WeightedConstituentVwap;
    public TimeOnly OpeningStart { get; init; } = new(9, 15);
    public TimeOnly OpeningEnd { get; init; } = new(9, 30);
    public TimeOnly EntryStart { get; init; } = new(9, 30);
    public TimeOnly EntryEnd { get; init; } = new(11, 30);
    public TimeOnly SessionExit { get; init; } = new(15, 15);
    public TimeSpan BarInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan SetupLifetime { get; init; } = TimeSpan.FromMinutes(1);
    public decimal ConfirmationWeightPercent { get; init; } = 65m;
    public string BreadthPolicyVersion { get; init; } = "constituent-count-v1";
    public int? MinimumConfirmingConstituents { get; init; }
    public decimal WeightTotalTolerancePercent { get; init; } = 0m;
    public int Lots { get; init; } = 1;
    public int MaximumCandidates { get; init; } = 35;
    public bool ExcludeExpiryDay { get; init; } = true;
    public EvidenceConfiguration Evidence { get; init; } = new();
    public RiskConfiguration Risk { get; init; } = new();
    public PositionPolicyConfiguration Position { get; init; } = new();
    public IReadOnlyList<RankingCriterion> Ranking { get; init; } =
        [RankingCriterion.SpreadPercentage, RankingCriterion.RoundTripFriction,
         RankingCriterion.StrikeDistance, RankingCriterion.ObservedDepth];

    internal IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Require(bool condition, string message) { if (!condition) errors.Add($"Strategies[{IndexId}]: {message}"); }
        Require(!string.IsNullOrWhiteSpace(IndexId), "IndexId is required.");
        Require(Enum.IsDefined(Kind) && Enum.IsDefined(Confirmation), "Unsupported policy or confirmation kind.");
        Require(OpeningStart < OpeningEnd && OpeningEnd <= EntryStart && EntryStart < EntryEnd
            && EntryEnd < SessionExit, "Require OpeningStart < OpeningEnd <= EntryStart < EntryEnd < SessionExit.");
        Require(BarInterval == TimeSpan.FromMinutes(1), "This policy supports completed one-minute bars only.");
        Require(SetupLifetime > TimeSpan.Zero && SetupLifetime <= BarInterval, "SetupLifetime must be positive and at most one bar.");
        Require(ConfirmationWeightPercent > 50m && ConfirmationWeightPercent <= 100m, "ConfirmationWeightPercent must be > 50 and <= 100.");
        Require(!string.IsNullOrWhiteSpace(BreadthPolicyVersion) && (MinimumConfirmingConstituents is null or > 0),
            "BreadthPolicyVersion is required and MinimumConfirmingConstituents must be null (disabled) or positive.");
        Require(WeightTotalTolerancePercent >= 0m && WeightTotalTolerancePercent < 1m, "WeightTotalTolerancePercent must be >= 0 and < 1.");
        Require(Lots > 0 && MaximumCandidates > 0, "Lots and MaximumCandidates must be positive.");
        Require(Ranking is not null && Ranking.Count > 0 && Ranking.Distinct().Count() == Ranking.Count
            && Ranking.All(Enum.IsDefined), "Ranking must contain distinct supported criteria.");
        Require(Evidence is not null && Evidence.MaximumObservationAge > TimeSpan.Zero
            && Evidence.MaximumReceiptAge > TimeSpan.Zero && Evidence.MaximumQuoteSkew >= TimeSpan.Zero
            && Evidence.MaximumSpreadPercent > 0m && Evidence.MinimumDepthLots > 0,
            "Evidence ages, spread and depth must be positive; skew cannot be negative.");
        Require(Risk is not null && Risk.GrossCashStop > 0m && Risk.MaximumNetRisk > 0m
            && (Risk.NetCashStop is null or > 0m)
            && Risk.MinimumScenarioNetProfit > 0m && Risk.StructuralLookbackBars > 0
            && Risk.StructuralBufferMultiplier >= 0m && !string.IsNullOrWhiteSpace(Risk.FeeProfileId),
            "Risk budgets, scenario hurdle, lookback and FeeProfileId are required.");
        Require(Position is not null && Position.ThesisFailureCloses >= 2 && Position.TimeExitAfter > TimeSpan.Zero
            && Position.RequiredProgressR > 0m && Position.ProfitMilestone > 0m
            && Position.MaximumSoftWeakeningDuration > TimeSpan.Zero && Position.MaximumSoftWeakeningDuration <= TimeSpan.FromDays(1)
            && !string.IsNullOrWhiteSpace(Position.SoftThesisPolicyVersion),
            "Position close count, time, progress and milestone are invalid.");
        Require(Position?.ProfitExit is not null, "ProfitExit settings are required.");
        if (Position?.ProfitExit is { } profitExit) errors.AddRange(profitExit.Validate().Select(error => $"Strategies[{IndexId}]: {error}"));
        return errors;
    }

    internal IndexStrategyConfiguration Freeze() => this with { Ranking = Array.AsReadOnly(Ranking.ToArray()) };
}

public sealed record EvidenceConfiguration
{
    public TimeSpan MaximumObservationAge { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaximumReceiptAge { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaximumQuoteSkew { get; init; } = TimeSpan.FromSeconds(1);
    public decimal MaximumSpreadPercent { get; init; } = 1m;
    public int MinimumDepthLots { get; init; } = 1;
}

public sealed record RiskConfiguration
{
    public decimal GrossCashStop { get; init; } = 200m;
    // Separate opt-in position trigger, not the entry-feasibility ceiling below.
    public decimal? NetCashStop { get; init; }
    public decimal MaximumNetRisk { get; init; } = 400m;
    public decimal MinimumScenarioNetProfit { get; init; } = 500m;
    public int StructuralLookbackBars { get; init; } = 14;
    public decimal StructuralBufferMultiplier { get; init; } = 0.10m;
    public string FeeProfileId { get; init; } = "paper-fees";
}

public sealed record PositionPolicyConfiguration
{
    public string SoftThesisPolicyVersion { get; init; } = "soft-confirmation-v1";
    public TimeSpan MaximumSoftWeakeningDuration { get; init; } = TimeSpan.FromMinutes(2);
    public ProfitExitConfiguration ProfitExit { get; init; } = new();
    public int ThesisFailureCloses { get; init; } = 2;
    public TimeSpan TimeExitAfter { get; init; } = TimeSpan.FromMinutes(20);
    public decimal RequiredProgressR { get; init; } = 0.5m;
    public decimal ProfitMilestone { get; init; } = 500m;
}

public sealed record PortfolioConfiguration
{
    public int MaximumActiveOrPendingEntries { get; init; } = 1;
    public int MaximumActiveOrPendingEntriesPerIndex { get; init; } = 1;
    public int MaximumStrategyAttempts { get; init; } = 3;
    public int MaximumStrategyAttemptsPerIndex { get; init; } = 3;
    public int MaximumManualAttempts { get; init; } = 1;
    public decimal NetSessionPauseLoss { get; init; } = 1200m;
    public IReadOnlyList<string> IndexPriority { get; init; } = ["BANKNIFTY"];

    internal IReadOnlyList<string> Validate() => MaximumActiveOrPendingEntries > 0
        && MaximumActiveOrPendingEntriesPerIndex > 0 && MaximumStrategyAttempts > 0
        && MaximumStrategyAttemptsPerIndex > 0 && MaximumManualAttempts >= 0 && NetSessionPauseLoss > 0m
        && IndexPriority is not null && IndexPriority.All(id => !string.IsNullOrWhiteSpace(id))
        && IndexPriority.Distinct(StringComparer.Ordinal).Count() == IndexPriority.Count
        ? [] : ["Portfolio limits must be positive (manual allowance may be zero); IndexPriority must contain distinct nonempty IDs."];
}

// Engine logs these assumptions; a separate local simulator implements their lifecycle.
public sealed record PaperSimulationConfiguration
{
    public string Version { get; init; } = "paper-fill-v1";
    public decimal InitialCapital { get; init; } = 500000m;
    public TimeSpan ExecutionDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan PendingEntryTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public decimal EntryAdverseSlippage { get; init; } = 0m;
    public decimal ExitAdverseSlippage { get; init; } = 0m;

    internal IReadOnlyList<string> Validate() => !string.IsNullOrWhiteSpace(Version) && InitialCapital > 0m
        && ExecutionDelay >= TimeSpan.Zero && PendingEntryTimeout > ExecutionDelay
        && EntryAdverseSlippage >= 0m && ExitAdverseSlippage >= 0m
        ? [] : ["PaperSimulation requires a version, positive capital, timeout greater than delay and nonnegative delay/slippage."];
}
