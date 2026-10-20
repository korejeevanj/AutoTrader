using System.Net.Http.Json;
using Trading.Api;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class PlaybackControlTests
{
    [Fact]
    public async Task Play_continues_a_comparison_recovered_after_a_preset_commit_but_before_the_next_preset_checkpoint()
    {
        await using var fixture = new HistoricalApiFixture();
        var settings = Trading.Replay.ReplayConfiguration.FromJson(File.ReadAllText(fixture.ReplayPath)) with
            { Presets = [new(), new() { Id = "adverse", FullSpreadTicks = 40, AdverseSlippageTicks = 10 }] };
        await Trading.Data.HistoricalAcquisitionStore.AtomicJsonAsync(fixture.ReplayPath, settings, CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare();
        var ready = await fixture.Wait(job.Id, view => view.Job.PreparationComplete && !view.Job.PreparingData);

        // Reproduce the durable state of a crash after the preset's last transaction,
        // with the host still checkpointed on that preset. No report was published.
        var root = Path.Combine(fixture.Root, "jobs", job.Id);
        var frozen = Trading.Data.HistoricalAcquisitionStore.Read<Trading.Data.FrozenAcquisition>(Path.Combine(root, "acquisition.json"))!;
        var replay = Trading.Replay.ReplayConfiguration.FromJson(frozen.ReplayJson);
        var engine = Trading.Engine.EngineConfiguration.FromJson(frozen.EngineJson);
        var dataset = Trading.Data.KiteCandleImporter.Load(Path.Combine(root, "attempt-1", "dataset.json"), replay.PublicationLag);
        var journal = new Trading.Data.ReplayEvidenceJournal(Path.Combine(fixture.Root, "paper-replay.sqlite"));
        var runner = new Trading.Replay.ReplayRunner(dataset, replay, engine);
        var session = new Trading.Replay.DurableReplaySession(runner.CreateSession(job.Date, "base"), journal,
            new(job.Id, ready.Job.Run, "base", journal.Checkpoint(ready.PaperRunId!)!.InputHash,
                Trading.Data.HistoricalAcquisitionStore.Midnight(job.Date, "Asia/Kolkata").Add(dataset.Manifest.MarketOpen.ToTimeSpan()), "BANKNIFTY"));
        while (!session.IsComplete) session.Step();
        var checkpoint = Path.Combine(root, "job.json");
        var saved = Trading.Data.HistoricalAcquisitionStore.Read<HistoricalReplayJob>(checkpoint)!;
        await Trading.Data.HistoricalAcquisitionStore.AtomicJsonAsync(checkpoint, saved with { AllPresets = true }, CancellationToken.None);

        await fixture.Restart(); await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        Assert.True(recovered.Complete);
        Assert.True(recovered.PresetProgress!.AllPresets);
        Assert.Equal(ready.PaperRunId, recovered.PaperRunId);
        await fixture.Command(job.Id, "play");
        var complete = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        Assert.Equal(ready.Job.Run, complete.Job.Run);
        Assert.Equal(2, complete.PresetProgress!.Number);
        Assert.True(complete.PresetProgress.AllPresets);
        using var report = System.Text.Json.JsonDocument.Parse(await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/reports/{complete.Job.Run}/results.json"));
        Assert.Equal(2, report.RootElement.GetProperty("Sessions").GetArrayLength());
    }

    [Fact]
    public async Task Server_restart_changes_snapshot_instance_identity_for_the_same_persisted_job()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); var before = await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        Assert.False(string.IsNullOrWhiteSpace(before.ServerInstanceId));
        await fixture.Restart(); var after = await fixture.View(job.Id);
        Assert.Equal(before.Job.Id, after.Job.Id);
        Assert.NotEqual(before.ServerInstanceId, after.ServerInstanceId);
    }

    [Fact]
    public async Task Restart_recovers_the_explicit_comparison_scope_and_runs_the_remaining_presets()
    {
        await using var fixture = new HistoricalApiFixture { Clock = new ObservableIterationClock() };
        var settings = Trading.Replay.ReplayConfiguration.FromJson(File.ReadAllText(fixture.ReplayPath)) with
            { Presets = [new(), new() { Id = "adverse" }, new() { Id = "stress" }] };
        await Trading.Data.HistoricalAcquisitionStore.AtomicJsonAsync(fixture.ReplayPath, settings, CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        await fixture.Command(job.Id, "runAllPresets");
        await fixture.Wait(job.Id, view => view.PresetProgress?.Number == 2 && !view.Complete);
        await fixture.Command(job.Id, "pause");
        await fixture.Restart(); await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        Assert.True(recovered.PresetProgress!.AllPresets);
        Assert.Equal(2, recovered.PresetProgress.Number); Assert.True(recovered.Paused);
        await fixture.Command(job.Id, "play");
        var done = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        using var report = System.Text.Json.JsonDocument.Parse(await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/reports/{done.Job.Run}/results.json"));
        Assert.Equal(3, report.RootElement.GetProperty("Sessions").GetArrayLength());
    }

    [Fact]
    public async Task Completed_price_preview_promotion_opens_a_coherently_ready_strategy_session()
    {
        await using var fixture = new HistoricalApiFixture(); File.Delete(fixture.Catalog);
        var archive = new GatedExchangeArchive(); fixture.ExchangeHandler = archive;
        var master = "instrument_token,exchange,tradingsymbol,name,instrument_type,expiry,strike,lot_size,tick_size\n"
            + "260105,NSE,NIFTY BANK,NIFTY BANK,EQ,,0,1,0.05\n"
            + "12466946,NFO,BANKNIFTY26OCTFUT,BANKNIFTY,FUT,2026-10-27,0,30,0.2\n"
            + "12656898,NFO,BANKNIFTY26OCT55000CE,BANKNIFTY,CE,2026-10-27,55000,30,0.05\n"
            + "12657154,NFO,BANKNIFTY26OCT55000PE,BANKNIFTY,PE,2026-10-27,55000,30,0.05\n";
        await new Trading.Data.HistoricalAcquisitionStore(fixture.Root).ArchiveMasterAsync(System.Text.Encoding.UTF8.GetBytes(master),
            new(2026,10,17,3,0,0,TimeSpan.Zero), CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare(); await archive.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Command(job.Id, "run"); await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        archive.Release.TrySetResult();
        var ready = await fixture.Wait(job.Id, view => view.Job.PreparationComplete && view.PlaybackKind == "Strategy");
        Assert.Equal("ReadyWithWarnings", ready.Job.State);
        Assert.False(ready.Complete); Assert.Null(ready.SimulatedTimeUtc); Assert.True(ready.Paused);
        Assert.False(ready.Job.ReportsAvailable);
    }

    private sealed class ObservableIterationClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026,10,17,10,0,0,TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => TimeProvider.System.CreateTimer(callback, state, dueTime > TimeSpan.Zero && dueTime < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : dueTime, period);
    }
    private sealed class GatedExchangeArchive : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(token);
            return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(NseHistoricalContractTests.Archive()) };
        }
    }

    [Fact]
    public async Task A_completed_run_keeps_its_completed_state_and_reports_after_recovery()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        await fixture.Command(job.Id, "run"); var done = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        await fixture.Restart(); await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        Assert.Equal("Completed", recovered.Job.State);
        Assert.True(recovered.Complete); Assert.True(recovered.Job.ReportsAvailable);
        Assert.Equal(done.PaperRunId, recovered.PaperRunId);
        Assert.Contains("summary.md", recovered.ReportFiles);
    }

    [Fact]
    public async Task Cancelling_completed_playback_does_not_turn_its_published_result_into_a_cancelled_run()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        await fixture.Command(job.Id, "run"); await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        await fixture.Command(job.Id, "cancel");
        var view = await fixture.View(job.Id);
        Assert.Equal("Completed", view.Job.State); Assert.True(view.Job.ReportsAvailable);
    }

    [Fact]
    public async Task Explicit_comparison_from_a_partly_stepped_session_starts_a_new_run_from_the_first_preset()
    {
        await using var fixture = new HistoricalApiFixture();
        var settings = Trading.Replay.ReplayConfiguration.FromJson(File.ReadAllText(fixture.ReplayPath)) with
            { Presets = [new(), new() { Id = "adverse", FullSpreadTicks = 40, AdverseSlippageTicks = 10 }] };
        await Trading.Data.HistoricalAcquisitionStore.AtomicJsonAsync(fixture.ReplayPath, settings, CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        await fixture.Command(job.Id, "step");
        var partial = await fixture.View(job.Id);
        await fixture.Command(job.Id, "runAllPresets");
        var done = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        Assert.Equal(partial.Job.Run + 1, done.Job.Run);
        var runs = (await fixture.Client.GetFromJsonAsync<Trading.Data.ReplayRunInfo[]>($"/api/replay/runs?date={ReplayFixture.Date:yyyy-MM-dd}"))!;
        Assert.Contains(runs, run => run.RunId == partial.PaperRunId && !run.Complete);
        Assert.Equal(2, runs.Count(run => run.RunId.Contains($":run-{done.Job.Run}:", StringComparison.Ordinal) && run.Complete));
    }

    [Fact]
    public async Task All_presets_require_an_explicit_command_and_publish_both_results_with_visible_progress()
    {
        await using var fixture = new HistoricalApiFixture();
        var settings = Trading.Replay.ReplayConfiguration.FromJson(File.ReadAllText(fixture.ReplayPath)) with
            { Presets = [new(), new() { Id = "adverse", FullSpreadTicks = 40, AdverseSlippageTicks = 10 }] };
        await Trading.Data.HistoricalAcquisitionStore.AtomicJsonAsync(fixture.ReplayPath, settings, CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        await fixture.Command(job.Id, "runAllPresets");
        var done = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        var runs = (await fixture.Client.GetFromJsonAsync<Trading.Data.ReplayRunInfo[]>($"/api/replay/runs?date={ReplayFixture.Date:yyyy-MM-dd}"))!;
        Assert.Equal(new[] { "adverse", "base" }, runs.Select(run => run.Preset).Order(StringComparer.Ordinal));
        using var report = System.Text.Json.JsonDocument.Parse(await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/reports/{done.Job.Run}/results.json"));
        Assert.Equal(2, report.RootElement.GetProperty("Sessions").GetArrayLength());
        var view = await fixture.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/replay/jobs/{job.Id}");
        var progress = view.GetProperty("presetProgress");
        Assert.True(progress.GetProperty("allPresets").GetBoolean());
        Assert.Equal(2, progress.GetProperty("number").GetInt32());
        Assert.Equal(2, progress.GetProperty("total").GetInt32());
        Assert.Equal("adverse", progress.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Full_day_finishes_once_without_automatically_restarting_for_another_preset()
    {
        await using var fixture = new HistoricalApiFixture();
        var settings = Trading.Replay.ReplayConfiguration.FromJson(File.ReadAllText(fixture.ReplayPath)) with
            { Presets = [new(), new() { Id = "adverse", FullSpreadTicks = 40, AdverseSlippageTicks = 10 }] };
        await Trading.Data.HistoricalAcquisitionStore.AtomicJsonAsync(fixture.ReplayPath, settings, CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        await fixture.Command(job.Id, "run");
        var done = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        var runs = (await fixture.Client.GetFromJsonAsync<Trading.Data.ReplayRunInfo[]>($"/api/replay/runs?date={ReplayFixture.Date:yyyy-MM-dd}"))!;
        Assert.Equal(new[] { "base" }, runs.Select(run => run.Preset));
        Assert.Equal("base", done.Job.ActivePresetId);
        await Task.Delay(100);
        var stable = await fixture.View(job.Id);
        Assert.Equal("Completed", stable.Job.State);
        Assert.Equal(done.SimulatedTimeUtc, stable.SimulatedTimeUtc);
        Assert.Equal(done.PaperRunId, stable.PaperRunId);
    }

    [Fact]
    public async Task Date_preparation_returns_while_storage_initialization_is_waiting_for_a_schema_lock()
    {
        await using var fixture = new HistoricalApiFixture();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Root, "paper-replay.sqlite"), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE test_marker(value INTEGER); BEGIN EXCLUSIVE;"; command.ExecuteNonQuery();
        fixture.Start();
        var job = await fixture.Prepare().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(job.CanReplay);
        command.CommandText = "ROLLBACK;"; command.ExecuteNonQuery();
        var ready = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        Assert.True(ready.Job.PreparationComplete);
    }
    [Fact]
    public async Task Failed_report_publication_can_be_retried_without_replaying_or_duplicating_trades()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.CanReplay);
        var blocked = Path.Combine(fixture.Root, "jobs", job.Id, "reports");
        File.WriteAllText(blocked, "Test-owned obstruction to report directory creation.");
        await fixture.Command(job.Id, "run");
        var failed = await fixture.Wait(job.Id, view => view.Job.State == "Failed");
        Assert.True(failed.Complete); Assert.False(failed.Job.ReportsAvailable);
        var count = failed.Trades.Count; var run = failed.Job.Run;
        File.Delete(blocked); await fixture.Command(job.Id, "publish");
        var complete = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        Assert.Equal(run, complete.Job.Run); Assert.Equal(count, complete.Trades.Count);
        Assert.Contains("runtime.jsonl.gz", complete.ReportFiles);
    }
    [Fact]
    public async Task Failed_entry_control_commit_restores_previous_control_and_pauses_active_playback()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.CanReplay);
        using var play = await fixture.Client.PostAsJsonAsync($"/api/replay/jobs/{job.Id}/commands", new HistoricalReplayCommand("play", 1)); play.EnsureSuccessStatusCode();
        await fixture.Wait(job.Id, view => view.SimulatedTimeUtc is not null);
        void Execute(string sql)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Root, "paper-replay.sqlite"), Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        Execute("CREATE TRIGGER fail_control BEFORE INSERT ON paper_batches WHEN NEW.batch_id LIKE 'control-%' BEGIN SELECT RAISE(FAIL,'test control storage failure'); END;");
        using var failed = await fixture.Client.PostAsJsonAsync($"/api/replay/jobs/{job.Id}/commands", new HistoricalReplayCommand("pauseEntries"));
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var view = await fixture.View(job.Id); Assert.True(view.Paused); Assert.False(view.Paper!.EntriesPaused);
        Execute("DROP TRIGGER fail_control;"); await fixture.Command(job.Id, "pauseEntries");
        Assert.True((await fixture.View(job.Id)).Paper!.EntriesPaused);
    }
    [Fact]
    public void Conflicting_snapshot_or_event_evidence_cannot_be_accepted_as_a_duplicate()
    {
        using var fixture = new ReplayFixture(); var journal = new Trading.Data.ReplayEvidenceJournal(Path.Combine(fixture.Root, "journal.sqlite"));
        journal.Begin("run", "frozen-input");
        Trading.Data.PaperJournalEvent[] events = [new("event", DateTimeOffset.Parse("2026-10-16T04:00:00Z"), "BANKNIFTY", "call", "CANDLE_RESEARCH", "REJECTED", null, "[]")];
        journal.Append("run", "step-1", events[0].TimeUtc, "{}", "{\"cash\":1}", "[]", events);
        Assert.Throws<InvalidDataException>(() => journal.Append("run", "step-1", events[0].TimeUtc, "{}", "{\"cash\":2}", "[]", events));
        Assert.Throws<InvalidDataException>(() => journal.Append("run", "step-2", events[0].TimeUtc, "{}", "{\"cash\":1}", "[]", events));
        Assert.Single(journal.Read("run"));
    }
    [Fact]
    public void Pacing_uses_full_historical_intervals_compensates_processing_and_reanchors_speed_changes()
    {
        var clock = new ManualClock(); var pacer = new ReplayPacingClock(clock);
        var start = DateTimeOffset.Parse("2026-10-16T03:45:00Z");
        pacer.Reanchor(start, 1);
        Assert.Equal(TimeSpan.FromSeconds(60), pacer.DelayUntil(start.AddMinutes(1)));
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(TimeSpan.FromSeconds(40), pacer.DelayUntil(start.AddMinutes(1)));
        clock.Advance(TimeSpan.FromSeconds(45));
        Assert.Equal(TimeSpan.Zero, pacer.DelayUntil(start.AddMinutes(1)));
        Assert.Equal(TimeSpan.FromSeconds(55), pacer.DelayUntil(start.AddMinutes(2)));
        pacer.Reanchor(start.AddMinutes(1), 60);
        Assert.Equal(TimeSpan.FromSeconds(1), pacer.DelayUntil(start.AddMinutes(2)));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(TimeSpan.FromMilliseconds(600), pacer.DelayUntil(start.AddMinutes(2)));
    }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan time) => ticks += time.Ticks;
    }
    [Fact]
    public async Task Cancelled_runs_keep_operational_logs_and_downloadable_evidence()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.CanReplay);
        await fixture.Command(job.Id, "step"); await fixture.Command(job.Id, "cancel");
        using var response = await fixture.Client.GetAsync($"/api/replay/jobs/{job.Id}/diagnostics");
        response.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(json.RootElement.GetProperty("operations").EnumerateArray(), row => row.GetProperty("code").GetString() == "COMMAND_CANCEL");
        var runId = (await fixture.View(job.Id)).PaperRunId!;
        using var export = await fixture.Client.GetAsync($"/api/replay/jobs/{job.Id}/evidence?runId={Uri.EscapeDataString(runId)}");
        export.EnsureSuccessStatusCode();
        using var stream = new System.IO.Compression.GZipStream(await export.Content.ReadAsStreamAsync(), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(stream);
        var evidence = await reader.ReadToEndAsync();
        Assert.Contains("manifest", evidence); Assert.Contains("Decisions", evidence);
    }
    [Fact]
    public async Task Speed_changes_are_acknowledged_by_the_server_without_stepping_or_starting_playback()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.CanReplay);
        using var response = await fixture.Client.PostAsJsonAsync($"/api/replay/jobs/{job.Id}/commands", new HistoricalReplayCommand("setSpeed", 1));
        response.EnsureSuccessStatusCode();
        var view = (await response.Content.ReadFromJsonAsync<HistoricalReplayView>())!;
        Assert.True(view.Paused); Assert.Null(view.SimulatedTimeUtc);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("timing").GetProperty("requestedSpeed").GetDouble());
    }
    [Fact]
    public async Task Speed_change_wakes_a_slow_playback_wait_and_pause_interrupts_it()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.CanReplay);
        using var play = await fixture.Client.PostAsJsonAsync($"/api/replay/jobs/{job.Id}/commands", new HistoricalReplayCommand("play", 1)); play.EnsureSuccessStatusCode();
        var first = await fixture.Wait(job.Id, view => view.SimulatedTimeUtc is not null);
        await fixture.Command(job.Id, "pause"); var paused = await fixture.View(job.Id);
        await Task.Delay(100); Assert.Equal(paused.SimulatedTimeUtc, (await fixture.View(job.Id)).SimulatedTimeUtc);
        using var resume = await fixture.Client.PostAsJsonAsync($"/api/replay/jobs/{job.Id}/commands", new HistoricalReplayCommand("play", 1)); resume.EnsureSuccessStatusCode();
        using var speed = await fixture.Client.PostAsJsonAsync($"/api/replay/jobs/{job.Id}/commands", new HistoricalReplayCommand("setSpeed", 100000)); speed.EnsureSuccessStatusCode();
        var done = await fixture.Wait(job.Id, view => view.Complete);
        Assert.True(done.SimulatedTimeUtc > first.SimulatedTimeUtc);
    }
    [Fact]
    public async Task Health_distinguishes_failed_requests_from_empty_series_and_retry_only_fetches_failures()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Handler.Data.TryRemove(12657154, out var original); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        var health = (await fixture.Client.GetFromJsonAsync<ReplayDataHealth>($"/api/replay/jobs/{job.Id}/data-health"))!;
        Assert.Equal(1, health.Failed); Assert.Contains(health.Instruments, row => row.Error == "INSTRUMENT_OR_DATA_UNAVAILABLE");
        var requests = fixture.Handler.Requests.Count;
        fixture.Handler.Data[12657154] = System.Text.Encoding.UTF8.GetBytes("{\"status\":\"success\",\"data\":{\"candles\":[]}}");
        await fixture.Command(job.Id, "retryFailed"); await fixture.Wait(job.Id, view => view.Job.PreparationComplete && view.Job.Attempt == 2);
        Assert.Equal(requests + 1, fixture.Handler.Requests.Count);
        health = (await fixture.Client.GetFromJsonAsync<ReplayDataHealth>($"/api/replay/jobs/{job.Id}/data-health"))!;
        Assert.Equal(0, health.Failed); Assert.Equal(1, health.Empty);
    }
    [Fact]
    public async Task Complete_journal_pages_and_gate_evidence_survive_restart_and_maximum_speed_matches_manual_steps()
    {
        await using var fixture = new HistoricalApiFixture();
        var catalogue = Trading.Data.HistoricalAcquisitionStore.Read<Trading.Data.HistoricalCatalogDocument>(fixture.Catalog)!;
        var call = catalogue.Manifest.Instruments.Single(item => item.Role == Trading.Domain.HistoricalInstrumentRole.Call);
        var extra = Enumerable.Range(0, 12).Select(i => call with { Id = $"research-call-{i}", InstrumentToken = 9000000 + i, Strike = 50000 + i * 100m }).ToArray();
        foreach (var option in extra) fixture.Handler.Data[option.InstrumentToken] = fixture.Handler.Data[call.InstrumentToken];
        File.WriteAllText(fixture.Catalog, System.Text.Json.JsonSerializer.Serialize(catalogue with { Manifest = catalogue.Manifest with { Instruments = catalogue.Manifest.Instruments.Concat(extra).ToArray() } }, Trading.Data.KiteCandleImporter.JsonOptions));
        fixture.Start();
        var job = await fixture.Prepare(); var ready = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        var baseline = ready.PaperRunId!;
        for (var i = 0; i < 100; i++)
        {
            await fixture.Command(job.Id, "step");
            if ((await fixture.View(job.Id)).Complete) break;
        }
        await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        var first = (await fixture.Client.GetFromJsonAsync<ReplayEventPage>($"/api/replay/jobs/{job.Id}/events?runId={Uri.EscapeDataString(baseline)}&limit=3"))!;
        Assert.Equal(3, first.Rows.Count); Assert.NotNull(first.NextBeforeSequence);
        var second = (await fixture.Client.GetFromJsonAsync<ReplayEventPage>($"/api/replay/jobs/{job.Id}/events?runId={Uri.EscapeDataString(baseline)}&limit=3&before={first.NextBeforeSequence}"))!;
        Assert.DoesNotContain(second.Rows, row => first.Rows.Any(other => other.Sequence == row.Sequence));
        using var evidence = System.Text.Json.JsonDocument.Parse(await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/events/{first.Rows[0].Sequence}?runId={Uri.EscapeDataString(baseline)}"));
        Assert.True(evidence.RootElement.GetProperty("gates").GetArrayLength() > 0);
        await fixture.Command(job.Id, "restart"); var next = (await fixture.View(job.Id)).PaperRunId!;
        await fixture.Command(job.Id, "run"); await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        using var comparison = System.Text.Json.JsonDocument.Parse(await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/compare?left={Uri.EscapeDataString(baseline)}&right={Uri.EscapeDataString(next)}"));
        Assert.Equal(0, comparison.RootElement.GetProperty("differences").GetArrayLength());
        var reportRun = (await fixture.View(job.Id)).Job.Run;
        var result = await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/reports/{reportRun}/results.json");
        using var report = System.Text.Json.JsonDocument.Parse(result);
        Assert.True(report.RootElement.GetProperty("Sessions")[0].GetProperty("Decisions").GetArrayLength() > 200);
        await fixture.Restart();
        var recovered = (await fixture.Client.GetFromJsonAsync<ReplayEventPage>($"/api/replay/jobs/{job.Id}/events?runId={Uri.EscapeDataString(baseline)}&limit=3"))!;
        Assert.Equal(first.Rows.Select(row => row.Sequence), recovered.Rows.Select(row => row.Sequence));
    }
}
