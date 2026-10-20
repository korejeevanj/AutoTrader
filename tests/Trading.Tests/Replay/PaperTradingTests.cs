using System.Text.Json;
using Trading.Data;
using Trading.Domain;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Trading.Api;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class PaperTradingTests
{
    [Fact]
    public async Task A_failed_final_step_is_committed_before_full_day_retry_publishes_the_same_run()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.CanReplay);
        var path = Path.Combine(fixture.Root, "paper-replay.sqlite");
        void Execute(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        Execute("CREATE TRIGGER fail_final BEFORE UPDATE ON paper_runs WHEN json_extract(NEW.snapshot_json,'$.Complete')=1 BEGIN SELECT RAISE(FAIL,'test final checkpoint failure'); END;");
        await fixture.Command(job.Id, "run");
        var failed = await fixture.Wait(job.Id, view => view.Job.State == "Failed");
        Assert.True(failed.Complete);
        Assert.True(failed.JournalPending);
        Assert.False(failed.Job.ReportsAvailable);
        Execute("DROP TRIGGER fail_final;");
        await fixture.Command(job.Id, "run");
        var complete = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        Assert.Equal(failed.PaperRunId, complete.PaperRunId);
        Assert.Equal(failed.SimulatedTimeUtc, complete.SimulatedTimeUtc);
        Assert.False(complete.JournalPending);
        Assert.Single(complete.Trades);

        await fixture.Restart(); await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        Assert.True(recovered.Complete);
        Assert.True(recovered.Job.ReportsAvailable);
        Assert.Equal(complete.PaperRunId, recovered.PaperRunId);
        Assert.Equal(complete.SimulatedTimeUtc, recovered.SimulatedTimeUtc);
    }

    [Fact]
    public async Task Engine_paper_lifecycle_is_visible_durable_and_recovers_paused_without_duplicate_fills()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.CanReplay);
        for (var i = 0; i < 17; i++) await fixture.Command(job.Id, "step");
        var before = await fixture.View(job.Id);
        Assert.Equal("Strategy", before.PlaybackKind); Assert.NotNull(before.PaperRunId);
        Assert.Single(before.Paper!.Positions); Assert.Equal(1, before.Paper.AcceptedAttempts);
        var log = await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/paper-log?limit=1000");
        Assert.Contains("HYPOTHETICAL_FILLED", log); Assert.Contains("STRICT_ENGINE", log);
        await fixture.Command(job.Id, "pauseEntries");
        await fixture.Restart(); await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay || view.Job.State == "Failed");
        Assert.True(recovered.Job.CanReplay, recovered.Job.Message);
        Assert.Equal(before.SimulatedTimeUtc, recovered.SimulatedTimeUtc); Assert.Equal(before.PaperRunId, recovered.PaperRunId);
        Assert.True(recovered.Paused); Assert.True(recovered.Paper!.EntriesPaused); Assert.Single(recovered.Paper.Positions);
        Assert.Equal(before.Paper.Cash, recovered.Paper.Cash);
        await fixture.Command(job.Id, "run");
        var closed = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable || view.Job.State == "Failed");
        Assert.True(closed.Job.ReportsAvailable, closed.Job.Message);
        Assert.Empty(closed.Paper!.Positions); Assert.Single(closed.Trades);
        Assert.Equal(-34m, closed.Paper.RealizedNetPnl); Assert.Equal(499966m, closed.Paper.Cash);
        var events = JsonSerializer.Deserialize<PaperJournalRow[]>(await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/paper-log?limit=1000"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Single(events, row => row.Action == "HYPOTHETICAL_FILLED");
        Assert.Single(events, row => row.Action == "HYPOTHETICAL_EXIT_FILLED");
    }

    [Fact]
    public void Paper_batch_transaction_is_idempotent_and_rejects_conflicting_evidence()
    {
        using var fixture = new ReplayFixture();
        var journal = new ReplayPaperJournal(Path.Combine(fixture.Root, "paper.sqlite"));
        journal.Begin("run", "inputs-v1");
        var time = new DateTimeOffset(2026,10,14,4,0,0,TimeSpan.Zero);
        PaperJournalEvent[] events = [new("event", time, "BANKNIFTY", "option", "CANDLE_RESEARCH", "HYPOTHETICAL_FILLED", "setup", "[]")];
        journal.Append("run", "batch", time, "{\"value\":1}", "{}", "[]", events);
        journal.Append("run", "batch", time, "{\"value\":1}", "{}", "[]", events);
        Assert.Single(journal.Read("run")); Assert.Equal(time, journal.Checkpoint("run")!.TimeUtc);
        Assert.Throws<InvalidDataException>(() => journal.Append("run", "batch", time, "{\"value\":2}", "{}", "[]", events));
        Assert.Throws<InvalidDataException>(() => journal.Begin("run", "different-inputs"));
    }

    [Fact]
    public async Task Entry_pause_before_first_tick_survives_api_restart()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); var prepared = await fixture.Wait(job.Id, view => view.Job.CanReplay);
        await fixture.Command(job.Id, "pauseEntries");
        await fixture.Restart(); await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay || view.Job.State == "Failed");
        Assert.True(recovered.Job.CanReplay, recovered.Job.Message);
        Assert.Equal(prepared.PaperRunId, recovered.PaperRunId); Assert.Null(recovered.SimulatedTimeUtc);
        Assert.True(recovered.Paper!.EntriesPaused);
        for (var i = 0; i < 17; i++) await fixture.Command(job.Id, "step");
        Assert.Empty((await fixture.View(job.Id)).Trades);
    }

    [Fact]
    public async Task A_failed_journal_commit_cannot_be_skipped_by_advancing_to_another_tick()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.CanReplay);
        for (var i = 0; i < 16; i++) await fixture.Command(job.Id, "step");
        var path = Path.Combine(fixture.Root, "paper-replay.sqlite");
        void Execute(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        Execute("CREATE TRIGGER fail_paper_batch BEFORE INSERT ON paper_batches BEGIN SELECT RAISE(FAIL,'test storage failure'); END;");
        using var failed = await fixture.Client.PostAsJsonAsync($"/api/replay/jobs/{job.Id}/commands", new HistoricalReplayCommand("step"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var uncommitted = await fixture.View(job.Id); Assert.True(uncommitted.Paused); Assert.Single(uncommitted.Trades);
        Execute("DROP TRIGGER fail_paper_batch;");
        await fixture.Command(job.Id, "step");
        var committed = await fixture.View(job.Id);
        Assert.Equal(uncommitted.SimulatedTimeUtc, committed.SimulatedTimeUtc);
        var rows = JsonSerializer.Deserialize<PaperJournalRow[]>(await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/paper-log?limit=1000"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Single(rows, row => row.Action == "HYPOTHETICAL_FILLED");
        await fixture.Command(job.Id, "step");
        Assert.True((await fixture.View(job.Id)).SimulatedTimeUtc > committed.SimulatedTimeUtc);
    }

    [Fact]
    public void Published_weight_parser_uses_symbol_column_not_uppercase_company_names()
    {
        var weights = PublishedBankInputsClient.ParseWeightArchive(PublishedBankInputsFixture.WeightArchive(), "Sep2026", new(2026,9,30));
        Assert.Equal(2, weights.Count); Assert.Equal(100m, weights.Sum(item => item.WeightPercent));
        Assert.DoesNotContain(weights, item => item.InstrumentId is "NSE:BANK" or "NSE:HDFC");
    }

    [Fact]
    public void Sparse_option_inventory_does_not_create_one_error_per_possible_minute()
    {
        using var fixture = new ReplayFixture(); File.Delete(Path.Combine(fixture.Root, "put.json"));
        var dataset = fixture.Load();
        Assert.Contains(dataset.Issues, issue => issue.InstrumentId == "put" && issue.Code == "NO_OPTION_CANDLES");
        Assert.DoesNotContain(dataset.Issues, issue => issue.InstrumentId == "put" && issue.Code == "MISSING_MINUTE");
        Assert.Equal("Unavailable", dataset.Coverage!.Single(row => row.InstrumentId == "put").State);
    }
}
