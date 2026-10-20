using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Trading.Api;
using Trading.Data;
using Trading.Engine;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class ProfitExitIntegrationTests
{
    [Fact]
    public async Task An_orphaned_variant_after_checkpoint_failure_does_not_block_a_later_policy_selection()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        var checkpoint = Path.Combine(fixture.Root, "jobs", job.Id, "job.json");
        try
        {
            File.SetAttributes(checkpoint, FileAttributes.ReadOnly);
            using var failed = await Select(fixture, job.Id, ProfitExitMode.FixedNetTarget);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        }
        finally { File.SetAttributes(checkpoint, FileAttributes.Normal); }
        await fixture.Restart(); await fixture.Command(job.Id, "retry");
        await fixture.Wait(job.Id, view => view.Job.CanReplay);
        using var selected = await Select(fixture, job.Id, ProfitExitMode.TrailingNetProfit);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var view = (await selected.Content.ReadFromJsonAsync<HistoricalReplayView>())!;
        Assert.True(view.Job.Run > 2); Assert.Equal(ProfitExitMode.TrailingNetProfit, view.Settings!.ProfitExit!.Mode);
    }

    [Fact]
    public async Task Selecting_profit_policy_preserves_special_session_restrictions_and_checkpoint_recovery()
    {
        await using var fixture = new HistoricalApiFixture();
        var path = Path.Combine(fixture.Root, "calendars", "2026.json");
        var calendar = HistoricalAcquisitionStore.Read<ExchangeCalendarDocument>(path)!;
        await HistoricalAcquisitionStore.AtomicJsonAsync(path, calendar with
            { SpecialSessions = [new(ReplayFixture.Date, new(9,15), new(9,34))] }, CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare(); var initial = await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        Assert.Contains("SPECIAL_SESSION_REQUIRES_DATE_SPECIFIC_STRATEGY_PROFILE", initial.Job.Warnings);
        using var selected = await Select(fixture, job.Id, ProfitExitMode.FixedNetTarget);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        await fixture.Command(job.Id, "step"); await fixture.Command(job.Id, "step");
        var before = await fixture.View(job.Id);
        await fixture.Restart(); await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        Assert.Equal(before.PaperRunId, recovered.PaperRunId); Assert.Equal(before.SimulatedTimeUtc, recovered.SimulatedTimeUtc);
        Assert.Empty(recovered.Trades);
        Assert.Contains("SPECIAL_SESSION_REQUIRES_DATE_SPECIFIC_STRATEGY_PROFILE", recovered.Job.Warnings);
    }

    [Fact]
    public async Task A_failed_policy_run_commit_blocks_playback_until_preparation_recovers()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        void Execute(string sql)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = Path.Combine(fixture.Root, "paper-replay.sqlite"), Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        Execute("CREATE TRIGGER fail_profit_variant BEFORE INSERT ON paper_runs WHEN NEW.run_id LIKE '%:run-2:%' BEGIN SELECT RAISE(FAIL,'test policy commit failure'); END;");
        using var response = await Select(fixture, job.Id, ProfitExitMode.FixedNetTarget);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var failed = await fixture.View(job.Id);
        Assert.Equal("Failed", failed.Job.State); Assert.False(failed.Job.CanReplay);
        Execute("DROP TRIGGER fail_profit_variant;");
        await fixture.Command(job.Id, "retry"); var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        Assert.True(recovered.Paused); Assert.Equal(ProfitExitMode.FixedNetTarget, recovered.Settings!.ProfitExit!.Mode);
    }

    [Fact]
    public async Task Trailing_peak_and_lock_recover_without_duplicate_fills_and_active_positions_block_policy_changes()
    {
        await using var fixture = new HistoricalApiFixture();
        var path = ProfitPath(fixture.Files.Load()).Candles.Where(bar => bar.InstrumentId == "call");
        fixture.Handler.Data[12656898] = JsonSerializer.SerializeToUtf8Bytes(new { status = "success", data = new
            { candles = path.Select(bar => new object?[] { bar.StartUtc.ToString("O"), bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, bar.OpenInterest }).ToArray() } });
        fixture.Start(); var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        using var selected = await Select(fixture, job.Id, ProfitExitMode.TrailingNetProfit); selected.EnsureSuccessStatusCode();
        var peakTime = new DateTimeOffset(2026,10,9,4,3,0,TimeSpan.Zero);
        HistoricalReplayView peak;
        do { await fixture.Command(job.Id, "step"); peak = await fixture.View(job.Id); } while (peak.SimulatedTimeUtc < peakTime);
        Assert.Equal(666m, Assert.Single(peak.Paper!.Positions).ProfitExit!.PeakNetLiquidationPnl);
        Assert.Equal(466m, Assert.Single(peak.Paper.Positions).CurrentProfitExitThreshold);
        using var blocked = await Select(fixture, job.Id, ProfitExitMode.FixedNetTarget);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Equal(peak.Job.Run, (await fixture.View(job.Id)).Job.Run);
        await fixture.Restart(); await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        Assert.Equal(466m, Assert.Single(recovered.Paper!.Positions).CurrentProfitExitThreshold);
        Assert.Equal(666m, Assert.Single(recovered.Paper.Positions).ProfitExit!.PeakNetLiquidationPnl);
        await fixture.Command(job.Id, "step"); var exited = await fixture.View(job.Id);
        var trade = Assert.Single(exited.Trades);
        Assert.Equal("NET_PROFIT_TRAIL", trade.ExitReason); Assert.Equal(466m, trade.NetPnl);
        Assert.Equal(1, exited.Paper!.AcceptedAttempts); Assert.Equal(4m, exited.Paper.PaidFees);
        Assert.Equal(18, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("{\"mode\":1}")]
    [InlineData("{\"mode\":\"Unknown\"}")]
    [InlineData("{\"mode\":\"FixedNetTarget\",\"netTarget\":0}")]
    [InlineData("{\"mode\":\"TrailingNetProfit\",\"minimumLockedNetProfit\":500}")]
    public async Task Invalid_profit_selections_do_not_open_a_new_run(string policy)
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); var ready = await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        using var body = new StringContent("{\"action\":\"setProfitExit\",\"profitExit\":" + policy + "}", System.Text.Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync($"/api/replay/jobs/{job.Id}/commands", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ready.Job.Run, (await fixture.View(job.Id)).Job.Run);
    }

    [Fact]
    public void Profit_exit_delay_uses_a_later_opening_and_reports_realized_shortfall()
    {
        using var fixture = new ReplayFixture();
        var profile = fixture.Engine.Strategies[0];
        var engine = fixture.Engine with { Strategies = [profile with { Position = profile.Position with { ProfitExit = new() { Mode = ProfitExitMode.FixedNetTarget } } }] };
        var settings = fixture.Configuration with { Presets = [new() { ExecutionDelay = TimeSpan.FromSeconds(1) }] };
        var data = DelayedProfitPath(fixture.Load());
        var trade = Assert.Single(Assert.Single(new ReplayRunner(data, settings, engine).RunToEnd().Sessions).Trades);
        Assert.Equal("NET_PROFIT_TARGET", trade.ExitReason); Assert.Equal(466m, trade.NetPnl);
        Assert.Equal(566m, trade.ProfitExit!.TriggerNetLiquidationPnl); Assert.Equal(500m, trade.ProfitExit.TriggerThreshold);
        Assert.Equal(34m, trade.ProfitExit.RealizedShortfall);
        Assert.True(trade.ExitKnownAtUtc >= trade.ProfitExit.TriggeredAtUtc!.Value.AddSeconds(1));
    }

    [Fact]
    public void Missing_post_trigger_candles_keep_the_exit_pending_without_an_invented_profit_fill()
    {
        using var fixture = new ReplayFixture(); var profile = fixture.Engine.Strategies[0];
        var engine = fixture.Engine with { Strategies = [profile with { Position = profile.Position with { ProfitExit = new() { Mode = ProfitExitMode.FixedNetTarget } } }] };
        var settings = fixture.Configuration with { Presets = [new() { ExecutionDelay = TimeSpan.FromSeconds(1) }] };
        var data = DelayedProfitPath(fixture.Load());
        var cutoff = new DateTimeOffset(2026,10,9,4,4,0,TimeSpan.Zero);
        data = data with { Candles = data.Candles.Where(bar => bar.InstrumentId != "call" || bar.StartUtc < cutoff).ToArray() };
        var result = Assert.Single(new ReplayRunner(data, settings, engine).RunToEnd().Sessions);
        var trade = Assert.Single(result.Trades);
        Assert.False(result.Complete); Assert.Equal(1, result.PendingExits); Assert.Null(trade.ExitPrice);
        Assert.NotNull(trade.ProfitExit!.TriggeredAtUtc); Assert.Null(trade.ProfitExit.RealizedShortfall);
    }

    [Fact]
    public void An_option_candle_high_does_not_ratchet_the_trail_or_change_its_fills()
    {
        using var fixture = new ReplayFixture(); var profile = fixture.Engine.Strategies[0];
        var engine = fixture.Engine with { Strategies = [profile with { Position = profile.Position with { ProfitExit = new() { Mode = ProfitExitMode.TrailingNetProfit } } }] };
        var data = ProfitPath(fixture.Load());
        var changed = data with { Candles = data.Candles.Select(bar => bar.InstrumentId == "call"
            ? bar with { High = 10000m } : bar).ToArray() };
        var first = Assert.Single(new ReplayRunner(data, fixture.Configuration, engine).RunToEnd().Sessions).Trades;
        var second = Assert.Single(new ReplayRunner(changed, fixture.Configuration, engine).RunToEnd().Sessions).Trades;
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(666m, Assert.Single(first).ProfitExit!.PeakNetLiquidationPnl);
    }

    private static Trading.Domain.HistoricalDataset DelayedProfitPath(Trading.Domain.HistoricalDataset data) => ProfitPath(data) with
    {
        Candles = ProfitPath(data).Candles.Select(bar => bar.InstrumentId != "call" ? bar :
            bar.StartUtc == new DateTimeOffset(2026,10,9,4,2,0,TimeSpan.Zero) ? bar with { Open = 200m, High = 201m, Low = 199m, Close = 200m }
            : bar.StartUtc == new DateTimeOffset(2026,10,9,4,3,0,TimeSpan.Zero) ? bar with { Open = 224m, High = 225m, Low = 223m, Close = 224m } : bar).ToArray()
    };

    [Theory]
    [InlineData(ProfitExitMode.FixedNetTarget, "NET_PROFIT_TARGET", 566, 9, 32)]
    [InlineData(ProfitExitMode.TrailingNetProfit, "NET_PROFIT_TRAIL", 466, 9, 34)]
    public void Replay_records_profit_trigger_peak_and_fill_using_the_existing_cost_model(ProfitExitMode mode, string reason, decimal net, int hour, int minute)
    {
        using var fixture = new ReplayFixture();
        var profile = fixture.Engine.Strategies[0];
        var engine = fixture.Engine with { Strategies = [profile with { Position = profile.Position with { ProfitExit = new() { Mode = mode } } }] };
        var data = ProfitPath(fixture.Load());
        var result = new ReplayRunner(data, fixture.Configuration, engine).RunToEnd();
        var trade = Assert.Single(Assert.Single(result.Sessions).Trades);
        Assert.Equal(reason, trade.ExitReason); Assert.Equal(net, trade.NetPnl);
        Assert.Equal(new TimeOnly(hour, minute), TimeOnly.FromDateTime(trade.ExitKnownAtUtc!.Value.ToOffset(TimeSpan.FromHours(5.5)).DateTime));
        var evidence = JsonSerializer.SerializeToElement(trade, KiteCandleImporter.JsonOptions).GetProperty("ProfitExit");
        Assert.Equal(mode.ToString(), evidence.GetProperty("Policy").GetProperty("Mode").GetString());
        Assert.True(evidence.GetProperty("PeakNetLiquidationPnl").GetDecimal() >= 500m);
        Assert.Equal(0m, evidence.GetProperty("RealizedShortfall").GetDecimal());
        var output = Path.Combine(fixture.Root, "profit-report"); ReplayReportWriter.Write(output, result);
        Assert.Contains("profitExitMode", File.ReadAllText(Path.Combine(output, "trades.csv")));
    }

    private static Trading.Domain.HistoricalDataset ProfitPath(Trading.Domain.HistoricalDataset data) => data with
    {
        Candles = data.Candles.Select(bar =>
        {
            if (bar.InstrumentId != "call") return bar;
            var local = TimeOnly.FromDateTime(bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
            decimal price = local == new TimeOnly(9,32) ? 224m : local == new TimeOnly(9,33) ? 228m
                : local == new TimeOnly(9,34) ? 220m : local == new TimeOnly(9,35) ? 219m : 200m;
            return bar with { Open = price, Close = price, High = price + 1m, Low = price - 1m };
        }).ToArray()
    };

    [Fact]
    public async Task Selecting_profit_policy_starts_a_frozen_same_dataset_variant_and_survives_restart()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); var baseline = await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        var acquisition = Path.Combine(fixture.Root, "jobs", job.Id, "acquisition.json");
        var frozenHash = KiteCandleImporter.Hash(await File.ReadAllBytesAsync(acquisition));
        await fixture.Command(job.Id, "run"); await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        using var response = await Select(fixture, job.Id, ProfitExitMode.FixedNetTarget);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var selected = (await response.Content.ReadFromJsonAsync<HistoricalReplayView>())!;
        Assert.True(selected.Job.Run > baseline.Job.Run); Assert.True(selected.Paused); Assert.Null(selected.SimulatedTimeUtc);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("FixedNetTarget", json.RootElement.GetProperty("settings").GetProperty("profitExit").GetProperty("mode").GetString());
        await fixture.Restart(); await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        using var recoveredJson = JsonDocument.Parse(await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}"));
        Assert.Equal("FixedNetTarget", recoveredJson.RootElement.GetProperty("settings").GetProperty("profitExit").GetProperty("mode").GetString());
        Assert.Equal(frozenHash, KiteCandleImporter.Hash(await File.ReadAllBytesAsync(acquisition)));
        Assert.Equal(18, fixture.Handler.Requests.Count);
        await fixture.Command(job.Id, "run"); await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        var runs = (await fixture.Client.GetFromJsonAsync<ReplayRunInfo[]>($"/api/replay/runs?date={ReplayFixture.Date:yyyy-MM-dd}"))!;
        Assert.Equal(runs.Single(run => run.RunId == baseline.PaperRunId).DatasetHash, runs.Single(run => run.RunId == recovered.PaperRunId).DatasetHash);
        var listed = await fixture.Client.GetFromJsonAsync<JsonElement>($"/api/replay/runs?date={ReplayFixture.Date:yyyy-MM-dd}");
        var selectedRow = listed.EnumerateArray().Single(row => row.GetProperty("runId").GetString() == recovered.PaperRunId);
        Assert.Equal("FixedNetTarget", selectedRow.GetProperty("profitExit").GetProperty("mode").GetString());
        var comparison = await fixture.Client.GetFromJsonAsync<ReplayRunComparison>($"/api/replay/jobs/{job.Id}/compare?left={Uri.EscapeDataString(baseline.PaperRunId!)}&right={Uri.EscapeDataString(recovered.PaperRunId!)}");
        Assert.True(comparison!.ConfigurationChanged);
    }

    private static Task<HttpResponseMessage> Select(HistoricalApiFixture fixture, string id, ProfitExitMode mode) => fixture.Client.PostAsJsonAsync(
        $"/api/replay/jobs/{id}/commands", new { action = "setProfitExit", profitExit = new { version = "profit-exit-v1", mode = mode.ToString(),
            netTarget = 500m, trailActivation = 500m, trailGiveback = 200m, minimumLockedNetProfit = 300m } });
}
