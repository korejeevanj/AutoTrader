using Trading.Domain;

namespace Trading.Analytics;

public sealed record DirectionalPriceMetrics(decimal? CloseMinusOpeningHigh, decimal? CloseMinusOpeningLow,
    decimal? CloseMinusVwap, decimal? Return1MinutePercent, decimal? Return5MinutePercent);
public sealed record ConstituentBreadth(int ConfirmingCount, int OpposingCount, int NeutralCount,
    decimal TopThreeConfirmingWeightPercent);

public static class DirectionalAnalytics
{
    public const string Version = "directional-context-v1";

    public static DirectionalPriceMetrics Prices(decimal close, decimal? openingHigh, decimal? openingLow,
        decimal? vwap, decimal? close1MinuteAgo, decimal? close5MinutesAgo) => new(
            close - openingHigh, close - openingLow, close - vwap,
            ReturnPercent(close, close1MinuteAgo), ReturnPercent(close, close5MinutesAgo));

    public static decimal? ReturnPercent(decimal close, decimal? reference) =>
        reference is > 0m ? (close - reference.Value) / reference.Value * 100m : null;

    // Inputs are canonical same-period rows; missing rows never count as neutral or confirming.
    public static ConstituentBreadth Breadth(IReadOnlyList<ConstituentRow> rows, bool bullish)
    {
        var usable = rows.Where(row => row.State == "fresh" && row.WeightPercent is > 0m
            && row.LastPrice is > 0m && row.Vwap is > 0m).ToArray();
        var confirming = usable.Where(row => bullish ? row.LastPrice > row.Vwap : row.LastPrice < row.Vwap).ToArray();
        return new(confirming.Length,
            usable.Count(row => bullish ? row.LastPrice < row.Vwap : row.LastPrice > row.Vwap),
            usable.Count(row => row.LastPrice == row.Vwap),
            confirming.OrderByDescending(row => row.WeightPercent).Take(3).Sum(row => row.WeightPercent!.Value));
    }
}
