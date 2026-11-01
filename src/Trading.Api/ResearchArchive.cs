using System.Text.Json;
using Trading.Data;

namespace Trading.Api;

public sealed class ResearchArchiveOptions
{
    public string Root { get; set; } = "../../data/research";
    // Research instance only: archive the day's option candles after the close, retrying until the evening.
    public bool AutoNightly { get; set; } = true;
    public int StrikeBandPercent { get; set; } = 12;
    public int ExpiriesToArchive { get; set; } = 2;
}

public sealed record ArchiveStatus(string State, string Kind, string? Detail, int Total, int Done, int Skipped, int Failed, string? LastError,
    DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc);

// Read-only research data: raw Kite candle responses saved byte-for-byte with a manifest (hash, size, status), resumable and idempotent.
// Expired option contracts cannot be fetched from Kite later, so live contracts are archived every evening to build our own history.
public sealed class ResearchArchive
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private readonly KiteHistoricalClient kite;
    private readonly TimeProvider clock;
    private readonly string root;
    private readonly SemaphoreSlim running = new(1, 1);
    private readonly ResearchArchiveOptions options;
    private ArchiveStatus status = new("Idle", "none", null, 0, 0, 0, 0, null, null, null);
    public ArchiveStatus Status => Volatile.Read(ref status);

    public ResearchArchive(KiteHistoricalClient kite, TimeProvider clock, Microsoft.Extensions.Options.IOptions<ResearchArchiveOptions> options, IHostEnvironment environment)
    {
        this.kite = kite; this.clock = clock; this.options = options.Value;
        root = Path.GetFullPath(this.options.Root, environment.ContentRootPath);
    }

    public string Root => root;
    public bool OptionDayComplete(DateOnly date) => File.Exists(Path.Combine(root, "option-candles", date.ToString("yyyy-MM-dd"), "complete.json"));

    private void Set(ArchiveStatus next) => Volatile.Write(ref status, next);

    // Starts a background run unless one is active. Returns false when busy.
    public bool TryStart(string kind, Func<CancellationToken, Task> work, string detail, CancellationToken stopping)
    {
        if (!running.Wait(0)) return false;
        Set(new("Running", kind, detail, 0, 0, 0, 0, null, clock.GetUtcNow(), null));
        _ = Task.Run(async () =>
        {
            try { await work(stopping); var s = Status; Set(s with { State = s.Failed == 0 ? "Completed" : "CompletedWithFailures", FinishedAtUtc = clock.GetUtcNow() }); }
            catch (OperationCanceledException) { Set(Status with { State = "Cancelled", FinishedAtUtc = clock.GetUtcNow() }); }
            catch (Exception error) when (error is KiteDataException or IOException or InvalidDataException or JsonException or HttpRequestException or KeyNotFoundException or InvalidOperationException)
            { Set(Status with { State = "Failed", LastError = error is KiteDataException k ? k.Code : error.GetType().Name + ":" + error.Message, FinishedAtUtc = clock.GetUtcNow() }); }
            finally { running.Release(); }
        }, CancellationToken.None);
        return true;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private async Task<bool> SaveAsync(string path, Func<Task<byte[]>> fetch, List<object> manifest, object meta)
    {
        if (File.Exists(path)) { Bump(skipped: 1); return true; }
        try
        {
            var bytes = await fetch();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var partial = path + ".tmp"; await File.WriteAllBytesAsync(partial, bytes); File.Move(partial, path, true);
            lock (manifest) manifest.Add(new { meta, file = Path.GetFileName(path), bytes = bytes.Length, sha256 = Sha(bytes), status = "ok" });
            Bump(done: 1); return true;
        }
        catch (KiteDataException error) when (error.Code is "INSTRUMENT_OR_DATA_UNAVAILABLE" or "RATE_LIMITED" or "PROVIDER_UNAVAILABLE")
        {
            lock (manifest) manifest.Add(new { meta, file = Path.GetFileName(path), status = error.Code });
            Bump(failed: 1, error: error.Code); return false;
        }
    }

    private void Bump(int done = 0, int skipped = 0, int failed = 0, string? error = null)
    {
        var s = Status; Set(s with { Done = s.Done + done, Skipped = s.Skipped + skipped, Failed = s.Failed + failed, LastError = error ?? s.LastError });
    }

    // All live Bank Nifty options of the nearest expiries within a band around the index close, minute candles with open interest.
    public async Task ArchiveOptionDayAsync(DateOnly date, CancellationToken token)
    {
        var master = KiteInstrumentMaster.Parse(await kite.MasterAsync(token), clock.GetUtcNow());
        var folder = Path.Combine(root, "option-candles", date.ToString("yyyy-MM-dd"));
        var open = date.ToDateTime(new TimeOnly(9, 15)); var close = date.ToDateTime(new TimeOnly(15, 30));
        var index = master.Instruments.Single(row => row.Exchange == "NSE" && row.TradingSymbol == "NIFTY BANK");
        var dayBytes = await kite.HistoricalRangeAsync(index.InstrumentToken, open, close, token);
        using var day = JsonDocument.Parse(dayBytes);
        var candles = day.RootElement.GetProperty("data").GetProperty("candles");
        if (candles.GetArrayLength() == 0) throw new InvalidDataException("NO_INDEX_CANDLES_FOR_DATE");
        var reference = candles[candles.GetArrayLength() - 1][4].GetDecimal();
        var all = master.Instruments.Where(row => row.Exchange == "NFO" && row.Name == "BANKNIFTY" && row.InstrumentType is "CE" or "PE" && row.Expiry >= date).ToArray();
        var expiries = all.Select(row => row.Expiry!.Value).Distinct().Order().Take(options.ExpiriesToArchive).ToHashSet();
        var band = reference * options.StrikeBandPercent / 100m;
        var targets = all.Where(row => expiries.Contains(row.Expiry!.Value) && Math.Abs(row.Strike - reference) <= band)
            .OrderBy(row => row.Expiry).ThenBy(row => row.Strike).ThenBy(row => row.InstrumentType).ToArray();
        Directory.CreateDirectory(folder);
        Set(Status with { Total = targets.Length, Detail = $"{date:yyyy-MM-dd}: {targets.Length} options, index close {reference}" });
        var manifest = new List<object>(); var failures = 0;
        foreach (var row in targets)
        {
            token.ThrowIfCancellationRequested();
            var ok = await SaveAsync(Path.Combine(folder, row.InstrumentToken + ".json"),
                () => kite.HistoricalRangeAsync(row.InstrumentToken, open, close, token, openInterest: true), manifest,
                new { row.InstrumentToken, row.TradingSymbol, row.InstrumentType, row.Strike, expiry = row.Expiry?.ToString("yyyy-MM-dd"), row.LotSize, row.TickSize });
            if (!ok) failures++;
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new { date = date.ToString("yyyy-MM-dd"), masterSha256 = master.Sha256,
            capturedAtUtc = clock.GetUtcNow(), indexClose = reference, bandPercent = options.StrikeBandPercent, requested = targets.Length, failures, entries = manifest },
            new JsonSerializerOptions { WriteIndented = true }), token);
        if (failures == 0) await File.WriteAllTextAsync(Path.Combine(folder, "complete.json"), "{}", token);
    }

    // Minute candles for context instruments over a date range, newest window first, in 60-day windows (the Kite limit for minute data).
    public async Task FetchIndexHistoryAsync(DateOnly from, DateOnly to, string[] symbols, CancellationToken token)
    {
        var master = KiteInstrumentMaster.Parse(await kite.MasterAsync(token), clock.GetUtcNow());
        var windows = new List<(DateOnly Start, DateOnly End)>();
        for (var end = to; end >= from; end = end.AddDays(-60)) windows.Add((end.AddDays(-59) < from ? from : end.AddDays(-59), end));
        Set(Status with { Total = windows.Count * symbols.Length });
        var manifest = new List<object>();
        foreach (var symbol in symbols)
        {
            var row = master.Instruments.FirstOrDefault(item => item.Exchange == "NSE" && item.TradingSymbol == symbol)
                ?? throw new InvalidDataException("SYMBOL_NOT_IN_MASTER:" + symbol);
            foreach (var (start, end) in windows)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(root, "index-minute", symbol.Replace(' ', '_'), $"{start:yyyy-MM-dd}_{end:yyyy-MM-dd}.json");
                await SaveAsync(path, () => kite.HistoricalRangeAsync(row.InstrumentToken, start.ToDateTime(new TimeOnly(9, 0)), end.ToDateTime(new TimeOnly(15, 59)), token),
                    manifest, new { symbol, row.InstrumentToken, start = start.ToString("yyyy-MM-dd"), end = end.ToString("yyyy-MM-dd") });
            }
        }
        Directory.CreateDirectory(Path.Combine(root, "index-minute"));
        await File.WriteAllTextAsync(Path.Combine(root, "index-minute", $"manifest-{clock.GetUtcNow():yyyyMMddHHmmss}.json"),
            JsonSerializer.Serialize(new { from, to, symbols, capturedAtUtc = clock.GetUtcNow(), entries = manifest }, new JsonSerializerOptions { WriteIndented = true }), token);
    }

    public DateOnly TodayIst() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Ist).DateTime);
    public TimeSpan TimeIst() => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Ist).TimeOfDay;
}

