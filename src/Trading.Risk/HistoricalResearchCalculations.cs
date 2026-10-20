using System.Text.Json.Serialization;

namespace Trading.Risk;

public sealed record ResearchFeeSchedule
{
    public string ProfileId { get; init; } = "paper-fees";
    public string Version { get; init; } = "";
    public string Source { get; init; } = "";
    public bool IsIllustrative { get; init; }
    public string EvidenceClass { get; init; } = "DatedSchedule";
    public bool RoundStatutoryChargesToRupee { get; init; }
    [JsonRequired] public DateTimeOffset AvailableAtUtc { get; init; }
    [JsonRequired] public DateTimeOffset EffectiveFromUtc { get; init; }
    public DateTimeOffset? EffectiveToUtc { get; init; }
    [JsonRequired] public decimal BrokeragePerOrder { get; init; }
    [JsonRequired] public decimal ExchangeTurnoverRate { get; init; }
    [JsonRequired] public decimal RegulatoryTurnoverRate { get; init; }
    [JsonRequired] public decimal SellTransactionTaxRate { get; init; }
    [JsonRequired] public decimal BuyStampDutyRate { get; init; }
    [JsonRequired] public decimal GstRate { get; init; }

    public bool IsValid => !string.IsNullOrWhiteSpace(ProfileId) && !string.IsNullOrWhiteSpace(Version)
        && !string.IsNullOrWhiteSpace(Source) && BrokeragePerOrder >= 0m
        && new[] { ExchangeTurnoverRate, RegulatoryTurnoverRate, SellTransactionTaxRate, BuyStampDutyRate, GstRate }.All(rate => rate >= 0m && rate <= 1m)
        && (EffectiveToUtc is null || EffectiveToUtc > EffectiveFromUtc);
    public bool Applies(DateTimeOffset time, string profileId, bool synthetic) => IsValid
        && ProfileId == profileId && AvailableAtUtc <= time && EffectiveFromUtc <= time
        && (EffectiveToUtc is null || time < EffectiveToUtc) && (synthetic || !IsIllustrative);
}

public sealed record ResearchFeeBreakdown(decimal Brokerage, decimal Exchange, decimal Regulatory,
    decimal TransactionTax, decimal StampDuty, decimal Gst, decimal Total, string ScheduleVersion);
public sealed record ResearchEntryPlan(decimal FillPrice, decimal RequiredCash, decimal ModelledNetRisk,
    decimal SpreadPercent, decimal RoundTripFriction, decimal StrikeDistance,
    ResearchFeeBreakdown EntryFees, ResearchFeeBreakdown StopExitFees);
public sealed record ResearchCashLedger(decimal InitialCapital, decimal Cash, decimal RealizedNetPnl = 0m,
    decimal PaidFees = 0m);

public static class HistoricalResearchCalculations
{
    public const string Version = "candle-research-risk-v1";
    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    public static ResearchFeeBreakdown Fees(decimal price, int units, bool buy, ResearchFeeSchedule schedule)
    {
        if (price <= 0m || units <= 0 || !schedule.IsValid) throw new ArgumentException("Positive price/quantity and a valid explicit fee schedule are required.");
        var turnover = price * units;
        var brokerage = Money(schedule.BrokeragePerOrder);
        var exchange = Money(turnover * schedule.ExchangeTurnoverRate);
        var regulatory = Money(turnover * schedule.RegulatoryTurnoverRate);
        decimal Statutory(decimal amount) => schedule.RoundStatutoryChargesToRupee
            ? decimal.Round(amount, 0, MidpointRounding.AwayFromZero) : Money(amount);
        var tax = buy ? 0m : Statutory(turnover * schedule.SellTransactionTaxRate);
        var stamp = buy ? Statutory(turnover * schedule.BuyStampDutyRate) : 0m;
        var gst = Money((brokerage + exchange + regulatory) * schedule.GstRate);
        return new(brokerage, exchange, regulatory, tax, stamp, gst,
            brokerage + exchange + regulatory + tax + stamp + gst, schedule.Version);
    }

    public static decimal Fill(decimal reference, decimal tick, int fullSpreadTicks, int slippageTicks, bool buy)
    {
        if (reference <= 0m || tick <= 0m || fullSpreadTicks < 0 || slippageTicks < 0) throw new ArgumentException("Invalid declared fill assumptions.");
        var friction = tick * (fullSpreadTicks / 2m + slippageTicks);
        var price = buy ? reference + friction : reference - friction;
        return (buy ? decimal.Ceiling(price / tick) : decimal.Floor(price / tick)) * tick;
    }
    public static decimal SellMark(decimal reference, decimal tick, int fullSpreadTicks) => Fill(reference, tick, fullSpreadTicks, 0, false);
    // Candle intervals are half-open. Booking time must not choose a later effective fee regime.
    public static (ResearchFeeBreakdown Fees, string Attribution)? ResolveExitFees(IEnumerable<ResearchFeeSchedule> source,
        string profileId, decimal price, int units, DateTimeOffset intervalStart, DateTimeOffset intervalEnd,
        DateTimeOffset knownAt, bool synthetic)
    {
        if (intervalEnd < intervalStart || intervalEnd > knownAt) throw new ArgumentException("Invalid execution interval.");
        var schedules = source.Where(schedule => schedule.IsValid && schedule.ProfileId == profileId
            && schedule.AvailableAtUtc <= knownAt && (synthetic || !schedule.IsIllustrative))
            .OrderBy(schedule => schedule.EffectiveFromUtc).ToArray();
        if (intervalStart == intervalEnd)
        {
            var point = schedules.SingleOrDefault(schedule => schedule.EffectiveFromUtc <= intervalStart
                && (schedule.EffectiveToUtc is null || intervalStart < schedule.EffectiveToUtc));
            return point is null ? null : (Fees(price, units, false, point), "EXECUTION_TIMESTAMP");
        }
        var overlaps = schedules.Where(schedule => schedule.EffectiveFromUtc < intervalEnd
            && (schedule.EffectiveToUtc is null || schedule.EffectiveToUtc > intervalStart)).ToArray();
        var covered = intervalStart;
        foreach (var schedule in overlaps)
        {
            if (schedule.EffectiveFromUtc > covered) return null;
            var end = schedule.EffectiveToUtc ?? intervalEnd;
            if (end > covered) covered = end;
        }
        if (covered < intervalEnd) return null;
        var worst = overlaps.Select(schedule => Fees(price, units, false, schedule))
            .OrderByDescending(fee => fee.Total).ThenBy(fee => fee.ScheduleVersion, StringComparer.Ordinal).First();
        return (worst, overlaps.Length == 1 ? "INTERVAL_SINGLE_SCHEDULE" : "INTERVAL_CONSERVATIVE_MAX_FEES");
    }
    public static decimal StopMark(decimal entry, decimal grossStop, int units) => entry - grossStop / units;
    public static decimal StopFill(decimal entry, decimal grossStop, int units, decimal optionOpen,
        decimal tick, int fullSpreadTicks, int slippageTicks)
    {
        var stop = StopMark(entry, grossStop, units);
        var openingMark = SellMark(optionOpen, tick, fullSpreadTicks);
        var referenceMark = Math.Min(stop, openingMark);
        return decimal.Floor((referenceMark - tick * slippageTicks) / tick) * tick;
    }

