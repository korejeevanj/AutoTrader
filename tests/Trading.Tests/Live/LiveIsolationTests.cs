using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Trading.Api;
using Trading.Data;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Live;

public sealed class LiveIsolationTests
{
    private static WebApplicationFactory<Program> Host(string root, string mode) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.UseEnvironment("Development").UseSetting("AutoTrader:Mode", mode).ConfigureServices(services =>
            services.PostConfigure<LivePaperOptions>(options => { options.RootDirectory = root; options.ConnectOnStartup = false; options.ArchiveClosedDays = false; })));

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "live-isolation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Live_mode_serves_the_live_surface_and_none_of_the_research_surface()
    {
        var root = TempRoot();
        await using var host = Host(root, "Live");
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/dashboard/indices")).StatusCode);
        var readiness = await client.GetFromJsonAsync<LiveReadiness>("/api/live/readiness", QuoteTradingSession.Json);
        Assert.False(readiness!.Ready);
        Assert.Contains(readiness.Checks, check => check.Code == "LIVE_RUN_PREPARED" && !check.Ok);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/live/status")).StatusCode);
        using var replay = await client.PostAsJsonAsync("/api/live/replays", new QuoteReplayRequest("any"));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("REPLAYS_RUN_IN_THE_RESEARCH_INSTANCE", await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Research_mode_serves_the_cockpit_but_refuses_to_control_the_live_run()
    {
        var root = TempRoot();
        await using var host = Host(root, "Research");
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        foreach (var command in new[] { "start", "stop", "arm", "pause", "exit" })
        {
            using var response = await client.PostAsJsonAsync("/api/live/commands", new QuoteControl(command, command + "-1"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("LIVE_FEED_RUNS_IN_THE_LIVE_INSTANCE", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Default_mode_keeps_every_surface()
    {
        await using var host = Host(TempRoot(), "Full");
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/live/readiness")).StatusCode);
    }

    [Fact]
    public async Task Unknown_mode_fails_startup_instead_of_running_something_unintended()
    {
        await using var host = Host(TempRoot(), "Both");
        Assert.ThrowsAny<Exception>(() => host.CreateClient());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Paper_writer_keeps_decisions_in_their_own_file_and_serializes_off_the_caller_thread()
    {
        var folder = TempRoot();
        var paper = new SqliteQuoteStore(Path.Combine(folder, "paper.sqlite")); var decisions = new SqliteQuoteStore(Path.Combine(folder, "decisions-2026-10-21.sqlite"));
        paper.Create("run", DateTimeOffset.UnixEpoch, "{}"); decisions.Create("run", DateTimeOffset.UnixEpoch, "{}");
        var caller = Environment.CurrentManagedThreadId; int? jsonThread = null, decisionThread = null;
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var writer = new BufferedPaperCheckpoints(paper, decisions))
        {
            Assert.True(writer.TrySave("run", 5, () => { jsonThread = Environment.CurrentManagedThreadId; return "{\"ack\":true}"; }, () => committed.SetResult(),
                () => { decisionThread = Environment.CurrentManagedThreadId; return "{\"reason\":\"X\"}"; }));
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.NotEqual(caller, jsonThread); Assert.NotEqual(caller, decisionThread);
        Assert.Equal(5, Assert.Single(paper.Runs()).LastSequence);
        Assert.Empty(paper.Decisions("run"));
        Assert.Equal("X", Assert.Single(decisions.Decisions("run")).GetProperty("reason").GetString());
        SqliteConnection.ClearAllPools(); Directory.Delete(folder, true);
    }

    [Fact]
    public void Reads_neither_wait_for_the_writer_nor_break_on_a_file_written_before_the_admission_column()
    {
        var folder = TempRoot(); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "market.sqlite");
        using (var legacy = new SqliteConnection("Data Source=" + path))
        {
            legacy.Open(); using var command = legacy.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE quote_runs(id TEXT PRIMARY KEY, created_at TEXT NOT NULL, manifest TEXT NOT NULL, closed INTEGER NOT NULL DEFAULT 0, last_sequence INTEGER NOT NULL DEFAULT 0, checkpoint TEXT);
                CREATE TABLE market_events(run_id TEXT NOT NULL, sequence INTEGER NOT NULL, received_at TEXT NOT NULL, connection_id TEXT NOT NULL, kind TEXT NOT NULL, payload BLOB NOT NULL, PRIMARY KEY(run_id,sequence));
                INSERT INTO quote_runs(id,created_at,manifest) VALUES('old','2026-10-01T00:00:00+00:00','{}');
                INSERT INTO market_events VALUES('old',1,'2026-10-01T00:00:00+00:00','c','Binary',x'01');
                """;
            command.ExecuteNonQuery();
        }
        var store = new SqliteQuoteStore(path);
        Assert.Equal("old", Assert.Single(store.Runs()).Id);
        Assert.Single(store.Read("old"));

        var (writerConnection, hold) = HoldWriteLock(path);
        using (writerConnection)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal("old", Assert.Single(store.Runs()).Id);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), "a read must not queue behind the writer");
            hold.Rollback();
        }
        SqliteConnection.ClearAllPools(); Directory.Delete(folder, true);
    }

    private static (SqliteConnection, SqliteTransaction) HoldWriteLock(string path)
    {
        var connection = new SqliteConnection("Data Source=" + path); connection.Open();
        var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE quote_runs SET last_sequence=last_sequence"; command.ExecuteNonQuery();
        return (connection, transaction);
    }
}
