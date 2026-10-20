using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Trading.Data;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Live;

public sealed class LivePaperApiTests
{
    [Fact]
    public async Task Stored_quote_source_replays_through_http_into_separate_durable_results_without_changing_source()
    {
        var root = Path.Combine(Path.GetTempPath(), "live-api-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteQuoteStore(Path.Combine(root, "market.sqlite"));
        var manifest = QuoteSessionTests.Manifest();
        store.Create(manifest.Id, manifest.CreatedAtUtc, JsonSerializer.Serialize(manifest, QuoteTradingSession.Json));
        store.Append(QuoteSessionTests.Lifecycle());
        var source = store.Read(manifest.Id).Select(item => Convert.ToHexString(item.Payload)).ToArray();
        await using (var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development").ConfigureServices(services => services.PostConfigure<LivePaperOptions>(options =>
                { options.RootDirectory = root; options.ConnectOnStartup = false; }))))
        {
            using var client = host.CreateClient();
            var status = await client.GetFromJsonAsync<LivePaperView>("/api/live/status", QuoteTradingSession.Json);
            Assert.Equal("Stopped", status!.Status); Assert.False(status.AutoEnabled);
            for (var i = 0; i < 2; i++)
            {
                using var response = await client.PostAsJsonAsync("/api/live/replays", new QuoteReplayRequest(manifest.Id));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var replay = await response.Content.ReadFromJsonAsync<QuoteReplayResult>(QuoteTradingSession.Json);
                Assert.Equal("SYNTHETIC TEST", replay!.Mode);
                Assert.Equal(27.10m, Assert.Single(replay.Result.Paper.Trades).NetPnl);
            }
            using var blocked = await client.PostAsJsonAsync("/api/live/commands", new QuoteControl("manual", "id", "call-100"));
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        }
        Assert.Equal(source, store.Read(manifest.Id).Select(item => Convert.ToHexString(item.Payload)).ToArray());
        Assert.Equal(2, new SqliteQuoteStore(Path.Combine(root, "quote-replays.sqlite")).Runs().Count);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
    }

    [Fact]
    public async Task Status_reports_free_disk_and_no_archive_fault_before_any_recording_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "live-api-" + Guid.NewGuid().ToString("N"));
        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development").ConfigureServices(services => services.PostConfigure<LivePaperOptions>(options =>
                { options.RootDirectory = root; options.ConnectOnStartup = false; options.ArchiveClosedDays = false; })));
        using var client = host.CreateClient();

        var status = await client.GetFromJsonAsync<LivePaperView>("/api/live/status", QuoteTradingSession.Json);

        Assert.Equal(0, status!.MarketRecordingBytes);
        Assert.True(status.FreeDiskBytes > 0);
        Assert.Null(status.ArchiveError);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Recordings_in_per_date_files_replay_identically_before_and_after_the_day_is_archived()
    {
        var root = Path.Combine(Path.GetTempPath(), "live-api-" + Guid.NewGuid().ToString("N"));
        var set = new MarketStoreSet(root);
        var oldest = QuoteSessionTests.Manifest() with { Id = "live-20261019-a" };
        foreach (var (date, id) in new[] { (new DateOnly(2026, 10, 19), oldest.Id), (new DateOnly(2026, 10, 20), "live-20261020-b"), (new DateOnly(2026, 10, 21), "live-20261021-c") })
        {
            var manifest = oldest with { Id = id };
            var store = set.ForDate(date);
            store.Create(id, manifest.CreatedAtUtc, JsonSerializer.Serialize(manifest, QuoteTradingSession.Json));
            var events = QuoteSessionTests.Lifecycle().Select(item => item with { RunId = id }).ToArray();
            store.Append(events); store.Checkpoint(id, events[^1].Sequence, "{}", true);
        }
        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development").ConfigureServices(services => services.PostConfigure<LivePaperOptions>(options =>
                { options.RootDirectory = root; options.ConnectOnStartup = false; options.ArchiveClosedDays = false; })));
        using var client = host.CreateClient();
        async Task<QuoteReplayResult> Replay()
        {
            using var response = await client.PostAsJsonAsync("/api/live/replays", new QuoteReplayRequest(oldest.Id));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<QuoteReplayResult>(QuoteTradingSession.Json))!;
        }

        var raw = await Replay();
        Assert.Equal(["market-2026-10-19.sqlite"], set.ArchiveClosedDays());
        var archived = await Replay();

        Assert.Equal(27.10m, Assert.Single(raw.Result.Paper.Trades).NetPnl);
        Assert.Equal(JsonSerializer.Serialize(raw.Result, QuoteTradingSession.Json), JsonSerializer.Serialize(archived.Result, QuoteTradingSession.Json));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