// Research instance only. After the close on a weekday, archive today's option candles once; retry every 15 minutes until 20:00 IST.
public sealed class ResearchArchiveWorker(ResearchArchive archive, KiteHistoricalClient kite, Microsoft.Extensions.Options.IOptions<ResearchArchiveOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutoNightly) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                var today = archive.TodayIst(); var time = archive.TimeIst();
                if (today.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && time >= new TimeSpan(15, 50, 0) && time < new TimeSpan(20, 0, 0)
                    && !archive.OptionDayComplete(today) && kite.CredentialStatus is not ("Missing" or "AuthenticationRequired"))
                    archive.TryStart("option-day", token => archive.ArchiveOptionDayAsync(today, token), today.ToString("yyyy-MM-dd"), stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException) { Console.Error.WriteLine($"[archive] {error.GetType().Name}"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class ResearchArchiveRoutes
{
    public static void MapResearchArchive(this WebApplication app)
    {
        var group = app.MapGroup("/api/research/archive").WithTags("Research archive");
        group.MapGet("/status", (ResearchArchive archive) => Results.Json(archive.Status)).WithName("ResearchArchiveStatus")
            .WithSummary("Progress of the running or last research archive job.");
        group.MapPost("/options", (DateOnly? date, ResearchArchive archive, IHostApplicationLifetime lifetime) =>
        {
            var day = date ?? archive.TodayIst();
            return archive.TryStart("option-day", token => archive.ArchiveOptionDayAsync(day, token), day.ToString("yyyy-MM-dd"), lifetime.ApplicationStopping)
                ? Results.Accepted("/api/research/archive/status", archive.Status) : Results.Problem(statusCode: 409, title: "Archive busy", detail: "Another archive job is running.");
        }).WithName("ResearchArchiveOptions").WithSummary("Archive one trading day of live Bank Nifty option minute candles with open interest (read-only Kite requests).");
        group.MapPost("/index-history", (DateOnly from, DateOnly to, string? symbols, ResearchArchive archive, IHostApplicationLifetime lifetime) =>
        {
            var list = (symbols ?? "NIFTY BANK,NIFTY 50,INDIA VIX").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (from > to || to.DayNumber - from.DayNumber > 3000) return Results.Problem(statusCode: 400, title: "Invalid range");
            return archive.TryStart("index-history", token => archive.FetchIndexHistoryAsync(from, to, list, token), $"{from:yyyy-MM-dd}..{to:yyyy-MM-dd} {string.Join('|', list)}",
                lifetime.ApplicationStopping) ? Results.Accepted("/api/research/archive/status", archive.Status)
                : Results.Problem(statusCode: 409, title: "Archive busy", detail: "Another archive job is running.");
        }).WithName("ResearchArchiveIndexHistory").WithSummary("Fetch minute candles for context indices over a date range in 60-day windows (read-only Kite requests).");
    }
}
