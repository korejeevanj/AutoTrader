using System.Text.Json;
using Trading.Domain;

namespace Trading.Data;

// Keep the persisted shape compatible with acquisition.json written by earlier hosts.
public sealed record FrozenAcquisition(HistoricalAcquisitionPlan Plan, string ReplayJson, string EngineJson);
public sealed record HistoricalAcquisitionAttempt(int Number, bool Refresh = false, IReadOnlySet<string>? Refetch = null);
public sealed record HistoricalInstrumentAcquisition(string Id, long Token, string Role, string State,
    int Rows, bool Cached, string? Error);
public sealed record HistoricalAcquisitionProgress(IReadOnlyList<HistoricalInstrumentAcquisition> Instruments,
    int Finished, bool Validating = false);

/// <summary>
/// Owns a job's frozen inputs, download pins and immutable attempt publication.
/// The host supplies downloads and observes progress; it never coordinates their files.
/// One acquisition worker must own a revision at a time.
/// </summary>
public sealed class HistoricalAcquisitionRevision
{
    private readonly HistoricalAcquisitionStore store;
    private readonly string jobRoot;
    private readonly DateOnly date;
    public FrozenAcquisition? Frozen { get; private set; }

    public HistoricalAcquisitionRevision(HistoricalAcquisitionStore store, string jobId, DateOnly date)
    {
        if (!Guid.TryParseExact(jobId, "N", out _)) throw new ArgumentException("Invalid historical job identity.", nameof(jobId));
        this.store = store; this.date = date;
        jobRoot = Path.Combine(store.JobsRoot, jobId);
        Frozen = HistoricalAcquisitionStore.Read<FrozenAcquisition>(Path.Combine(jobRoot, "acquisition.json"));
    }

    public async Task<string> AcquireAsync(FrozenAcquisition inputs, HistoricalAcquisitionAttempt attempt,
        Func<HistoricalInstrument, CancellationToken, Task<HistoricalCacheEntry>> download,
        Func<HistoricalAcquisitionProgress, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt.Number, 1);
        if (Frozen is null)
        {
            await HistoricalAcquisitionStore.AtomicJsonAsync(Path.Combine(jobRoot, "acquisition.json"), inputs, cancellationToken);
            Frozen = inputs;
        }
        var manifest = Frozen.Plan.Manifest;
        KiteCandleImporter.ValidateManifest(manifest);
        var pinsPath = Path.Combine(jobRoot, "download-bindings.json");
        var pins = HistoricalAcquisitionStore.Read<Dictionary<string, HistoricalCacheEntry>>(pinsPath) ?? new(StringComparer.Ordinal);
        var folder = Path.Combine(jobRoot, "attempt-" + attempt.Number);
        try { Directory.CreateDirectory(folder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new HistoricalStorageException(error); }
        var instruments = manifest.Instruments.OrderBy(item => item.Role switch
            { HistoricalInstrumentRole.CashIndex => 0, HistoricalInstrumentRole.Futures => 1, HistoricalInstrumentRole.Constituent => 2, _ => 3 })
            .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var rows = instruments.Select(item => new HistoricalInstrumentAcquisition(item.Id, item.InstrumentToken,
            item.Role.ToString(), "Waiting", 0, false, null)).ToArray();
        await progress(new(Array.AsReadOnly(rows.ToArray()), 0));
        for (var i = 0; i < instruments.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instrument = instruments[i];
            try
            {
                if (instrument.ProviderMappingVerified == false) throw new KiteDataException("HISTORICAL_KITE_MAPPING_UNVERIFIED");
                HistoricalCacheEntry? cache = null;
                if (attempt.Refetch?.Contains(instrument.Id) == true)
                    cache = await download(instrument, cancellationToken);
                else if (pins.TryGetValue(instrument.Id, out var pinned) && (attempt.Refetch is not null || pinned.Complete
                    || instrument.Role is HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put))
                {
                    if (!File.Exists(pinned.RawFile) || KiteCandleImporter.Hash(await File.ReadAllBytesAsync(pinned.RawFile, cancellationToken)) != pinned.Sha256)
                        throw new KiteDataException("PINNED_SOURCE_CORRUPT");
                    cache = pinned;
                }
                else if (!attempt.Refresh && !pins.ContainsKey(instrument.Id))
                    cache = await store.CachedAsync(instrument, date, manifest.MarketOpen, manifest.MarketClose, cancellationToken);
                var hit = cache is not null && attempt.Refetch?.Contains(instrument.Id) != true;
                cache ??= await download(instrument, cancellationToken);
                // Bind the exact response before materializing/publishing progress. Recovery
                // must never choose a newer shared cache entry after either later step fails.
                pins[instrument.Id] = cache;
                await HistoricalAcquisitionStore.AtomicJsonAsync(pinsPath, pins, cancellationToken);
                try { File.Copy(cache.RawFile, Path.Combine(folder, instrument.File), overwrite: false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new HistoricalStorageException(error); }
                rows[i] = rows[i] with { State = cache.Rows == 0 ? "Unavailable" : cache.Complete ? "Complete" : "Partial",
                    Rows = cache.Rows, Cached = hit, Error = cache.Rows == 0 ? "EMPTY_RESPONSE" : null };
            }
            catch (KiteDataException error) when (error.Code is not ("CREDENTIALS_MISSING" or "AUTHENTICATION_REQUIRED" or "ACCESS_DENIED" or "PINNED_SOURCE_CORRUPT"))
            { rows[i] = rows[i] with { State = "Failed", Error = error.Code }; }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            { rows[i] = rows[i] with { State = "Failed", Error = "INVALID_PROVIDER_RESPONSE" }; }
            await progress(new(Array.AsReadOnly(rows.ToArray()), i + 1));
        }
        await progress(new(Array.AsReadOnly(rows.ToArray()), rows.Length, Validating: true));
        var path = Path.Combine(folder, "dataset.json");
        await HistoricalAcquisitionStore.AtomicJsonAsync(path, manifest, cancellationToken);
        return path;
    }
}
