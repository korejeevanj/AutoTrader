using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trading.Api;
using Trading.Data;
using Xunit;

namespace Trading.Tests.Replay;

internal sealed class HistoricalApiFixture : IAsyncDisposable
{
    public ReplayFixture Files { get; }
    public string Root => Path.Combine(Files.Root, "acquisition");
    public string Catalog => Path.Combine(Root, "catalogs", "day.json");
    public string ReplayPath => Path.Combine(Files.Root, "replay.json");
    public string EnginePath => Path.Combine(Files.Root, "engine.json");
    public readonly FakeKiteHandler Handler = new();
    public HttpMessageHandler ExchangeHandler { get; set; } = new MissingExchangeArchive();
    public KiteCredentials Credentials = new("test-api-key", "daily-test-token");
    public TimeProvider Clock { get; set; } = new FixedClock();
    private WebApplicationFactory<Program>? factory;
    public HttpClient Client { get; private set; } = null!;
    public HistoricalReplayService ReplayService => factory!.Services.GetRequiredService<HistoricalReplayService>();
    public HistoricalApiFixture(params DateOnly[] dates)
    {
        Files = new(dates);
        Directory.CreateDirectory(Path.GetDirectoryName(Catalog)!);
        Directory.CreateDirectory(Path.Combine(Root, "calendars"));
        var manifest = Files.ReadManifest();
        manifest = manifest with { Instruments = manifest.Instruments.Select(item => item.Role == Trading.Domain.HistoricalInstrumentRole.Futures
            ? item with { Expiry = new(2026, 10, 29) } : item).ToArray() };
        File.WriteAllText(Catalog, JsonSerializer.Serialize(new HistoricalCatalogDocument("Synthetic complete contract catalogue", true, manifest), KiteCandleImporter.JsonOptions));
        File.WriteAllText(Path.Combine(Root, "calendars", "2026.json"), JsonSerializer.Serialize(new ExchangeCalendarDocument(2026, "Synthetic calendar fixture", true,
            [new(2026, 10, 8)], manifest.TradingDates.Select(date => new ExchangeSessionOverride(date, new(9,15), new(9,36))).ToArray(),
            manifest.CalendarPublicationEvidence), KiteCandleImporter.JsonOptions));
        File.WriteAllText(EnginePath, Files.Engine.ToJson());
        File.WriteAllText(ReplayPath, (Files.Configuration with { EngineProfile = EnginePath }).ToJson());
        foreach (var item in manifest.Instruments) Handler.Data[item.InstrumentToken] = File.ReadAllBytes(Path.Combine(Files.Root, item.File));
    }
    public void Start()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.PostConfigure<HistoricalReplayOptions>(options =>
            { options.RootDirectory = Root; options.ReplayConfigurationPath = ReplayPath; options.ArchiveCurrentMaster = false; options.AcquirePublishedBankInputs = false; });
            services.RemoveAll<KiteHistoricalClient>();
            services.AddSingleton(new KiteHistoricalClient(new HttpClient(Handler, disposeHandler: false), () => Credentials,
                TimeProvider.System, TimeSpan.Zero, maximumAttempts: 1));
            services.RemoveAll<TimeProvider>(); services.AddSingleton(Clock);
            services.RemoveAll<NseHistoricalContractClient>();
            services.AddSingleton(new NseHistoricalContractClient(new HttpClient(ExchangeHandler, disposeHandler: false)));
        }));
        Client = factory.CreateClient();
    }
    public async Task Restart()
    {
        Client.Dispose(); if (factory is not null) await factory.DisposeAsync(); factory = null; Start();
    }
    public async Task<HistoricalReplayJob> Prepare(bool refresh = false, DateOnly? date = null)
    {
        using var response = await Client.PostAsJsonAsync("/api/replay/jobs", new PrepareHistoricalReplayRequest(date ?? ReplayFixture.Date, refresh));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HistoricalReplayJob>())!;
    }
    public Task<HistoricalReplayView> View(string id) => GetView(id);
    private async Task<HistoricalReplayView> GetView(string id) => (await Client.GetFromJsonAsync<HistoricalReplayView>($"/api/replay/jobs/{id}"))!;
    public async Task<HistoricalReplayView> Wait(string id, Func<HistoricalReplayView, bool> predicate)
    {
        for (var i = 0; i < 400; i++)
        {
            var view = await View(id); if (predicate(view)) return view;
            await Task.Delay(25);
        }
        throw new TimeoutException("Replay job did not reach expected state: " + (await View(id)).Job.State);
    }
    public async Task Command(string id, string action)
    {
        using var response = await Client.PostAsJsonAsync($"/api/replay/jobs/{id}/commands", new HistoricalReplayCommand(action, 3600));
        response.EnsureSuccessStatusCode();
    }
    public async ValueTask DisposeAsync()
    { Client?.Dispose(); if (factory is not null) await factory.DisposeAsync(); Handler.Dispose(); ExchangeHandler.Dispose(); Files.Dispose(); }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero); }
    private sealed class MissingExchangeArchive : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal sealed class FakeKiteHandler : HttpMessageHandler
{
    public readonly ConcurrentDictionary<long, byte[]> Data = new();
    public readonly ConcurrentQueue<(string Method, string Path, string Query, string? Authorization, bool Cookie, string? Version)> Requests = new();
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Override;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue((request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query,
            request.Headers.Authorization?.ToString(), request.Headers.Contains("Cookie"), request.Headers.TryGetValues("X-Kite-Version", out var versions) ? versions.Single() : null));
        if (Override is not null) return Override(request, cancellationToken);
        var parts = request.RequestUri.AbsolutePath.Split('/');
        if (parts.Length == 5 && long.TryParse(parts[3], out var token) && Data.TryGetValue(token, out var raw))
        {
            var from = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["from"].ToString();
            using var document = JsonDocument.Parse(raw);
            var candles = document.RootElement.GetProperty("data").GetProperty("candles").EnumerateArray()
                .Where(row => row[0].GetString()!.StartsWith(from[..10], StringComparison.Ordinal)).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(new { status = "success", data = new { candles } })) });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
