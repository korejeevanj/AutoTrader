using System.Text.Json;
using Trading.Domain;
using Trading.Engine;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class ReplayBehaviorTests
{
    [Fact]
    public void Full_fourteen_bank_replay_generates_hypothetical_trade_but_no_strict_enter()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        Assert.Empty(data.Issues);
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.True(result.Complete);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(new TimeOnly(9, 31), TimeOnly.FromDateTime(trade.EntryTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime));
        Assert.Equal(200.6m, trade.EntryPrice);
        Assert.Equal(199.4m, trade.ExitPrice);
        Assert.Equal(-30m, trade.GrossPnl);
        Assert.Equal(-34m, trade.NetPnl); // one spread/slippage crossing and fees exactly once
        Assert.Equal(4m, result.PaidFees);
        Assert.Equal(trade.NetPnl, result.RealizedNetPnl);
        Assert.All(result.Decisions.Where(row => row.Class == "STRICT_ENGINE"), row => Assert.NotEqual("ENTER", row.Action));
        Assert.Contains(result.Features, row => row.AboveWeight == 70m && row.MissingWeight == 0m && row.SetupId is not null);
    }

    [Fact]
    public void Stepping_pause_and_run_to_end_produce_identical_results()
    {
        using var fixture = new ReplayFixture();
        var runner = new ReplayRunner(fixture.Load(), fixture.Configuration, fixture.Engine);
        var automatic = Assert.Single(runner.RunToEnd().Sessions);
        var stepped = runner.CreateSession(ReplayFixture.Date, "base");
        stepped.Pause(); stepped.RunToEnd();
        Assert.Null(stepped.CurrentTimeUtc);
        for (var i = 0; i < 5; i++) Assert.NotNull(stepped.Step());
        stepped.Resume(); stepped.RunToEnd();
        Assert.Equal(JsonSerializer.Serialize(automatic), JsonSerializer.Serialize(stepped.Result));
        Assert.Null(stepped.Step());
    }

    [Fact]
    public void Future_high_low_close_volume_changes_cannot_influence_opening_time_inputs_or_fills()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        var opening = data.Candles.Single(bar => bar.InstrumentId == "call" && bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay == new TimeSpan(9, 31, 0));
        var altered = data with { Candles = data.Candles.Select(bar => bar.Id == opening.Id
            ? bar with { High = 300m, Low = 1m, Close = 250m, Volume = 90000m } : bar).ToArray() };
        var first = new ReplayRunner(data, fixture.Configuration, fixture.Engine).CreateSession(ReplayFixture.Date, "base");
        var second = new ReplayRunner(altered, fixture.Configuration, fixture.Engine).CreateSession(ReplayFixture.Date, "base");
        while (first.CurrentTimeUtc is null || first.CurrentTimeUtc < opening.StartUtc)
            Assert.Equal(JsonSerializer.Serialize(first.Step()), JsonSerializer.Serialize(second.Step()));
        Assert.Equal(JsonSerializer.Serialize(first.Result.Trades), JsonSerializer.Serialize(second.Result.Trades));
        Assert.Single(first.Result.Trades);
    }

    [Fact]
    public void Publication_lag_prevents_backdating_a_fill_to_an_open_before_the_decision()
    {
        using var fixture = new ReplayFixture();
        fixture.Configuration = fixture.Configuration with { PublicationLag = TimeSpan.FromSeconds(1) };
        var result = Assert.Single(new ReplayRunner(fixture.Load(), fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(new TimeOnly(9, 32), TimeOnly.FromDateTime(trade.EntryTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime));
    }

    [Fact]
    public void Intrabar_stop_is_known_at_completion_records_an_interval_and_reports_overrun()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        var bar = data.Candles.Single(bar => bar.InstrumentId == "call" && bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay == new TimeSpan(9, 31, 0));
        data = data with { Candles = data.Candles.Select(row => row.Id == bar.Id ? row with { Low = 190m } : row).ToArray() };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(bar.AvailableAtUtc, trade.ExitKnownAtUtc);
        Assert.Equal(bar.StartUtc, trade.ExitIntervalStartUtc);
        Assert.Equal(bar.EndUtc, trade.ExitIntervalEndUtc);
        Assert.Equal("GROSS_CASH_STOP_INTRABAR_MODEL", trade.ExitReason);
        Assert.Equal(-202.5m, trade.GrossPnl);
        Assert.Equal(-206.5m, trade.NetPnl);
        Assert.Equal(2.5m, trade.GrossStopOverrun);
    }

    [Fact]
    public void Gap_stop_uses_adverse_open_and_positive_delay_waits_for_a_later_open()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        var bar = data.Candles.Single(bar => bar.InstrumentId == "call" && bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay == new TimeSpan(9, 33, 0));
        data = data with { Candles = data.Candles.Select(row => row.Id == bar.Id ? row with
            { Open = 180m, High = 202m, Low = 179m } : row).ToArray() };
        var immediate = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions).Trades.Single();
        Assert.Equal(bar.StartUtc, immediate.ExitKnownAtUtc);
        Assert.Equal(179.4m, immediate.ExitPrice);
        var delayedConfig = fixture.Configuration with { Presets = [new() { ExecutionDelay = TimeSpan.FromSeconds(5) }] };
        var delayed = Assert.Single(new ReplayRunner(data, delayedConfig, fixture.Engine).RunToEnd().Sessions).Trades.Single();
        Assert.True(delayed.EntryTimeUtc < bar.StartUtc);
        Assert.True(delayed.ExitKnownAtUtc >= bar.StartUtc.AddSeconds(5));
        Assert.Equal("GROSS_CASH_STOP_PENDING", delayed.ExitReason);
    }

    [Fact]
    public void Missing_option_tail_leaves_session_exit_pending_without_a_fabricated_fill()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        var cutoff = data.Candles.Single(bar => bar.InstrumentId == "call" && bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay == new TimeSpan(9, 32, 0)).EndUtc;
        data = data with { Candles = data.Candles.Where(bar => bar.InstrumentId != "call" || bar.StartUtc < cutoff).ToArray() };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.False(result.Complete);
        Assert.Equal(1, result.PendingExits);
        Assert.Null(Assert.Single(result.Trades).ExitPrice);
        Assert.Null(result.Equity[^1].NetSessionPnl);
    }

    [Fact]
    public void Missing_fees_or_constituents_and_future_constituent_metadata_cannot_qualify_research()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        var noFees = new ReplayRunner(data, fixture.Configuration with { FeeSchedules = [] }, fixture.Engine).RunToEnd();
        Assert.Empty(Assert.Single(noFees.Sessions).Trades);
        Assert.Contains(noFees.Sessions[0].Decisions, row => row.Class == "CANDLE_RESEARCH" && row.Reasons.Contains("RESEARCH_FINANCIAL_EVIDENCE"));
        var missing = data with { Candles = data.Candles.Where(bar => bar.InstrumentId != "bank-13").ToArray() };
        Assert.Empty(Assert.Single(new ReplayRunner(missing, fixture.Configuration, fixture.Engine).RunToEnd().Sessions).Trades);
        var futureDefinition = data with { Manifest = data.Manifest with { Instruments = data.Manifest.Instruments.Select(item => item.Id == "bank-13"
            ? item with { AvailableAtUtc = ReplayFixture.MetadataTime.AddMonths(1) } : item).ToArray() } };
        var future = Assert.Single(new ReplayRunner(futureDefinition, fixture.Configuration, fixture.Engine).RunToEnd().Sessions);
        Assert.Empty(future.Trades);
        Assert.Contains(future.Decisions, row => row.Reasons.Contains("MISSING_CONSTITUENT_EVIDENCE"));
    }

    [Fact]
    public void Delayed_entry_revalidates_futures_validity_and_cancelled_setup_cannot_retry()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        var expiry = data.Candles.Single(bar => bar.InstrumentId == "future" && bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay == new TimeSpan(9, 32, 0)).StartUtc;
        data = data with { Manifest = data.Manifest with { Instruments = data.Manifest.Instruments.Select(item => item.Id == "future"
            ? item with { ValidToUtc = expiry } : item).ToArray() } };
        var config = fixture.Configuration with { Presets = [new() { ExecutionDelay = TimeSpan.FromSeconds(5) }] };
        var result = Assert.Single(new ReplayRunner(data, config, fixture.Engine).RunToEnd().Sessions);
        Assert.Empty(result.Trades);
        Assert.Single(result.Decisions, row => row.Action == "HYPOTHETICAL_INTENT_ACCEPTED");
        Assert.Single(result.Decisions, row => row.Action == "HYPOTHETICAL_INTENT_CANCELED");
    }

    [Fact]
    public void Different_dates_and_adverse_presets_start_independently_and_changes_do_not_mutate_captured_data()
    {
        using var fixture = new ReplayFixture(ReplayFixture.Date, ReplayFixture.Date.AddDays(3));
        var dataset = fixture.Load();
        var weights = dataset.Manifest.Weights[0].Members.ToList();
        var input = dataset with { Manifest = dataset.Manifest with { Weights = [dataset.Manifest.Weights[0] with { Members = weights }] } };
        var configuration = fixture.Configuration with { Presets = [new(), new() { Id = "adverse", FullSpreadTicks = 40, AdverseSlippageTicks = 10 }] };
        var runner = new ReplayRunner(input, configuration, fixture.Engine);
        weights.Clear();
        var result = runner.RunToEnd();
        Assert.Equal(4, result.Sessions.Count);
        Assert.All(result.Sessions, session => Assert.Single(session.Trades));
        Assert.Equal(result.Sessions[0].RealizedNetPnl, result.Sessions[2].RealizedNetPnl);
        Assert.True(result.Sessions[1].RealizedNetPnl < result.Sessions[0].RealizedNetPnl);
        Assert.Equal(14, result.CapturedDataset!.Manifest.Weights[0].Members.Count);
    }

    [Fact]
    public void Reports_bind_captured_configuration_and_do_not_overwrite_existing_run_evidence()
    {
        using var fixture = new ReplayFixture();
        var result = new ReplayRunner(fixture.Load(), fixture.Configuration, fixture.Engine).RunToEnd();
        var output = Path.Combine(fixture.Root, "report");
        ReplayReportWriter.Write(output, result);
        Assert.True(File.Exists(Path.Combine(output, "summary.md")));
        Assert.True(File.Exists(Path.Combine(output, "trades.csv")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "run-manifest.json")));
        Assert.Equal("candle-replay-run-v1", manifest.RootElement.GetProperty("SchemaVersion").GetString());
        Assert.Contains("CANDLE_RESEARCH_NOT_OBSERVED_EXECUTION", File.ReadAllText(Path.Combine(output, "run-manifest.json")));
        Assert.Throws<IOException>(() => ReplayReportWriter.Write(output, result));
    }

    [Fact]
    public void Shared_constituents_and_coherent_marks_prevent_a_false_cross_index_loss_pause()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        var secondIndex = data.Manifest.Indices[0] with { Id = "SECOND" };
        var originalFuture = data.Manifest.Instruments.Single(item => item.Id == "future");
        var originalCall = data.Manifest.Instruments.Single(item => item.Id == "call");
        var secondFuture = originalFuture with { Id = "future-second", IndexId = "SECOND", InstrumentToken = 8000001, File = "future-second.json" };
        var secondCall = originalCall with { Id = "call-second", IndexId = "SECOND", InstrumentToken = 8000002, File = "call-second.json" };
        var extraCandles = data.Candles.Where(bar => bar.InstrumentId is "future" or "call")
            .Select(bar => bar with { InstrumentId = bar.InstrumentId == "future" ? "future-second" : "call-second" });
        var all = data.Candles.Concat(extraCandles).Where(bar => !(bar.InstrumentId == "call-second"
            && bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay is var time && (time == new TimeSpan(9, 32, 0) || time == new TimeSpan(9, 33, 0))))
            .Select(bar => bar.InstrumentId == "call" && bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay == new TimeSpan(9, 32, 0)
                ? bar with { Close = 199.6m } : bar.InstrumentId == "call" && bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay == new TimeSpan(9, 33, 0)
                    ? bar with { Open = 199.6m } : bar).ToArray();
        data = data with { Candles = all, Manifest = data.Manifest with
        {
            Indices = data.Manifest.Indices.Append(secondIndex).ToArray(),
            Instruments = data.Manifest.Instruments.Concat([secondFuture, secondCall]).ToArray(),
            Weights = data.Manifest.Weights.Append(data.Manifest.Weights[0] with { IndexId = "SECOND" }).ToArray()
        } };
        var settings = fixture.Engine with { Strategies = [fixture.Engine.Strategies[0], fixture.Engine.Strategies[0] with { IndexId = "SECOND" }],
            Portfolio = fixture.Engine.Portfolio with { MaximumActiveOrPendingEntries = 2, NetSessionPauseLoss = 70m } };
        var result = Assert.Single(new ReplayRunner(data, fixture.Configuration, settings).RunToEnd().Sessions);
        Assert.Equal(2, result.Trades.Count);
        Assert.DoesNotContain(result.Decisions, row => row.Reasons.Contains("PORTFOLIO_SESSION_PAUSE"));
        Assert.Contains(result.Equity, point => point.TimeUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay == new TimeSpan(9, 33, 0)
            && point.NetSessionPnl is null);
    }

    [Fact]
    public void Research_adapter_rejects_future_diagnostics_and_rechecks_changed_contracts()
    {
        var input = global::Trading.Tests.Engine.EngineFixture.Entry();
        var engine = new TradingDecisionEngine(new());
        var diagnostics = engine.EvaluateIndex(input);
        var earlier = input with { AsOfUtc = input.AsOfUtc.AddSeconds(-2) };
        Assert.Throws<ArgumentException>(() => engine.AssessCandleResearch(earlier, diagnostics, []));
        var expired = input with { Candidates = [input.Candidates[0] with { Contract = input.Candidates[0].Contract with
            { Expiry = input.Session.TradingDate.AddDays(-1) } }] };
        var assessment = Assert.Single(engine.AssessCandleResearch(expired, diagnostics, []));
        Assert.False(assessment.HypotheticallyEligible);
        Assert.Contains(assessment.Gates, gate => gate.Code == "RESEARCH_CURRENT_CONTRACT" && gate.Outcome == GateOutcome.FAIL);
        Assert.Throws<ArgumentException>(() => engine.AssessCandleResearch(input with
            { Identity = input.Identity with { Mode = EngineDataMode.PAPER_LIVE_DATA } }, diagnostics, []));
    }

    [Fact]
    public void Nonminute_time_exit_uses_its_exact_deadline_then_waits_for_next_open()
    {
        using var fixture = new ReplayFixture();
        var profile = fixture.Engine.Strategies[0];
        var settings = fixture.Engine with { Strategies = [profile with { Position = profile.Position with { TimeExitAfter = TimeSpan.FromSeconds(150) } }] };
        var result = Assert.Single(new ReplayRunner(fixture.Load(), fixture.Configuration, settings).RunToEnd().Sessions);
        var trade = Assert.Single(result.Trades);
        Assert.Contains(result.Decisions, row => row.Class == "CANDLE_POSITION_POLICY" && row.Action == "EXIT"
            && row.TimeUtc == trade.EntryTimeUtc.AddSeconds(150) && row.Reasons.Contains("TIME_NO_PROGRESS"));
        Assert.Equal(trade.EntryTimeUtc.AddMinutes(3), trade.ExitKnownAtUtc);
    }

    [Fact]
    public void Immediate_spread_stop_executes_on_the_known_open_independently_of_future_volume()
    {
        using var fixture = new ReplayFixture();
        var data = fixture.Load();
        var settings = fixture.Engine with { Strategies = [fixture.Engine.Strategies[0] with
            { Risk = fixture.Engine.Strategies[0].Risk with { GrossCashStop = 10m } }] };
        var expected = Assert.Single(new ReplayRunner(data, fixture.Configuration, settings).RunToEnd().Sessions).Trades.Single();
        var changed = data with { Candles = data.Candles.Select(bar => bar.InstrumentId == "call"
            && bar.StartUtc == expected.EntryTimeUtc ? bar with { Volume = 0m } : bar).ToArray() };
        var actual = Assert.Single(new ReplayRunner(changed, fixture.Configuration, settings).RunToEnd().Sessions).Trades.Single();
        Assert.Equal(expected.EntryTimeUtc, expected.ExitKnownAtUtc);
        Assert.Equal(expected.EntryTimeUtc, actual.ExitKnownAtUtc);
        Assert.Equal(expected.NetPnl, actual.NetPnl);
        Assert.Equal("GROSS_CASH_STOP_AFTER_ENTRY", actual.ExitReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void Intrabar_fees_follow_the_execution_interval_not_later_booking_time(int publicationLagMs)
    {
        using var fixture = new ReplayFixture();
        fixture.Configuration = fixture.Configuration with { PublicationLag = TimeSpan.FromMilliseconds(publicationLagMs) };
        var data = fixture.Load();
        var minute = publicationLagMs == 0 ? new TimeSpan(9, 31, 0) : new TimeSpan(9, 32, 0);
        var stop = data.Candles.Single(bar => bar.InstrumentId == "call" && bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay == minute);
        var old = fixture.Configuration.FeeSchedules[0] with { Version = "before-boundary", EffectiveToUtc = stop.EndUtc };
        var next = old with { Version = "after-boundary", EffectiveFromUtc = stop.EndUtc, EffectiveToUtc = null, BrokeragePerOrder = 10m };
        var config = fixture.Configuration with { FeeSchedules = [old, next] };
        data = data with { Candles = data.Candles.Select(bar => bar.Id == stop.Id ? bar with { Low = 190m } : bar).ToArray() };
        var trade = Assert.Single(new ReplayRunner(data, config, fixture.Engine).RunToEnd().Sessions).Trades.Single();
        Assert.Equal("before-boundary", trade.ExitFees!.ScheduleVersion);
        Assert.Equal(2m, trade.ExitFees.Total);
        Assert.Equal("INTERVAL_SINGLE_SCHEDULE", trade.ExitFeeAttribution);
        Assert.Equal(stop.AvailableAtUtc, trade.ExitKnownAtUtc);
    }

    [Fact]
    public void Delayed_second_index_entry_waits_when_existing_position_has_no_available_mark()
    {
        using var fixture = new ReplayFixture();
        fixture.Configuration = fixture.Configuration with { PublicationLag = TimeSpan.FromSeconds(1),
            Presets = [new() { ExecutionDelay = TimeSpan.FromSeconds(5) }] };
        var data = fixture.Load();
        var secondFuture = data.Manifest.Instruments.Single(item => item.Id == "future") with
            { Id = "future-second", IndexId = "SECOND", InstrumentToken = 8000001, File = "future-second.json" };
        var secondCall = data.Manifest.Instruments.Single(item => item.Id == "call") with
            { Id = "call-second", IndexId = "SECOND", InstrumentToken = 8000002, File = "call-second.json" };
        var extra = data.Candles.Where(bar => bar.InstrumentId is "future" or "call").Select(bar =>
            bar.InstrumentId == "future" ? bar with { InstrumentId = "future-second",
                Close = bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay == new TimeSpan(9, 30, 0) ? 55000m : bar.Close }
                : bar with { InstrumentId = "call-second" });
        data = data with { Candles = data.Candles.Concat(extra).Where(bar => !(bar.InstrumentId == "call"
            && bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay == new TimeSpan(9, 33, 0))).ToArray(),
            Manifest = data.Manifest with
            {
                Indices = data.Manifest.Indices.Append(data.Manifest.Indices[0] with { Id = "SECOND" }).ToArray(),
                Instruments = data.Manifest.Instruments.Concat([secondFuture, secondCall]).ToArray(),
                Weights = data.Manifest.Weights.Append(data.Manifest.Weights[0] with { IndexId = "SECOND" }).ToArray()
            } };
        var settings = fixture.Engine with { Strategies = [fixture.Engine.Strategies[0], fixture.Engine.Strategies[0] with { IndexId = "SECOND" }],
            Portfolio = fixture.Engine.Portfolio with { MaximumActiveOrPendingEntries = 2 } };
        var session = new ReplayRunner(data, fixture.Configuration, settings).CreateSession(ReplayFixture.Date, "base");
        var target = new DateTimeOffset(2026, 10, 9, 4, 3, 0, TimeSpan.Zero); // 09:33 IST
        while (session.CurrentTimeUtc is null || session.CurrentTimeUtc < target) session.Step();
        Assert.Single(session.Result.Trades);
        Assert.Equal(1, session.Result.PendingEntries);
        Assert.Null(session.Result.Equity[^1].NetSessionPnl);
        Assert.Contains(session.Result.Decisions, row => row.IndexId == "SECOND" && row.Action == "HYPOTHETICAL_INTENT_ACCEPTED");
    }
}
