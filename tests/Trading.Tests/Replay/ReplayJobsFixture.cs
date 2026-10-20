using Trading.Data;
using Trading.Domain;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Replay;

internal static class HistoricalTestFiles
{
    // The same dated catalogue, calendar and settings files a host would find under its acquisition root.
    public static void Seed(ReplayFixture files, InMemoryHistoricalSources sources)
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
}

/// <summary>A job module over a temp acquisition root with in-memory sources; time stands after the session, timers are real.</summary>
internal sealed class ReplayJobsFixture : IAsyncDisposable
{
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero); }

    public ReplayFixture Files { get; } = new();
    public InMemoryHistoricalSources Sources { get; } = new();
    public ReplayJobs Jobs { get; private set; } = null!;
    private CancellationTokenSource stop = new();
    private Task running = Task.CompletedTask;

    public ReplayJobsFixture() { HistoricalTestFiles.Seed(Files, Sources); Start(); }

    private void Start()
    {
        var store = new HistoricalAcquisitionStore(Files.Root);
        var journal = new ReplayEvidenceJournal(Path.Combine(Files.Root, "paper-replay.sqlite"));
        var clock = new FixedClock();
        Jobs = new(store, new HistoricalAcquisition(store, Sources, clock, Path.Combine(Files.Root, "replay.json"), acquirePublishedBankInputs: false),
            Sources, journal, new ReplayOperationLog(journal), clock, new() { ArchiveCurrentMaster = false });
        stop = new(); running = Jobs.RunAsync(stop.Token);
    }

    /// <summary>Stops the host and starts a fresh module over the same files, as a server restart does.</summary>
    public async Task Restart() { await Stop(); Start(); }

    private async Task Stop()
    {
        stop.Cancel();
        try { await running; } catch (OperationCanceledException) { }
        await Jobs.DrainAsync(CancellationToken.None);
    }

    /// <summary>Waits for a snapshot that satisfies the predicate by listening for changes; there is no polling.</summary>
    public async Task<ReplayJobSnapshot> Until(string id, Func<ReplayJobSnapshot, bool> predicate, int seconds = 20)
    {
        var met = new TaskCompletionSource<ReplayJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(ReplayJobSnapshot snapshot) { if (snapshot.Job.Id == id && predicate(snapshot)) met.TrySetResult(snapshot); }
        Jobs.Changed += Observe;
        try
        {
            if (Jobs.Snapshot(id) is { } current) Observe(current);
            return await met.Task.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        catch (TimeoutException) { throw new TimeoutException("Replay job did not reach the expected state: " + Jobs.Snapshot(id)?.Job.State); }
        finally { Jobs.Changed -= Observe; }
    }

    public async ValueTask DisposeAsync() { await Stop(); Files.Dispose(); }
}
