using System.Text.Json;
using Trading.Domain;
using Trading.Engine;
using Trading.Replay;
using Trading.Tests.Replay;

// Reuses the existing importer fixture and real ReplayRunner; no new execution model.
// All scenarios and comparison parameters are specified before any evaluation.
if (args.Contains("--audit-loss", StringComparer.Ordinal)) { LossAudit.Run(args); return; }
using var fixture = new ReplayFixture(new(2026, 10, 9), new(2026, 10, 12), new(2026, 10, 13));
var loaded = fixture.Load();
// One extra declared opening after the 09:35 exit lets delayed exits actually execute.
var original = loaded with { Manifest = loaded.Manifest with { MarketClose = new(9, 37) },
    Candles = loaded.Candles.Concat(loaded.Candles.Where(bar => bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay == new TimeSpan(9, 35, 0))
        .Select(bar => bar with { StartUtc = bar.StartUtc.AddMinutes(1), EndUtc = bar.EndUtc.AddMinutes(1), AvailableAtUtc = bar.AvailableAtUtc.AddMinutes(1) })).ToArray() };
var sourceCall = original.Manifest.Instruments.Single(instrument => instrument.Id == "call");
var calls = Enumerable.Range(0, 35).Select(i => sourceCall with
{
    Id = $"call-{i:D2}", Strike = 54900m + i * 10m, InstrumentToken = 3000000 + i, File = $"synthetic-call-{i:D2}.json"
}).ToArray();
var data = original with
{
    Manifest = original.Manifest with
    {
        DatasetId = "frozen-synthetic-winner-loser-no-setup-v1",
        UniverseVersion = "frozen-synthetic-35-calls-and-one-put-v1",
        Instruments = original.Manifest.Instruments.Where(instrument => instrument.Id != "call").Concat(calls).ToArray()
    },
    Candles = original.Candles.Where(bar => bar.InstrumentId != "call").Concat(
        calls.SelectMany(contract => original.Candles.Where(bar => bar.InstrumentId == "call")
            .Select(bar => bar with { InstrumentId = contract.Id })))
        .Select(bar =>
        {
            var local = bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5));
            var day = DateOnly.FromDateTime(local.DateTime);
            var minute = local.Hour * 60 + local.Minute;
            // Third session has no breakout: zero trades must remain a valid result.
            if (day == new DateOnly(2026, 10, 13) && bar.InstrumentId == "future") return bar with { Close = 55000m };
            if (!bar.InstrumentId.StartsWith("call-", StringComparison.Ordinal)) return bar;
            if (day == new DateOnly(2026, 10, 9))
            {
                var price = minute == 572 ? 224m : minute == 573 ? 228m : minute == 574 ? 220m : minute == 575 ? 219m : 200m;
                return bar with { Open = price, Close = price, High = price + 1m, Low = price - 1m };
            }
            return day == new DateOnly(2026, 10, 12) && minute == 572
                ? bar with { Open = 190m, High = 201m, Low = 189m, Close = 190m } : bar;
        }).ToArray()
};
var presets = new CandleExecutionPreset[]
{
    new(), new() { Id = "worse-spread-slippage", FullSpreadTicks = 40, AdverseSlippageTicks = 10 },
    new() { Id = "one-second-delay", ExecutionDelay = TimeSpan.FromSeconds(1) }
};
var replay = fixture.Configuration with { RunId = "frozen-synthetic-decision-comparison-v1", Presets = presets };
var outputIndex = Array.IndexOf(args, "--out");
var output = outputIndex >= 0 ? args[outputIndex + 1] : null;
if (args.Contains("--compare-breadth", StringComparer.Ordinal))
{
    // Eleven is deliberately above this fixture's ten confirming members; not a market recommendation.
    var comparison = ReplayRuleComparison.Run(data, replay, fixture.Engine, minimumConfirmingConstituents: 11);
    if (output is not null) ReplayReportWriter.WriteComparison(output, comparison);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        evidenceClass = "SYNTHETIC_RULE_COMPARISON_NOT_PROFITABILITY_EVIDENCE",
        thresholdMeaning = "Fixture-only count 11 tests rejection of ten confirming members; no empirical threshold selection",
        comparison.Sessions
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
var rows = new List<object>();
foreach (var universe in new[] { "atm-only", "wider-35" })
foreach (var mode in new[] { ProfitExitMode.Disabled, ProfitExitMode.FixedNetTarget, ProfitExitMode.TrailingNetProfit })
{
    var allowed = universe == "atm-only" ? new HashSet<string>(["call-12"], StringComparer.Ordinal)
        : calls.Select(contract => contract.Id).ToHashSet(StringComparer.Ordinal);
    var selected = data with
    {
        Manifest = data.Manifest with { UniverseVersion = universe + ":frozen-v1",
            Instruments = data.Manifest.Instruments.Where(instrument => !instrument.Id.StartsWith("call-", StringComparison.Ordinal) || allowed.Contains(instrument.Id)).ToArray() },
        Candles = data.Candles.Where(bar => !bar.InstrumentId.StartsWith("call-", StringComparison.Ordinal) || allowed.Contains(bar.InstrumentId)).ToArray()
    };
    var profile = fixture.Engine.Strategies[0];
    var engine = fixture.Engine with { Version = $"synthetic-comparison-v1:{mode}", Strategies = [profile with
        { Position = profile.Position with { ProfitExit = new() { Mode = mode } } }] };
    var result = new ReplayRunner(selected, replay, engine).RunToEnd();
    if (output is not null) ReplayReportWriter.Write(Path.Combine(output, universe + "-" + mode), result);
    foreach (var group in result.Sessions.GroupBy(session => session.PresetId))
    {
        var complete = group.Where(session => session.Complete).ToArray();
        var trades = complete.SelectMany(session => session.Trades).Where(trade => trade.NetPnl is not null).ToArray();
        var wins = trades.Where(trade => trade.NetPnl > 0m).ToArray();
        var losses = trades.Where(trade => trade.NetPnl < 0m).ToArray();
        rows.Add(new
        {
            universe, profitMode = mode.ToString(), preset = group.Key,
            completeSessions = complete.Length, incompleteSessions = group.Count() - complete.Length,
            pendingExits = group.Sum(session => session.PendingExits),
            diagnosticPaidFeesAllSessions = group.Sum(session => session.PaidFees),
            diagnosticRealizedNetAllSessions = group.Sum(session => session.RealizedNetPnl),
            completionIssues = group.SelectMany(session => session.CompletionIssues).Distinct().ToArray(),
            noTradeSessions = complete.Count(session => session.Trades.Count == 0), closedTrades = trades.Length,
            net = complete.Sum(session => session.RealizedNetPnl), charges = complete.Sum(session => session.PaidFees),
            netExpectancy = trades.Length == 0 ? (decimal?)null : trades.Average(trade => trade.NetPnl!.Value),
            averageWin = wins.Length == 0 ? (decimal?)null : wins.Average(trade => trade.NetPnl!.Value),
            averageLoss = losses.Length == 0 ? (decimal?)null : losses.Average(trade => trade.NetPnl!.Value),
            winRatePercent = trades.Length == 0 ? (decimal?)null : wins.Length * 100m / trades.Length,
            maximumSessionDrawdown = complete.Select(session => session.ObservedNetDrawdown).DefaultIfEmpty().Max(),
            maximumGrossStopOverrun = trades.Select(trade => trade.GrossStopOverrun ?? 0m).DefaultIfEmpty().Max(),
            net500Attained = trades.Count(trade => trade.NetPnl >= 500m),
            selectedContracts = trades.Select(trade => trade.ContractId).Distinct().ToArray()
        });
    }
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    evidenceClass = "SYNTHETIC_CANDLE_FIXTURE_COMPARISON_NOT_PROFITABILITY_EVIDENCE",
    frozenBeforeEvaluation = true, chronologicalUntouchedEvaluation = "NOT_AVAILABLE: constructed software fixtures, no empirical holdout",
    constituentAblation = "NOT_EVALUATED: existing policy requires constituents; removing that hard gate is not an approved comparison policy",
    universeLimitation = "Cloned identical synthetic option paths test selection mechanics only; no measured strike-selection edge",
    results = rows
}, new JsonSerializerOptions { WriteIndented = true }));
