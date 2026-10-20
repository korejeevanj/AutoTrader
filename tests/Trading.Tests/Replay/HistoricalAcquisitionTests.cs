using Trading.Data;
using Trading.Domain;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class HistoricalAcquisitionTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private static readonly DateTimeOffset Later = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    private static HistoricalAcquisition Acquisition(string root, TimeProvider? clock = null, IHistoricalSources? sources = null,
        bool bankInputs = false) =>
        new(new HistoricalAcquisitionStore(root), sources ?? new InMemoryHistoricalSources(), clock ?? new FixedClock(Later),
            Path.Combine(root, "replay.json"), bankInputs);

    // The same dated catalogue, calendar and settings files a host would find under its acquisition root.
    private static void Seed(ReplayFixture files, InMemoryHistoricalSources sources)
    {
        var manifest = files.ReadManifest();
        manifest = manifest with { Instruments = manifest.Instruments.Select(item => item.Role == HistoricalInstrumentRole.Futures
            ? item with { Expiry = new(2026, 10, 29) } : item).ToArray() };
        Directory.CreateDirectory(Path.Combine(files.Root, "catalogs")); Directory.CreateDirectory(Path.Combine(files.Root, "calendars"));
        File.WriteAllText(Path.Combine(files.Root, "catalogs", "day.json"),
            System.Text.Json.JsonSerializer.Serialize(new HistoricalCatalogDocument("Synthetic complete contract catalogue", true, manifest), KiteCandleImporter.JsonOptions));
        File.WriteAllText(Path.Combine(files.Root, "calendars", "2026.json"), System.Text.Json.JsonSerializer.Serialize(new ExchangeCalendarDocument(
            2026, "Synthetic calendar fixture", true, [new(2026, 10, 8)], [new(ReplayFixture.Date, new(9, 15), new(9, 36))],
            manifest.CalendarPublicationEvidence), KiteCandleImporter.JsonOptions));
        File.WriteAllText(Path.Combine(files.Root, "engine.json"), files.Engine.ToJson());
        File.WriteAllText(Path.Combine(files.Root, "replay.json"), (files.Configuration with { EngineProfile = Path.Combine(files.Root, "engine.json") }).ToJson());
        foreach (var item in manifest.Instruments) sources.Candles[item.InstrumentToken] = File.ReadAllBytes(Path.Combine(files.Root, item.File));
    }

    private static AcquisitionRequest Request(string job = "0123456789abcdef0123456789abcdef", bool refresh = false) =>
        new(job, ReplayFixture.Date, "TradingDay", 0, refresh, null);

    [Fact]
    public async Task Without_a_catalogue_index_prices_become_a_replayable_preview_and_acquisition_stops_when_no_contract_file_exists()
    {
        using var files = new ReplayFixture(); var sources = new InMemoryHistoricalSources(); Seed(files, sources);
        Directory.Delete(Path.Combine(files.Root, "catalogs"), recursive: true);
        var observer = new RecordingObserver();

        var outcome = await Acquisition(files.Root, sources: sources).PrepareAsync(Request(), observer, CancellationToken.None);

        Assert.IsType<AcquisitionOutcome.PricePreviewOnly>(outcome);
        var preview = Assert.Single(observer.Previews);
        Assert.Equal("BANKNIFTY-2026-10-09-price-only", preview.Dataset.Manifest.DatasetId);
        Assert.Equal("Complete", preview.Dataset.Coverage!.Single().State);
        Assert.False(preview.Replay.EnableCandleResearch);
        Assert.Equal(1, observer.ContractLookups);
        Assert.Equal(["NSE:NIFTY BANK"], sources.Requested);
    }

    [Fact]
    public async Task Without_a_catalogue_an_empty_index_response_is_reported_as_unavailable_not_as_a_holiday()
    {
        using var files = new ReplayFixture(); var sources = new InMemoryHistoricalSources(); Seed(files, sources);
        Directory.Delete(Path.Combine(files.Root, "catalogs"), recursive: true);
        sources.Candles.Remove(260105);
        var observer = new RecordingObserver();

        var outcome = await Acquisition(files.Root, sources: sources).PrepareAsync(Request(), observer, CancellationToken.None);

        var unavailable = Assert.IsType<AcquisitionOutcome.IndexUnavailable>(outcome);
        Assert.Equal(1, unavailable.Attempt);
        Assert.Empty(observer.Previews);
    }

    [Fact]
    public async Task A_dated_exchange_contract_file_found_after_the_preview_continues_into_a_full_acquisition()
    {
        using var files = new ReplayFixture(); var sources = new InMemoryHistoricalSources(); Seed(files, sources);
        sources.Contracts = new HistoricalAcquisitionStore(files.Root).Resolve(ReplayFixture.Date, new(9, 15), new(9, 36));
        Directory.Delete(Path.Combine(files.Root, "catalogs"), recursive: true);
        var observer = new RecordingObserver();

        var outcome = await Acquisition(files.Root, sources: sources).PrepareAsync(Request(), observer, CancellationToken.None);

        Assert.IsType<AcquisitionOutcome.Acquired>(outcome);
        Assert.Single(observer.Previews);
        Assert.Equal(19, sources.Requested.Count); // the preview's index fallback plus the 18 catalogued instruments
    }

    [Fact]
    public async Task Unavailable_published_bank_inputs_are_frozen_as_a_warning_instead_of_failing_the_day()
    {
        using var files = new ReplayFixture(); var sources = new InMemoryHistoricalSources(); Seed(files, sources);
        sources.BankInputsFailure = new HttpRequestException("offline");

        var outcome = await Acquisition(files.Root, sources: sources, bankInputs: true).PrepareAsync(Request(), new RecordingObserver(), CancellationToken.None);

        Assert.Contains("PUBLISHED_BANK_INPUTS_UNAVAILABLE", Assert.IsType<AcquisitionOutcome.Acquired>(outcome).Frozen.Plan.Warnings);
    }

    [Fact]
    public async Task A_second_attempt_recovers_the_frozen_inputs_and_reuses_pinned_downloads()
    {
        using var files = new ReplayFixture(); var sources = new InMemoryHistoricalSources(); Seed(files, sources);
        var acquisition = Acquisition(files.Root, sources: sources);
        var first = Assert.IsType<AcquisitionOutcome.Acquired>(await acquisition.PrepareAsync(Request(), new RecordingObserver(), CancellationToken.None));
        File.WriteAllText(Path.Combine(files.Root, "replay.json"), (files.Configuration with { Presets = [new(), new() { Id = "later-edit" }],
            EngineProfile = Path.Combine(files.Root, "engine.json") }).ToJson());

        var second = Assert.IsType<AcquisitionOutcome.Acquired>(await acquisition.PrepareAsync(Request() with { PreviousAttempt = 1 }, new RecordingObserver(), CancellationToken.None));

        Assert.Equal(2, second.Attempt);
        Assert.Equal(first.Frozen.ReplayJson, second.Frozen.ReplayJson);
        Assert.Equal(18, sources.Requested.Count);
    }

    [Fact]
    public async Task The_frozen_acquisition_of_a_job_is_readable_only_after_it_was_frozen()
    {
        using var files = new ReplayFixture(); var sources = new InMemoryHistoricalSources(); Seed(files, sources);
        var acquisition = Acquisition(files.Root, sources: sources);
        Assert.Null(acquisition.FrozenFor(Request().JobId, ReplayFixture.Date));

        var acquired = Assert.IsType<AcquisitionOutcome.Acquired>(await acquisition.PrepareAsync(Request(), new RecordingObserver(), CancellationToken.None));

        Assert.Equal(acquired.Frozen.EngineJson, acquisition.FrozenFor(Request().JobId, ReplayFixture.Date)!.EngineJson);
    }

    [Fact]
    public async Task A_day_the_verified_calendar_marks_closed_stops_before_any_request()
    {
        using var files = new ReplayFixture(); var sources = new InMemoryHistoricalSources(); Seed(files, sources);
        var observer = new RecordingObserver();

        var outcome = await Acquisition(files.Root, sources: sources).PrepareAsync(
            Request() with { Date = new(2026, 10, 8) }, observer, CancellationToken.None);

        Assert.IsType<AcquisitionOutcome.MarketClosed>(outcome);
        Assert.Empty(observer.Started); Assert.Empty(sources.Requested);
    }

    [Fact]
    public async Task A_catalogued_day_is_frozen_and_every_instrument_is_downloaded()
    {
        using var files = new ReplayFixture(); var sources = new InMemoryHistoricalSources(); Seed(files, sources);
        var observer = new RecordingObserver();

        var outcome = await Acquisition(files.Root, sources: sources).PrepareAsync(Request(), observer, CancellationToken.None);

        var acquired = Assert.IsType<AcquisitionOutcome.Acquired>(outcome);
        Assert.Equal(1, acquired.Attempt);
        Assert.True(File.Exists(acquired.ManifestPath));
        Assert.Equal(18, sources.Requested.Count);
        Assert.Equal([1], observer.Started);
        Assert.True(observer.Progress.Last().Validating);
        Assert.Equal(files.Engine.ToJson(), acquired.Frozen.EngineJson);
    }

    [Fact]
    public void A_session_that_has_not_finished_cannot_be_prepared()
    {
        using var files = new ReplayFixture();
        var acquisition = Acquisition(files.Root, new FixedClock(new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero)));

        Assert.Throws<ArgumentException>(() => acquisition.Session(ReplayFixture.Date));
    }

    [Fact]
    public void Without_a_verified_calendar_the_regular_session_is_prepared_and_labelled_unverified()
    {
        using var files = new ReplayFixture();

        var session = Acquisition(files.Root).Session(ReplayFixture.Date);

        Assert.Equal(("CalendarUnverified", new TimeOnly(9, 15), new TimeOnly(15, 30)), (session.Status, session.Open, session.Close));
    }

    [Fact]
    public void The_request_fingerprint_changes_when_a_catalogue_changes_and_not_otherwise()
    {
        using var files = new ReplayFixture(); var acquisition = Acquisition(files.Root);
        var catalogs = Directory.CreateDirectory(Path.Combine(files.Root, "catalogs")).FullName;
        var first = acquisition.RequestFingerprint(ReplayFixture.Date);

        Assert.Equal(first, acquisition.RequestFingerprint(ReplayFixture.Date));
        File.WriteAllText(Path.Combine(catalogs, "day.json"), "{}");
        Assert.NotEqual(first, acquisition.RequestFingerprint(ReplayFixture.Date));
    }
}

