using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Trading.Api;
using Trading.Data;
using Xunit;

namespace Trading.Tests.Live;

public sealed class ResearchArchiveTests
{
    private sealed class StubKite(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request.RequestUri!.PathAndQuery));
        }
    }

    private static string Master() => string.Join("\n", new[]
    {
        "instrument_token,exchange_token,tradingsymbol,name,last_price,expiry,strike,tick_size,lot_size,instrument_type,segment,exchange",
        "260105,1017,NIFTY BANK,NIFTY BANK,0,,0,0.05,1,EQ,INDICES,NSE",
        "1,1,BANKNIFTY26OCT55000CE,BANKNIFTY,0,2026-10-27,55000,0.05,30,CE,NFO-OPT,NFO",
        "2,2,BANKNIFTY26OCT55000PE,BANKNIFTY,0,2026-10-27,55000,0.05,30,PE,NFO-OPT,NFO",
        "3,3,BANKNIFTY26OCT70000CE,BANKNIFTY,0,2026-10-27,70000,0.05,30,CE,NFO-OPT,NFO",
        "4,4,BANKNIFTY26NOV56000CE,BANKNIFTY,0,2026-11-24,56000,0.05,30,CE,NFO-OPT,NFO",
        "5,5,BANKNIFTY26DEC55000CE,BANKNIFTY,0,2026-12-29,55000,0.05,30,CE,NFO-OPT,NFO",
    }) + "\n";

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (ResearchArchive Archive, StubKite Stub, string Root) Build(Func<string, HttpResponseMessage>? candles = null)
    {
        var stub = new StubKite(path => path.EndsWith("/instruments", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Master()) }
            : candles?.Invoke(path) ?? Json("{\"status\":\"success\",\"data\":{\"candles\":[[\"2026-10-21T15:29:00+0530\",54990,55010,54980,55000,100,5000]]}}"));
        var kite = new KiteHistoricalClient(new HttpClient(stub), () => new("key", "token"), TimeProvider.System, TimeSpan.Zero, 1);
        var root = Path.Combine(Path.GetTempPath(), "research-archive-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ResearchArchiveOptions { Root = root });
        return (new ResearchArchive(kite, TimeProvider.System, options, new TestEnvironment()), stub, root);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static async Task<ArchiveStatus> Run(ResearchArchive archive, Func<CancellationToken, Task> work)
    {
        Assert.True(archive.TryStart("test", work, "t", CancellationToken.None));
        while (archive.Status.State == "Running") await Task.Delay(20);
        return archive.Status;
    }

    [Fact]
    public async Task Option_day_archives_only_the_nearest_expiries_inside_the_strike_band_with_hashes_and_is_idempotent()
    {
        var (archive, stub, root) = Build();
        var day = new DateOnly(2026, 10, 21);

        var first = await Run(archive, token => archive.ArchiveOptionDayAsync(day, token));

        Assert.Equal("Completed", first.State);
        var folder = Path.Combine(root, "option-candles", "2026-10-21");
        Assert.Equal(new[] { "1.json", "2.json", "4.json" }, Directory.GetFiles(folder, "?.json").Select(file => Path.GetFileName(file)!).Order().ToArray()); // not the 70000 strike or the December expiry
        Assert.True(archive.OptionDayComplete(day));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        foreach (var entry in manifest.RootElement.GetProperty("entries").EnumerateArray())
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, entry.GetProperty("file").GetString()!)))), entry.GetProperty("sha256").GetString());
        Assert.Contains(stub.Requests, request => request.Contains("oi=1", StringComparison.Ordinal));

        var requestsBefore = stub.Requests.Count;
        var second = await Run(archive, token => archive.ArchiveOptionDayAsync(day, token));
        Assert.Equal(3, second.Skipped);
        Assert.Equal(requestsBefore + 2, stub.Requests.Count); // only the master and the index close are fetched again
    }

    [Fact]
    public async Task A_failed_request_is_reported_and_the_day_is_not_marked_complete()
    {
        var (archive, _, root) = Build(path => path.Contains("/historical/2/", StringComparison.Ordinal) ? Json("{}", HttpStatusCode.BadRequest)
            : Json("{\"status\":\"success\",\"data\":{\"candles\":[[\"2026-10-21T15:29:00+0530\",54990,55010,54980,55000,100,5000]]}}"));

        var status = await Run(archive, token => archive.ArchiveOptionDayAsync(new(2026, 10, 21), token));

        Assert.Equal("CompletedWithFailures", status.State);
        Assert.Equal(1, status.Failed);
        Assert.False(archive.OptionDayComplete(new(2026, 10, 21)));
        Assert.True(File.Exists(Path.Combine(root, "option-candles", "2026-10-21", "1.json")));
    }

    [Fact]
    public async Task Index_history_is_fetched_in_sixty_day_windows_per_symbol_and_a_second_job_is_refused_while_one_runs()
    {
        var gate = new TaskCompletionSource();
        var (archive, stub, root) = Build();
        var from = new DateOnly(2026, 1, 1); var to = new DateOnly(2026, 4, 10);   // 100 days: two windows

        var started = archive.TryStart("index", async token => { await gate.Task; await archive.FetchIndexHistoryAsync(from, to, ["NIFTY BANK"], token); }, "t", CancellationToken.None);
        Assert.True(started);
        Assert.False(archive.TryStart("other", _ => Task.CompletedTask, "t", CancellationToken.None));
        gate.SetResult();
        while (archive.Status.State == "Running") await Task.Delay(20);

        Assert.Equal("Completed", archive.Status.State);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "index-minute", "NIFTY_BANK")).Length);
        Assert.Equal(2, stub.Requests.Count(request => request.Contains("/historical/260105/minute", StringComparison.Ordinal)));
    }
}
