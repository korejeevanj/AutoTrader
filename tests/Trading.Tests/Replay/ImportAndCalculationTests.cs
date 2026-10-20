using System.Text;
using Trading.Analytics;
using Trading.Data;
using Trading.Domain;
using Trading.Risk;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class ImportAndCalculationTests
{
    [Fact]
    public void Kite_offset_optional_oi_and_zero_volume_are_preserved_without_inventing_fields()
    {
        var json = "{\"status\":\"success\",\"data\":{\"candles\":[[\"2026-10-09T09:15:00+0530\",54746.25,54852.5,54617.2,54808.05,0,0],[\"2026-10-09T09:16:00+0530\",54808.85,54866.8,54807.95,54826.5,0]]}}";
        var imported = KiteCandleImporter.Parse(Encoding.UTF8.GetBytes(json), "index", TimeSpan.FromSeconds(1));
        Assert.Empty(imported.Issues);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 3, 45, 0, TimeSpan.Zero), imported.Candles[0].StartUtc);
        Assert.Equal(imported.Candles[0].EndUtc.AddSeconds(1), imported.Candles[0].AvailableAtUtc);
        Assert.Equal(0m, imported.Candles[0].Volume);
        Assert.Equal(0m, imported.Candles[0].OpenInterest);
        Assert.Null(imported.Candles[1].OpenInterest);
        var features = HistoricalSessionAnalytics.Calculate(imported.Candles, imported.Candles[0].StartUtc, imported.Candles[^1].AvailableAtUtc, 1);
        Assert.All(features, feature => Assert.Null(feature.Vwap));
    }

    [Fact]
    public void Identical_duplicates_collapse_conflicts_quarantine_and_bad_rows_are_reported()
    {
        var json = "[[\"2026-10-09T09:15:00+0530\",100,101,99,100,5],[\"2026-10-09T09:15:00+0530\",100,101,99,100,5],[\"2026-10-09T09:16:00+0530\",100,101,99,100,5],[\"2026-10-09T09:16:00+0530\",100,101,99,101,5],[\"2026-10-09T09:17:00+0530\",100,90,99,100,-1],[\"2026-10-09T09:18:00\",100,101,99,100,5]]";
        var imported = KiteCandleImporter.Parse(Encoding.UTF8.GetBytes(json), "future", TimeSpan.Zero);
        Assert.Single(imported.Candles);
        Assert.Equal(1, imported.Provenance.IdenticalDuplicates);
        Assert.Contains(imported.Issues, issue => issue.Code == "CONFLICTING_DUPLICATES");
        Assert.Equal(2, imported.Issues.Count(issue => issue.Code == "INVALID_ROW"));
    }

    [Fact]
    public void Manifest_rejects_duplicate_tokens_and_source_aliases_and_reports_missing_files()
    {
        using var fixture = new ReplayFixture();
        var manifest = fixture.ReadManifest();
        fixture.WriteManifest(manifest with { Instruments = manifest.Instruments.Select((item, i) => i == 1 ? item with { InstrumentToken = manifest.Instruments[0].InstrumentToken } : item).ToArray() });
        Assert.Throws<InvalidDataException>(() => fixture.Load());
        fixture.WriteManifest(manifest with { Instruments = manifest.Instruments.Select((item, i) => i == 1 ? item with { File = manifest.Instruments[0].File } : item).ToArray() });
        Assert.Throws<InvalidDataException>(() => fixture.Load());
        fixture.WriteManifest(manifest);
        File.Delete(Path.Combine(fixture.Root, "bank-00.json"));
        Assert.Contains(fixture.Load().Issues, issue => issue.Code == "FILE_MISSING");
    }

    [Fact]
    public void Shared_features_are_unrounded_session_scoped_and_use_preceding_true_ranges()
    {
        var start = new DateTimeOffset(2026, 10, 9, 3, 45, 0, TimeSpan.Zero);
        HistoricalCandle Candle(int i, decimal high, decimal low, decimal close, decimal volume) =>
            new("future", start.AddMinutes(i), start.AddMinutes(i + 1), start.AddMinutes(i + 1), close, high, low, close, volume, null);
        var bars = new[] { Candle(0, 103m, 99m, 101m, 2m), Candle(1, 107m, 101m, 104m, 1m), Candle(2, 112m, 102m, 106m, 1m) };
        var output = HistoricalSessionAnalytics.Calculate(bars, start, start.AddMinutes(3), 2);
        Assert.Equal(102m, output[1].Vwap!.Value);
        Assert.InRange(output[2].Vwap!.Value, 103.16666m, 103.16667m); // not chart-rounded to 103.17
        Assert.Equal(5m, output[2].PreviousMeanTrueRange); // preceding 4 and 6; signal range 10 excluded
        var structural = HistoricalResearchCalculations.Structural(102m, 112m, 106m, 5m, 0.1m);
        Assert.Equal(101.5m, structural.LongStop);
        Assert.Equal(4.5m, structural.LongRisk);
        var gap = HistoricalSessionAnalytics.Calculate([bars[0], bars[2]], start, start.AddMinutes(3), 1);
        Assert.False(gap[1].CompleteCoverage);
        Assert.Null(gap[1].Vwap);
    }

    [Fact]
    public void Rounded_model_stop_and_immediate_spread_loss_are_included_in_risk()
    {
        var schedule = new ResearchFeeSchedule { Version = "test", Source = "test", IsIllustrative = true };
        var plan = HistoricalResearchCalculations.Plan(100m, 100m, 100m, 75, 0.05m, 0, 2, 200m, 0m, schedule)!;
        var stop = HistoricalResearchCalculations.StopFill(plan.FillPrice, 200m, 75, 100m, 0.05m, 0, 2);
        Assert.Equal(-IndicativePlanning.LongGrossPnl(plan.FillPrice, stop, 75)!.Value, plan.ModelledNetRisk);
        var tight = HistoricalResearchCalculations.Plan(100m, 100m, 100m, 75, 0.05m, 40, 2, 1m, 0m, schedule)!;
        Assert.True(tight.ModelledNetRisk > 150m);
        Assert.Equal(100.15m, HistoricalResearchCalculations.Fill(100m, 0.05m, 3, 1, true));
        Assert.Equal(99.85m, HistoricalResearchCalculations.Fill(100m, 0.05m, 3, 1, false));
    }

    [Fact]
    public void Fee_components_and_cash_ledger_reconcile_without_double_spread()
    {
        var schedule = new ResearchFeeSchedule { Version = "algebra", Source = "synthetic", BrokeragePerOrder = 2m,
            ExchangeTurnoverRate = 0.001m, RegulatoryTurnoverRate = 0.0001m, GstRate = 0.18m,
            BuyStampDutyRate = 0.0001m, SellTransactionTaxRate = 0.001m };
        var buy = HistoricalResearchCalculations.Fees(200m, 25, true, schedule);
        Assert.Equal(2m, buy.Brokerage); Assert.Equal(5m, buy.Exchange); Assert.Equal(0.5m, buy.Regulatory);
        Assert.Equal(0.5m, buy.StampDuty); Assert.Equal(1.35m, buy.Gst); Assert.Equal(9.35m, buy.Total);
        var sell = HistoricalResearchCalculations.Fees(210m, 25, false, schedule);
        var ledger = new ResearchCashLedger(500000m, 500000m);
        ledger = HistoricalResearchCalculations.Enter(ledger, 200m, 25, buy.Total);
        ledger = HistoricalResearchCalculations.Exit(ledger, 200m, 210m, 25, buy.Total, sell.Total);
        Assert.Equal(ledger.InitialCapital + ledger.RealizedNetPnl, ledger.Cash);
        Assert.Equal(buy.Total + sell.Total, ledger.PaidFees);
    }

    [Fact]
    public void Ambiguous_intrabar_fee_regimes_use_the_conservative_cost_and_gaps_remain_unknown()
    {
        var start = new DateTimeOffset(2026, 10, 9, 4, 1, 0, TimeSpan.Zero);
        var old = new ResearchFeeSchedule { ProfileId = "paper-fees", Version = "old", Source = "synthetic",
            IsIllustrative = true, EffectiveToUtc = start.AddSeconds(30), BrokeragePerOrder = 2m };
        var next = old with { Version = "next", EffectiveFromUtc = start.AddSeconds(30), EffectiveToUtc = null, BrokeragePerOrder = 10m };
        var interval = HistoricalResearchCalculations.ResolveExitFees([old, next], "paper-fees", 200m, 25, start, start.AddMinutes(1), start.AddMinutes(1), true)!.Value;
        Assert.Equal(10m, interval.Fees.Total);
        Assert.Equal("INTERVAL_CONSERVATIVE_MAX_FEES", interval.Attribution);
        var gap = next with { EffectiveFromUtc = start.AddSeconds(40) };
        Assert.Null(HistoricalResearchCalculations.ResolveExitFees([old, gap], "paper-fees", 200m, 25, start, start.AddMinutes(1), start.AddMinutes(1), true));
    }
}
