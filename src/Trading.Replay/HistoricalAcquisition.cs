using System.Text.Json;
using Trading.Data;
using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

/// <summary>The exchange session selected for a historical day, before any data is requested.</summary>
public sealed record AcquisitionSession(string Status, string Message, TimeOnly Open, TimeOnly Close);

/// <summary>
/// Freezes and acquires the inputs for one historical day. The host supplies the
/// sources, clock and an observer for progress; it never coordinates catalogue,
/// calendar, download or cache files itself.
/// </summary>
public sealed class HistoricalAcquisition(HistoricalAcquisitionStore store, IHistoricalSources sources, TimeProvider clock,
    string configurationPath, bool acquirePublishedBankInputs)
{
    public async Task<AcquisitionOutcome> PrepareAsync(AcquisitionRequest request, IAcquisitionObserver observer, CancellationToken token)
    {
        var day = request.Date;
        var revision = new HistoricalAcquisitionRevision(store, request.JobId, day);
        var frozen = revision.Frozen;
        var session = frozen is null ? Session(day) : new AcquisitionSession(request.RecordedCalendarStatus, "Recovering the frozen historical session.",
            frozen.Plan.Manifest.MarketOpen, frozen.Plan.Manifest.MarketClose);
        if (session.Status == "MarketClosed") return new AcquisitionOutcome.MarketClosed(session);
        var attempt = request.PreviousAttempt + 1;
        await observer.Started(session, attempt);
        var plan = frozen?.Plan ?? store.Resolve(day, session.Open, session.Close);
        if (plan is null)
        {
            var index = IndexFallback;
            var cache = request.Refresh && attempt == 1 ? null : await store.CachedAsync(index, day, session.Open, session.Close, token);
            cache ??= await Download(index, day, session, observer, token);
            if (cache.Rows == 0) return new AcquisitionOutcome.IndexUnavailable(session, attempt, index);
            await observer.PricePreviewReady(await Preview(day, session, index, cache, token), token);
            try
            {
                await observer.BeforeContractLookup(token);
                plan = await sources.ContractsAsync(day, session.Open, session.Close, token);
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException or FormatException)
            { plan = null; }
            if (plan is null) return new AcquisitionOutcome.PricePreviewOnly(session, attempt);
        }
        if (frozen is null)
        {
            var replaySettings = ReplayConfiguration.FromJson(await File.ReadAllTextAsync(configurationPath, token));
            var calendarEvidence = store.Calendar(day.Year);
            plan = plan with { Manifest = plan.Manifest with { CalendarVerified = calendarEvidence?.Verified == true,
                CalendarSource = calendarEvidence?.Source, CalendarPublicationEvidence = calendarEvidence?.PublicationEvidence,
                IncludesAllOptions = plan.AllOptionsCovered } };
            if (acquirePublishedBankInputs)
            {
                try { plan = await sources.BankInputsAsync(plan, day, replaySettings.MaximumWeightSnapshotAgeDays, token); }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or FormatException)
                { plan = plan with { Warnings = plan.Warnings.Append("PUBLISHED_BANK_INPUTS_UNAVAILABLE").ToArray() }; }
            }
            var enginePath = Path.GetFullPath(replaySettings.EngineProfile, Path.GetDirectoryName(configurationPath)!);
            var engineSettings = EngineConfiguration.FromJson(await File.ReadAllTextAsync(enginePath, token));
            frozen = new(plan, replaySettings.ToJson(), engineSettings.ToJson());
        }
        var manifestPath = await revision.AcquireAsync(frozen, new(attempt, request.Refresh, request.RetryOnlyFailed),
            (instrument, cancellation) => Download(instrument, day, session, observer, cancellation), progress => observer.Progress(progress, plan), token);
        return new AcquisitionOutcome.Acquired(session, attempt, frozen, plan, manifestPath);
    }

    private static HistoricalInstrument IndexFallback { get; } = new() { Id = "NSE:NIFTY BANK", IndexId = "BANKNIFTY", InstrumentToken = 260105,
        Role = HistoricalInstrumentRole.CashIndex, File = "index.json", Exchange = "NSE", DefinitionVersion = "index-token-config-v1" };

    private async Task<PricePreview> Preview(DateOnly day, AcquisitionSession session, HistoricalInstrument index, HistoricalCacheEntry cache,
        CancellationToken token)
    {
        var replay = ReplayConfiguration.FromJson(await File.ReadAllTextAsync(configurationPath, token)) with { EnableCandleResearch = false };
        var enginePath = Path.GetFullPath(replay.EngineProfile, Path.GetDirectoryName(configurationPath)!);
        var engine = EngineConfiguration.FromJson(await File.ReadAllTextAsync(enginePath, token));
        var parsed = KiteCandleImporter.Parse(await File.ReadAllBytesAsync(cache.RawFile, token), index.Id, replay.PublicationLag);
        var midnight = HistoricalAcquisitionStore.Midnight(day, "Asia/Kolkata");
        var manifest = new HistoricalDatasetManifest { DatasetId = $"BANKNIFTY-{day:yyyy-MM-dd}-price-only",
            TradingDates = [day], MarketOpen = session.Open, MarketClose = session.Close, Instruments = [index],
            Indices = [new("BANKNIFTY", "Bank Nifty", "NSE", "Asia/Kolkata", "Banks", "index-context-v1", midnight, null, false, new(false, false, false))] };
        var start = midnight.Add(session.Open.ToTimeSpan()); var end = midnight.Add(session.Close.ToTimeSpan());
        var candles = parsed.Candles.Where(bar => bar.StartUtc >= start && bar.StartUtc < end).ToArray();
        var expected = (int)(end - start).TotalMinutes; var returned = candles.Select(bar => bar.StartUtc).Distinct().Count();
        var dataset = new HistoricalDataset(manifest, candles, parsed.Issues, [parsed.Provenance], HistoricalAcquisitionStore.SafeName("price-only:" + cache.Sha256),
            [new(index.Id, "CashIndex", expected, returned, Math.Max(0, expected - returned), candles.Count(bar => bar.Volume == 0m), returned == expected ? "Complete" : "Sparse")]);
        return new(index, cache, replay, engine, dataset);
    }

    private async Task<HistoricalCacheEntry> Download(HistoricalInstrument instrument, DateOnly date, AcquisitionSession session,
        IAcquisitionObserver observer, CancellationToken token)
    {
        var close = session.Close;
        if (instrument.ContinuousTradingClose is { } continuous && continuous < close) close = continuous;
        var bytes = await sources.CandlesAsync(instrument, date, session.Open, close, attempt => observer.ProviderAttempt(instrument, attempt), token);
        var cached = await store.CacheAsync(instrument, date, session.Open, close, bytes, clock.GetUtcNow(), token);
        if (cached.RepairedCorruptSource) observer.CacheRepaired(cached);
        return cached;
    }

    /// <summary>Identifies the catalogue, calendar, weight and settings files a day's preparation would freeze.</summary>
    public string RequestFingerprint(DateOnly date)
    {
        var paths = new List<string> { configurationPath, Path.Combine(store.Root, "calendars", $"{date.Year}.json") };
        if (File.Exists(configurationPath))
        {
            try { paths.Add(Path.GetFullPath(ReplayConfiguration.FromJson(File.ReadAllText(configurationPath)).EngineProfile, Path.GetDirectoryName(configurationPath)!)); }
            catch (Exception error) when (error is JsonException or ArgumentException) { }
        }
        var files = paths.Concat(Directory.Exists(store.CatalogsRoot) ? Directory.EnumerateFiles(store.CatalogsRoot, "*.json") : [])
            .Concat(new[] { Path.Combine(store.Root, "weights"), Path.Combine(store.Root, "published-weights") }
                .Where(Directory.Exists).SelectMany(folder => Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)));
        return HistoricalAcquisitionStore.SafeName("historical-readiness-v1|" + date.ToString("yyyy-MM-dd") + string.Join("|", files.Order(StringComparer.Ordinal)
            .Select(path => File.Exists(path) ? KiteCandleImporter.Hash(File.ReadAllBytes(path)) : "missing")));
    }

    /// <summary>The inputs a job froze, or null before its first acquisition reached that step.</summary>
    public FrozenAcquisition? FrozenFor(string jobId, DateOnly date) => new HistoricalAcquisitionRevision(store, jobId, date).Frozen;

    public AcquisitionSession Session(DateOnly date)
    {
        var calendar = store.Calendar(date.Year);
        var special = calendar is { Verified: true } ? calendar.SpecialSessions.SingleOrDefault(session => session.Date == date) : null;
        var open = special?.Open ?? new TimeOnly(9, 15); var close = special?.Close ?? new TimeOnly(15, 30);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")).DateTime);
        if (date > today || clock.GetUtcNow() < HistoricalAcquisitionStore.Midnight(date, "Asia/Kolkata").Add(close.ToTimeSpan()))
            throw new ArgumentException("Choose a completed historical session; future/in-progress sessions are unavailable.");
        if (calendar is { Verified: true } && special is null
            && (calendar.ClosedDates.Contains(date) || date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday))
            return new("MarketClosed", "Exchange calendar confirms the selected day was closed.", open, close);
        return new(special is not null ? "SpecialSession" : calendar is { Verified: true } ? "TradingDay" : "CalendarUnverified",
            "Preparing selected date. Empty data will not be mislabelled as a confirmed market holiday.", open, close);
    }
}
