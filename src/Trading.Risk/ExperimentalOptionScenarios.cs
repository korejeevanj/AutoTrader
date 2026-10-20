namespace Trading.Risk;

public sealed record OptionScenarioPoint(decimal UnderlyingR, double VolatilityFactor, double HorizonSeconds,
    decimal ModelExitPrice, decimal NetPnl);
public sealed record OptionScenarioInputs(decimal Forward, decimal Strike, DateTimeOffset ExpiryUtc, DateTimeOffset AsOfUtc,
    decimal Bid, decimal Ask, decimal Tick, int Units, bool Call, decimal UnderlyingRisk, decimal? StructuralStop, string FeeScheduleVersion);
public sealed record OptionScenarioAssessment(string Version, string EvidenceClass, double CalibratedIv,
    decimal EntryPrice, decimal EntryFees, decimal RoundTripFees, decimal RequiredCash, decimal NetRisk,
    decimal FavorableNetProfit, decimal SpreadPercent, decimal Friction, decimal StrikeDistance,
    IReadOnlyList<OptionScenarioPoint> Scenarios, OptionScenarioInputs Inputs);

// Explicit experimental assumptions, not exchange quotes/Greeks or empirically calibrated loss bounds.
public static class ExperimentalOptionScenarios
{
    public const string Version = "experimental-black76-zero-rate-sticky-strike-v1";
    private const double SecondsPerYear = 365d * 24 * 60 * 60;

    public static decimal Price(decimal forward, decimal strike, double years, double volatility, bool call)
    {
        if (forward <= 0m || strike <= 0m || !double.IsFinite(years) || !double.IsFinite(volatility) || years < 0 || volatility <= 0)
            throw new ArgumentException("Invalid option model inputs.");
        if (years == 0) return Math.Max(0m, call ? forward - strike : strike - forward);
        var scale = volatility * Math.Sqrt(years);
        var d1 = (Math.Log((double)(forward / strike)) + scale * scale / 2) / scale;
        var d2 = d1 - scale;
        var value = call ? (double)forward * Phi(d1) - (double)strike * Phi(d2)
            : (double)strike * Phi(-d2) - (double)forward * Phi(-d1);
        return (decimal)Math.Max(0d, value);
    }

    private static double Phi(double x)
    {
        var t = 1 / (1 + .2316419 * Math.Abs(x));
        var tail = .3989422804014327 * Math.Exp(-x * x / 2) * t *
            (.319381530 + t * (-.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        return x >= 0 ? 1 - tail : tail;
    }

    public static double? ImpliedVolatility(decimal forward, decimal strike, double years, decimal observed, bool call = true)
    {
        if (forward <= 0 || strike <= 0 || years <= 0 || !double.IsFinite(years) || observed <= 0) return null;
        double low = .01, high = 3;
        if (observed < Price(forward, strike, years, low, call) || observed > Price(forward, strike, years, high, call)) return null;
        for (var i = 0; i < 80; i++)
        {
            var middle = (low + high) / 2;
            if (Price(forward, strike, years, middle, call) < observed) low = middle; else high = middle;
        }
        return (low + high) / 2;
    }

    public static OptionScenarioAssessment? Assess(decimal forward, decimal strike, DateTimeOffset expiry,
        DateTimeOffset now, decimal bid, decimal ask, decimal tick, int units, bool call,
        decimal underlyingRisk, decimal grossStop, ResearchFeeSchedule fees, decimal? structuralStop = null)
    {
        if (bid <= 0 || ask < bid || tick <= 0 || units <= 0 || underlyingRisk <= 0 || grossStop <= 0
            || !fees.IsValid || now >= expiry || structuralStop is <= 0
            || structuralStop is { } invalidation && (call ? invalidation >= forward : invalidation <= forward)) return null;
        var years = (expiry - now).TotalSeconds / SecondsPerYear;
        var iv = ImpliedVolatility(forward, strike, years, (bid + ask) / 2, call);
        if (iv is null) return null;
        var entry = IndicativePlanning.ExecutablePrice(ask, tick, tick, true);
        var liquidation = IndicativePlanning.ExecutablePrice(bid, tick, tick, false);
        if (entry is null || liquidation is null) return null;
        var entryFees = HistoricalResearchCalculations.Fees(entry.Value, units, true, fees).Total;
        var roundFees = entryFees + HistoricalResearchCalculations.Fees(liquidation.Value, units, false, fees).Total;
        var floor = IndicativePlanning.LongQuoteCostFloor(bid, ask, tick, tick, tick, units, roundFees, forward, strike)!;
        var points = new List<OptionScenarioPoint>();
        var moves = new List<decimal> { -2m, -1m, 0m, 1m };
        // Always include the actual frozen invalidation, even after the current
        // future has moved more than two signal-R away from it.
        if (structuralStop is { } stop) moves.Add((call ? stop - forward : forward - stop) / underlyingRisk);
        foreach (var horizon in new[] { .25, 1d, 1200d })
        foreach (var move in moves.Distinct())
        foreach (var factor in new[] { .8, 1d, 1.2 })
        {
            var scenarioForward = forward + (call ? 1m : -1m) * move * underlyingRisk;
            if (scenarioForward <= 0) return null;
            var model = Price(scenarioForward, strike, Math.Max(0, years - horizon / SecondsPerYear), iv.Value * factor, call);
            // A MODEL_ESTIMATE sell-side haircut. Never inserted into observed market evidence.
            var exit = Math.Max(0m, decimal.Floor((model - (ask - bid) / 2 - tick) / tick) * tick);
            // Zero modeled recovery is possible. A minimum statutory fee basis is
            // conservative; it does not invent positive liquidation proceeds.
            var exitFees = HistoricalResearchCalculations.Fees(Math.Max(tick, exit), units, false, fees).Total;
            points.Add(new(move, factor, horizon, exit, HistoricalResearchCalculations.Net(entry.Value, exit, units, entryFees, exitFees)));
        }
        var risk = Math.Max(floor.ImmediateLiquidationLossWithFees, points.Where(point => point.UnderlyingR <= 0).Max(point => -point.NetPnl));
        var stopExit = Math.Max(tick, decimal.Floor((entry.Value - grossStop / units - tick) / tick) * tick);
        risk = Math.Max(risk, -HistoricalResearchCalculations.Net(entry.Value, stopExit, units, entryFees,
            HistoricalResearchCalculations.Fees(stopExit, units, false, fees).Total));
        return new(Version, "MODEL_ESTIMATE", iv.Value, entry.Value, entryFees, roundFees,
            entry.Value * units + entryFees, risk,
            points.Where(point => point.UnderlyingR == 1m && point.HorizonSeconds == 1200).Min(point => point.NetPnl),
            floor.SpreadPercent, floor.ImmediateLiquidationLossWithFees, floor.AbsoluteStrikeDistance, points.AsReadOnly(),
            new(forward, strike, expiry, now, bid, ask, tick, units, call, underlyingRisk, structuralStop, fees.Version));
    }
}
