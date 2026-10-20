using System.Text.Json;
using Trading.Data;
using Trading.Risk;

namespace Trading.Replay;

public sealed record CandleExecutionPreset
{
    public string Id { get; init; } = "base";
    public int FullSpreadTicks { get; init; } = 20;
    public int AdverseSlippageTicks { get; init; } = 2;
    public decimal GapStressRupees { get; init; } = 50m;
    public TimeSpan ExecutionDelay { get; init; } = TimeSpan.Zero;
    public TimeSpan PendingEntryTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

public sealed record ReplayConfiguration
{
    public string Version { get; init; } = "candle-replay-v1";
    public string RunId { get; init; } = "historical-replay";
    public string EngineProfile { get; init; } = "../Trading.Engine/engine.paper.example.json";
    public TimeSpan PublicationLag { get; init; } = TimeSpan.Zero;
    public bool EnableCandleResearch { get; init; } = true;
    public int MaximumWeightSnapshotAgeDays { get; init; } = 45;
    public DateOnly? FromDate { get; init; }
    public DateOnly? ToDate { get; init; }
    public IReadOnlyList<CandleExecutionPreset> Presets { get; init; } =
        [new(), new() { Id = "adverse", FullSpreadTicks = 40, AdverseSlippageTicks = 10 }];
    public IReadOnlyList<ResearchFeeSchedule> FeeSchedules { get; init; } = [];
    public DirectionalResearchConfiguration DirectionalResearch { get; init; } = new();
    public static ReplayConfiguration FromJson(string json)
    {
        var value = JsonSerializer.Deserialize<ReplayConfiguration>(json, KiteCandleImporter.JsonOptions)
            ?? throw new ArgumentException("Replay configuration must be an object.");
        value.Validate();
        return value;
    }
    public string ToJson() => JsonSerializer.Serialize(this, KiteCandleImporter.JsonOptions);
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || string.IsNullOrWhiteSpace(RunId) || string.IsNullOrWhiteSpace(EngineProfile)
            || PublicationLag < TimeSpan.Zero || PublicationLag >= TimeSpan.FromMinutes(1)
            || MaximumWeightSnapshotAgeDays <= 0
            || DirectionalResearch is null || !DirectionalResearch.IsValid
            || (FromDate is not null && ToDate is not null && FromDate > ToDate)
            || Presets is null || Presets.Count == 0 || FeeSchedules is null
            || Presets.Any(preset => string.IsNullOrWhiteSpace(preset.Id) || preset.FullSpreadTicks < 0
                || preset.AdverseSlippageTicks < 0 || preset.GapStressRupees < 0m || preset.ExecutionDelay < TimeSpan.Zero
                || preset.PendingEntryTimeout <= preset.ExecutionDelay)
            || Presets.Select(preset => preset.Id).Distinct(StringComparer.Ordinal).Count() != Presets.Count
            || FeeSchedules.Any(schedule => !schedule.IsValid))
            throw new ArgumentException("Invalid replay identity, dates, lag, execution presets or fee schedules.");
        foreach (var group in FeeSchedules.GroupBy(schedule => schedule.ProfileId, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(schedule => schedule.EffectiveFromUtc).ToArray();
            for (var i = 1; i < ordered.Length; i++)
                if (ordered[i - 1].EffectiveToUtc is null || ordered[i - 1].EffectiveToUtc > ordered[i].EffectiveFromUtc)
                    throw new ArgumentException($"Overlapping fee schedules for {group.Key}.");
        }
    }
}

public sealed record DirectionalResearchConfiguration
{
    public string Version { get; init; } = "directional-labels-v1";
    public int HorizonMinutes { get; init; } = 5;
    public decimal BarrierPercent { get; init; } = 0.05m;
    public decimal FlatThresholdPercent { get; init; } = 0.01m;
    internal bool IsValid => !string.IsNullOrWhiteSpace(Version) && HorizonMinutes is > 0 and <= 120
        && BarrierPercent is > 0m and < 100m && FlatThresholdPercent is > 0m and < 100m;
}