    public static ResearchEntryPlan? Plan(decimal reference, decimal strike, decimal underlying,
        int units, decimal tick, int fullSpreadTicks, int slippageTicks,
        decimal grossStop, decimal gapStress, ResearchFeeSchedule schedule)
    {
        var entry = Fill(reference, tick, fullSpreadTicks, slippageTicks, true);
        var stopFill = StopFill(entry, grossStop, units, reference, tick, fullSpreadTicks, slippageTicks);
        var immediateExit = Fill(reference, tick, fullSpreadTicks, slippageTicks, false);
        if (stopFill <= 0m || immediateExit <= 0m) return null;
        var entryFees = Fees(entry, units, true, schedule);
        var exitFees = Fees(stopFill, units, false, schedule);
        var modelRisk = -IndicativePlanning.LongGrossPnl(entry, stopFill, units)!.Value + entryFees.Total + exitFees.Total + gapStress;
        var friction = -IndicativePlanning.LongGrossPnl(entry, immediateExit, units)!.Value
            + entryFees.Total + Fees(immediateExit, units, false, schedule).Total;
        return new(entry, entry * units + entryFees.Total, modelRisk, tick * fullSpreadTicks / reference * 100m,
            friction, Math.Abs(strike - underlying), entryFees, exitFees);
    }

    public static (decimal LongStop, decimal LongRisk, decimal ShortStop, decimal ShortRisk) Structural(
        decimal low, decimal high, decimal close, decimal previousMeanTrueRange, decimal multiplier)
    {
        var buffer = previousMeanTrueRange * multiplier;
        var longStop = low - buffer; var shortStop = high + buffer;
        return (longStop, close - longStop, shortStop, shortStop - close);
    }
    public static decimal Progress(decimal reference, decimal price, decimal initialRisk, bool bullish) =>
        IndicativePlanning.UnderlyingProgressR(reference, price, initialRisk, bullish)
            ?? throw new ArgumentException("Positive, representable underlying prices and initial risk are required.");
    public static decimal Net(decimal entry, decimal exit, int units, decimal entryFees, decimal exitFees) =>
        IndicativePlanning.NetLiquidationPnl((exit - entry) * units, entryFees, exitFees)
            ?? throw new ArgumentException("Nonnegative, representable entry and exit costs are required.");
    public static decimal GrossStopOverrun(decimal grossPnl, decimal trigger) => Math.Max(0m, -grossPnl - trigger);
    public static decimal ProfitExitShortfall(decimal realizedNetPnl, decimal triggerThreshold) => Math.Max(0m, triggerThreshold - realizedNetPnl);
    public static decimal AvailableCash(ResearchCashLedger ledger, IEnumerable<decimal> reservations) => ledger.Cash - reservations.Sum();
    public static decimal ReservedCash(IEnumerable<decimal> reservations) => reservations.Sum();
    public static ResearchCashLedger Enter(ResearchCashLedger ledger, decimal price, int units, decimal fee) =>
        ledger with { Cash = ledger.Cash - price * units - fee, PaidFees = ledger.PaidFees + fee };
    public static ResearchCashLedger Exit(ResearchCashLedger ledger, decimal entry, decimal exit, int units,
        decimal entryFees, decimal exitFees) => ledger with
        { Cash = ledger.Cash + exit * units - exitFees, PaidFees = ledger.PaidFees + exitFees,
          RealizedNetPnl = ledger.RealizedNetPnl + Net(entry, exit, units, entryFees, exitFees) };
    public static decimal? SessionNet(ResearchCashLedger ledger, IEnumerable<decimal?> openNet)
    {
        var marks = openNet.ToArray();
        return marks.Any(mark => mark is null) ? null : ledger.RealizedNetPnl + marks.Sum(mark => mark!.Value);
    }
    public static decimal ObservedDrawdown(IEnumerable<decimal?> equity)
    {
        decimal? peak = null; decimal maximum = 0m;
        foreach (var mark in equity.Where(mark => mark is not null))
        { peak = peak is null ? mark : Math.Max(peak.Value, mark!.Value); maximum = Math.Max(maximum, peak!.Value - mark!.Value); }
        return maximum;
    }
}
