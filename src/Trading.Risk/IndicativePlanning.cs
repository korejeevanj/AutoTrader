namespace Trading.Risk;

public static class IndicativePlanning
{
    public static int WholeLotUnits(int lots, int lotSize)
    {
        if (lots <= 0 || lotSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lots), "Lots and lot size must be positive whole numbers.");
        }
        return checked(lots * lotSize);
    }

    public static decimal? LongGrossPnl(decimal entryPrice, decimal? currentBid, int units) =>
        currentBid is { } bid ? (bid - entryPrice) * units : null;

    // The observed bid/ask already includes spread. Apply only adverse slippage and tick rounding.
    public static decimal? ExecutablePrice(decimal? sidePrice, decimal tickSize, decimal adverseSlippage, bool buy)
    {
        if (sidePrice is not > 0m || tickSize <= 0m || adverseSlippage < 0m) return null;
        try
        {
            var price = buy ? sidePrice.Value + adverseSlippage : sidePrice.Value - adverseSlippage;
            var rounded = (buy ? decimal.Ceiling(price / tickSize) : decimal.Floor(price / tickSize)) * tickSize;
            return rounded > 0m ? rounded : null;
        }
        catch (OverflowException) { return null; }
    }

    public static QuoteCostFloor? LongQuoteCostFloor(decimal? bid, decimal? ask, decimal tickSize,
        decimal entrySlippage, decimal exitSlippage, int units, decimal roundTripFees, decimal? underlying, decimal strike)
    {
        if (bid is not > 0m || ask is not > 0m || bid > ask || units <= 0 || roundTripFees < 0m
            || underlying is not > 0m || strike <= 0m) return null;
        var entry = ExecutablePrice(ask, tickSize, entrySlippage, buy: true);
        var exit = ExecutablePrice(bid, tickSize, exitSlippage, buy: false);
        if (entry is null || exit is null) return null;
        try
        {
            return new(entry.Value, entry.Value * units, (ask.Value - bid.Value) / ask.Value * 100m,
                -LongGrossPnl(entry.Value, exit, units)!.Value + roundTripFees, Math.Abs(strike - underlying.Value));
        }
        catch (OverflowException) { return null; }
    }

    // Separate long purchases cannot offset each other's purchase debit as though they were hedged margin.
    public static decimal? LongPremiumCash(IEnumerable<(decimal EntryPrice, int Units)> purchases)
    {
        try
        {
            decimal total = 0m;
            foreach (var (price, units) in purchases)
            {
                if (price <= 0m || units <= 0) return null;
                total += price * units;
            }
            return total;
        }
        catch (OverflowException) { return null; }
    }

    // Exact decimal reconciliation: supplied monetary evidence uses the same already-rounded fees.
    // Invalid/unrepresentable costs remain unavailable rather than becoming a usable net mark.
    public static decimal? NetLiquidationPnl(decimal grossPnl, decimal entryFees, decimal exitFees)
    {
        if (entryFees < 0m || exitFees < 0m) return null;
        try { return grossPnl - entryFees - exitFees; }
        catch (OverflowException) { return null; }
    }

    public static decimal? UnderlyingProgressR(decimal entryReference, decimal currentPrice,
        decimal initialRisk, bool bullish)
    {
        if (entryReference <= 0m || currentPrice <= 0m || initialRisk <= 0m) return null;
        try { return (bullish ? currentPrice - entryReference : entryReference - currentPrice) / initialRisk; }
        catch (OverflowException) { return null; }
    }
}

public sealed record QuoteCostFloor(decimal EntryPrice, decimal PremiumCash, decimal SpreadPercent,
    decimal ImmediateLiquidationLossWithFees, decimal AbsoluteStrikeDistance);
