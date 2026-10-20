using System.Text;
using Trading.Data;
using Trading.Domain;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class AcquisitionRevisionTests
{
    [Fact]
    public async Task Failed_refetch_pin_publication_keeps_the_prior_revision_and_earlier_attempt_unchanged()
    {
        using var fixture = new ReplayFixture();
        var store = new HistoricalAcquisitionStore(Path.Combine(fixture.Root, "acquisition"));
        var jobId = Guid.NewGuid().ToString("N");
        var manifest = fixture.ReadManifest();
        var acquisition = new FrozenAcquisition(new(manifest, true, "Synthetic dated catalogue", []),
            fixture.Configuration.ToJson(), fixture.Engine.ToJson());
        var requested = new List<string>();
        async Task<HistoricalCacheEntry> Download(HistoricalInstrument instrument, CancellationToken token)
        {
            requested.Add(instrument.Id);
            return await store.CacheAsync(instrument, ReplayFixture.Date, manifest.MarketOpen, manifest.MarketClose,
                await File.ReadAllBytesAsync(Path.Combine(fixture.Root, instrument.File), token),
                ReplayFixture.MetadataTime.AddSeconds(requested.Count), token);
        }
        var first = await new HistoricalAcquisitionRevision(store, jobId, ReplayFixture.Date).AcquireAsync(acquisition,
            new(1), Download, _ => Task.CompletedTask, CancellationToken.None);
        var original = KiteCandleImporter.Load(first, TimeSpan.Zero).Files.ToDictionary(item => item.InstrumentId, item => item.Sha256);
        var index = manifest.Instruments.Single(instrument => instrument.Id == "index");
        var source = Path.Combine(fixture.Root, index.File);
        await File.WriteAllTextAsync(source, (await File.ReadAllTextAsync(source)).Replace(",0,0]", ",0,1]", StringComparison.Ordinal));
        var pins = Path.Combine(store.JobsRoot, jobId, "download-bindings.json");
        try
        {
            File.SetAttributes(pins, FileAttributes.ReadOnly);
            await Assert.ThrowsAsync<HistoricalStorageException>(() => new HistoricalAcquisitionRevision(store, jobId, ReplayFixture.Date)
                .AcquireAsync(acquisition, new(2, Refetch: new HashSet<string>(StringComparer.Ordinal) { "index" }),
                    Download, _ => Task.CompletedTask, CancellationToken.None));
        }
        finally { File.SetAttributes(pins, FileAttributes.Normal); }

        var recovered = await new HistoricalAcquisitionRevision(store, jobId, ReplayFixture.Date).AcquireAsync(acquisition,
            new(3), Download, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(2, requested.Count(id => id == "index"));
        Assert.All(KiteCandleImporter.Load(recovered, TimeSpan.Zero).Files, file => Assert.Equal(original[file.InstrumentId], file.Sha256));
        Assert.All(KiteCandleImporter.Load(first, TimeSpan.Zero).Files, file => Assert.Equal(original[file.InstrumentId], file.Sha256));
    }

    [Fact]
    public async Task Interrupted_attempt_file_publication_keeps_the_response_pinned_before_any_instrument_progress_is_reported()
    {
        using var fixture = new ReplayFixture();
        var store = new HistoricalAcquisitionStore(Path.Combine(fixture.Root, "acquisition"));
        var jobId = Guid.NewGuid().ToString("N");
        var manifest = fixture.ReadManifest();
        var index = manifest.Instruments.Single(instrument => instrument.Id == "index");
        var acquisition = new FrozenAcquisition(new(manifest, true, "Synthetic dated catalogue", []),
            fixture.Configuration.ToJson(), fixture.Engine.ToJson());
        var original = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, index.File));
        var downloads = new List<string>();
        async Task<HistoricalCacheEntry> Download(HistoricalInstrument instrument, CancellationToken token)
        {
            downloads.Add(instrument.Id);
            return await store.CacheAsync(instrument, ReplayFixture.Date, manifest.MarketOpen, manifest.MarketClose,
                await File.ReadAllBytesAsync(Path.Combine(fixture.Root, instrument.File), token), ReplayFixture.MetadataTime, token);
        }
        var progress = new List<int>();
        var revision = new HistoricalAcquisitionRevision(store, jobId, ReplayFixture.Date);
        await Assert.ThrowsAsync<HistoricalStorageException>(() => revision.AcquireAsync(acquisition, new(1), Download, update =>
        {
            progress.Add(update.Finished);
            Directory.CreateDirectory(Path.Combine(store.JobsRoot, jobId, "attempt-1", index.File));
            return Task.CompletedTask;
        }, CancellationToken.None));
        Assert.Equal([0], progress);

        var newer = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original).Replace(",0,0]", ",0,1]", StringComparison.Ordinal));
        await store.CacheAsync(index, ReplayFixture.Date, manifest.MarketOpen, manifest.MarketClose, newer,
            ReplayFixture.MetadataTime.AddDays(1), CancellationToken.None);
        var result = await new HistoricalAcquisitionRevision(store, jobId, ReplayFixture.Date).AcquireAsync(acquisition,
            new(2), Download, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Single(downloads, id => id == "index");
        Assert.Equal(KiteCandleImporter.Hash(original), KiteCandleImporter.Load(result, TimeSpan.Zero).Files.Single(item => item.InstrumentId == "index").Sha256);
        Assert.False(File.Exists(Path.Combine(store.JobsRoot, jobId, "attempt-1", "dataset.json")));
    }

    [Fact]
    public async Task Interrupted_progress_recovers_frozen_inputs_and_pinned_responses_without_rebinding_to_newer_cache()
    {
        using var fixture = new ReplayFixture();
        var store = new HistoricalAcquisitionStore(Path.Combine(fixture.Root, "acquisition"));
        var jobId = Guid.NewGuid().ToString("N");
        var manifest = fixture.ReadManifest();
        var acquisition = new FrozenAcquisition(new(manifest, true, "Synthetic dated catalogue", []),
            fixture.Configuration.ToJson(), fixture.Engine.ToJson());
        var requested = new List<string>();
        async Task<HistoricalCacheEntry> Download(HistoricalInstrument instrument, CancellationToken token)
        {
            requested.Add(instrument.Id);
            return await store.CacheAsync(instrument, ReplayFixture.Date, manifest.MarketOpen, manifest.MarketClose,
                await File.ReadAllBytesAsync(Path.Combine(fixture.Root, instrument.File), token), ReplayFixture.MetadataTime, token);
        }
        var revision = new HistoricalAcquisitionRevision(store, jobId, ReplayFixture.Date);
        await Assert.ThrowsAsync<IOException>(() => revision.AcquireAsync(acquisition, new(1), Download,
            progress => progress.Finished == 1 ? Task.FromException(new IOException("Progress publication interrupted.")) : Task.CompletedTask,
            CancellationToken.None));
        Assert.Equal(["index"], requested);

        var index = manifest.Instruments.Single(instrument => instrument.Id == "index");
        var original = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, index.File));
        var newer = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original).Replace(",0,0]", ",0,1]", StringComparison.Ordinal));
        Assert.NotEqual(KiteCandleImporter.Hash(original), KiteCandleImporter.Hash(newer));
        await store.CacheAsync(index, ReplayFixture.Date, manifest.MarketOpen, manifest.MarketClose, newer,
            ReplayFixture.MetadataTime.AddDays(1), CancellationToken.None);

        var recovered = new HistoricalAcquisitionRevision(store, jobId, ReplayFixture.Date);
        Assert.Equal(acquisition.ReplayJson, recovered.Frozen!.ReplayJson);
        Assert.Equal(acquisition.EngineJson, recovered.Frozen.EngineJson);
        var result = await recovered.AcquireAsync(acquisition with { EngineJson = "new settings must not replace frozen inputs" },
            new(2), Download, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Single(requested, id => id == "index");
        var dataset = KiteCandleImporter.Load(result, TimeSpan.Zero);
        Assert.Equal(18, dataset.Manifest.Instruments.Count);
        Assert.Equal(KiteCandleImporter.Hash(original), dataset.Files.Single(item => item.InstrumentId == "index").Sha256);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(store.JobsRoot, jobId, "attempt-1", index.File)));
        Assert.Equal(acquisition.EngineJson, new HistoricalAcquisitionRevision(store, jobId, ReplayFixture.Date).Frozen!.EngineJson);
    }
}