internal sealed class InMemoryHistoricalSources : IHistoricalSources
{
    public readonly Dictionary<long, byte[]> Candles = [];
    public readonly List<string> Requested = [];
    public HistoricalAcquisitionPlan? Contracts { get; set; }
    public Exception? BankInputsFailure { get; set; }
    public Task<byte[]> CandlesAsync(HistoricalInstrument instrument, DateOnly date, TimeOnly open, TimeOnly close,
        Action<KiteRequestAttempt>? onAttempt, CancellationToken token)
    {
        Requested.Add(instrument.Id); onAttempt?.Invoke(new(1, 200, 0));
        return Task.FromResult(Candles.TryGetValue(instrument.InstrumentToken, out var raw) ? raw
            : System.Text.Encoding.UTF8.GetBytes("""{"status":"success","data":{"candles":[]}}"""));
    }
    public Task<HistoricalAcquisitionPlan?> ContractsAsync(DateOnly date, TimeOnly open, TimeOnly close, CancellationToken token) =>
        Task.FromResult(Contracts);
    public Task<HistoricalAcquisitionPlan> BankInputsAsync(HistoricalAcquisitionPlan plan, DateOnly date, int maximumSnapshotAgeDays,
        CancellationToken token) => BankInputsFailure is null ? Task.FromResult(plan) : Task.FromException<HistoricalAcquisitionPlan>(BankInputsFailure);
}

internal sealed class RecordingObserver : IAcquisitionObserver
{
    public readonly List<int> Started = [];
    public readonly List<HistoricalAcquisitionProgress> Progress = [];
    public readonly List<PricePreview> Previews = [];
    public int ContractLookups;
    Task IAcquisitionObserver.Started(AcquisitionSession session, int attempt) { Started.Add(attempt); return Task.CompletedTask; }
    Task IAcquisitionObserver.PricePreviewReady(PricePreview preview, CancellationToken token) { Previews.Add(preview); return Task.CompletedTask; }
    Task IAcquisitionObserver.BeforeContractLookup(CancellationToken token) { ContractLookups++; return Task.CompletedTask; }
    Task IAcquisitionObserver.Progress(HistoricalAcquisitionProgress progress, HistoricalAcquisitionPlan plan) { Progress.Add(progress); return Task.CompletedTask; }
    void IAcquisitionObserver.ProviderAttempt(HistoricalInstrument instrument, KiteRequestAttempt attempt) { }
    void IAcquisitionObserver.CacheRepaired(HistoricalCacheEntry entry) { }
}
