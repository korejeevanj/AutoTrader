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
    public string Version { get; init; } = "profit-exit-v2";
    public ProfitExitMode Mode { get; init; } = ProfitExitMode.TrailingNetProfit;
    public decimal NetTarget { get; init; } = 500m;
    public decimal TrailActivation { get; init; } = 500m;
    public decimal TrailGiveback { get; init; } = 200m;
    public decimal MinimumLockedNetProfit { get; init; } = 300m;
    /// <summary>Peak net P&amp;L at or above which <see cref="WideTrailGiveback"/> replaces <see cref="TrailGiveback"/>; 0 keeps one flat giveback.</summary>
    public decimal WideTrailPeak { get; init; } = 1000m;
    public decimal WideTrailGiveback { get; init; } = 400m;

    /// <summary>Giveback = this multiple of the recent average one-bar move in net P&amp;L (never below <see cref="TrailGiveback"/>); 0 keeps the fixed/tiered giveback.</summary>
    public decimal AdaptiveGivebackMultiplier { get; init; } = 1.5m;
    public decimal MaximumGiveback { get; init; } = 600m;
    public int VolatilityLookbackBars { get; init; } = 10;
    public int VolatilityWarmupBars { get; init; } = 3;
    /// <summary>While the VWAP/breadth thesis is intact the giveback is multiplied by this (capped at <see cref="MaximumGiveback"/>); 1 disables.</summary>
    public decimal ThesisIntactGivebackFactor { get; init; } = 1.5m;

    /// <summary>Giveback as a percentage of the peak net P&amp;L (never below <see cref="TrailGiveback"/>); 0 disables. Ignored while the volatility-scaled giveback is active.</summary>
    public decimal TrailGivebackPercent { get; init; }

    public decimal GivebackAtPeak(decimal peak) => TrailGivebackPercent > 0m
        ? Math.Max(TrailGiveback, peak * TrailGivebackPercent / 100m)
        : WideTrailPeak > 0m && peak >= WideTrailPeak ? WideTrailGiveback : TrailGiveback;

    public decimal GivebackFor(decimal peak, decimal? averageBarMove, bool thesisIntact)
    {
        var giveback = AdaptiveGivebackMultiplier > 0m && averageBarMove is { } move
            ? Math.Max(TrailGiveback, AdaptiveGivebackMultiplier * move) : GivebackAtPeak(peak);
        if (thesisIntact) giveback *= ThesisIntactGivebackFactor;
        return Math.Min(giveback, MaximumGiveback);
    }

    public IReadOnlyList<string> Validate() => !string.IsNullOrWhiteSpace(Version) && Enum.IsDefined(Mode)
        && NetTarget > 0m && TrailActivation > 0m && TrailGiveback > 0m
        && MinimumLockedNetProfit >= 0m && MinimumLockedNetProfit < TrailActivation
        && (WideTrailPeak == 0m ? WideTrailGiveback == 0m : WideTrailPeak > TrailActivation && WideTrailGiveback > TrailGiveback)
        && TrailGivebackPercent is >= 0m and < 100m && AdaptiveGivebackMultiplier >= 0m && ThesisIntactGivebackFactor >= 1m
        && MaximumGiveback >= Math.Max(TrailGiveback, WideTrailGiveback)
        && VolatilityLookbackBars >= 2 && VolatilityWarmupBars >= 1 && VolatilityWarmupBars <= VolatilityLookbackBars
        ? [] : ["Profit exit requires a version, supported mode, positive target/activation/giveback, a nonnegative floor below activation, either no wide tier or a wide-tier peak above activation with a larger giveback, a maximum giveback covering the configured givebacks, a giveback percentage below 100, a thesis factor of at least 1, a nonnegative adaptive multiplier, and a volatility warm-up within its lookback."];
}
