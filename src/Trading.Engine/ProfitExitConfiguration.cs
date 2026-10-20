using System.Text.Json.Serialization;

namespace Trading.Engine;

[JsonConverter(typeof(ProfitExitModeJsonConverter))]
public enum ProfitExitMode { Disabled, FixedNetTarget, TrailingNetProfit }

public sealed class ProfitExitModeJsonConverter : JsonStringEnumConverter<ProfitExitMode>
{
    public ProfitExitModeJsonConverter() : base(allowIntegerValues: false) { }
}

public sealed record ProfitExitConfiguration
{
    public string Version { get; init; } = "profit-exit-v1";
    public ProfitExitMode Mode { get; init; } = ProfitExitMode.Disabled;
    public decimal NetTarget { get; init; } = 500m;
    public decimal TrailActivation { get; init; } = 500m;
    public decimal TrailGiveback { get; init; } = 200m;
    public decimal MinimumLockedNetProfit { get; init; } = 300m;

    public IReadOnlyList<string> Validate() => !string.IsNullOrWhiteSpace(Version) && Enum.IsDefined(Mode)
        && NetTarget > 0m && TrailActivation > 0m && TrailGiveback > 0m
        && MinimumLockedNetProfit >= 0m && MinimumLockedNetProfit < TrailActivation
        ? [] : ["Profit exit requires a version, supported mode, positive target/activation/giveback and a nonnegative floor below activation."];
}
