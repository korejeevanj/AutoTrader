using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Trading.Domain;

namespace Trading.Data;

// Market recordings live in one SQLite file per IST trading date, so a day can be archived or removed on its own.
// The pre-split single market.sqlite stays listed and readable and is never archived; nothing is migrated.
// A closed day older than the newest few is compressed losslessly to market-DATE.sqlite.br beside a run listing,
// and decompressed into a one-file cache only when one of its runs is read back.
public sealed partial class MarketStoreSet(string root)
{
    private readonly object archiving = new();
    private readonly object extracting = new();
    private readonly ConcurrentDictionary<string, byte> faults = new();
    private const string LegacyFile = "market.sqlite";
    private const string ArchiveSuffix = ".br";
    private const string ListingSuffix = ".br.runs.json";  // beside market-DATE.sqlite.br

    [GeneratedRegex(@"^market-(\d{4}-\d{2}-\d{2})\.sqlite$")]
    private static partial Regex DayFile();

    [GeneratedRegex(@"^live-(\d{8})-")]
    private static partial Regex LiveRunId();

    public SqliteQuoteStore ForDate(DateOnly tradingDate) => new(Path.Combine(root, DayName(tradingDate)));

    // Files that could not be read or rebuilt on the last attempt; they are skipped, never silently dropped.
    public IReadOnlyList<string> Faults => faults.Keys.Order(StringComparer.Ordinal).ToArray();

    private sealed record Source(string Name, bool Archived);

    private static string DayName(DateOnly day) => $"market-{day:yyyy-MM-dd}.sqlite";
    private static string RawName(string archiveFile) => archiveFile[..^ArchiveSuffix.Length];
    private static DateOnly? DayOf(string name) => DayFile().Match(name) is { Success: true } match
        ? DateOnly.ParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    private string ListingPath(string name) => Path.Combine(root, name + ListingSuffix);

    // Newest trading date first; the legacy file sorts last. A raw file wins over its archive (a crash can leave both).
    private IEnumerable<Source> Sources()
    {
        if (!Directory.Exists(root)) yield break;
        var names = Directory.EnumerateFiles(root, "market*").Select(Path.GetFileName).OfType<string>().ToArray();
        var raw = names.Where(name => name == LegacyFile || DayFile().IsMatch(name)).ToHashSet();
        var archived = names.Where(name => name.EndsWith(".sqlite" + ArchiveSuffix, StringComparison.Ordinal) && DayFile().IsMatch(RawName(name)))
            .Select(RawName).Where(name => !raw.Contains(name));
        foreach (var source in raw.Select(name => new Source(name, false)).Concat(archived.Select(name => new Source(name, true)))
            .OrderByDescending(source => source.Name == LegacyFile ? "" : source.Name, StringComparer.Ordinal))
            yield return source;
    }

    // An unreadable file is recorded in Faults and contributes no runs, so one bad day cannot block the others.
    private IReadOnlyList<RecordedQuoteRun> RunsOf(Source source)
    {
        try
        {
            var runs = source.Archived ? ArchivedRuns(source.Name) : new SqliteQuoteStore(Path.Combine(root, source.Name)).Runs();
            faults.TryRemove(source.Name, out _);
            return runs;
        }
        catch (Exception error) when (error is SqliteException or IOException or JsonException or InvalidDataException)
        { faults[source.Name] = 0; return []; }
    }

    // The listing sidecar saves decompressing a day just to list it; a missing one is rebuilt from the archive.
    private IReadOnlyList<RecordedQuoteRun> ArchivedRuns(string name)
    {
        var listing = ListingPath(name);
        if (File.Exists(listing)) return JsonSerializer.Deserialize<RecordedQuoteRun[]>(File.ReadAllBytes(listing))!;
        var runs = new SqliteQuoteStore(Extract(name)).Runs();
        File.WriteAllBytes(listing, JsonSerializer.SerializeToUtf8Bytes(runs));
        return runs;
    }

    public IReadOnlyList<RecordedQuoteRun> Runs() =>
        Sources().SelectMany(RunsOf).OrderByDescending(run => run.CreatedAtUtc).ToArray();

    // Live run ids carry their trading date, so that day's file is tried first and other days are rarely opened.
    public SqliteQuoteStore StoreOf(string runId)
    {
        var hinted = LiveRunId().Match(runId) is { Success: true } match
            ? DayName(DateOnly.ParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture)) : null;
        var source = Sources().OrderBy(item => item.Name == hinted ? 0 : 1).FirstOrDefault(item => RunsOf(item).Any(run => run.Id == runId))
            ?? throw new InvalidOperationException("RECORDING_NOT_FOUND");
        return new(source.Archived ? Extract(source.Name) : Path.Combine(root, source.Name));
    }

    public IEnumerable<RecordedMarketEvent> Read(string runId) => StoreOf(runId).Read(runId);

    // Keeps one decompressed day at a time. A stale extraction that a replay is still reading is left in place
    // and removed by a later extraction.
    private string Extract(string name)
    {
        lock (extracting)
        {
            var cache = Path.Combine(root, "cache"); var target = Path.Combine(cache, name);
            if (File.Exists(target)) return target;
            Directory.CreateDirectory(cache);
            foreach (var stale in Directory.EnumerateFiles(cache))
            {
                if (stale.EndsWith(".sqlite", StringComparison.Ordinal)) new SqliteQuoteStore(stale).ReleasePool();
                try { File.Delete(stale); } catch (IOException) { }
            }
            var partial = target + ".tmp";
            using (var input = File.OpenRead(Path.Combine(root, name + ArchiveSuffix))) using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
            using (var output = File.Create(partial)) brotli.CopyTo(output);
            File.Move(partial, target);
            return target;
        }
    }

    // Compresses every fully closed day file except the newest `keepRaw`, verifying the archive byte for byte
    // before the raw file is removed. A trading date that has not started yet (`today`, no file) still holds one
    // of the raw slots. Returns the raw file names that were archived.
    public IReadOnlyList<string> ArchiveClosedDays(int keepRaw = 2, DateOnly? today = null)
    {
        lock (archiving)
        {
            var days = Sources().Where(item => !item.Archived && item.Name != LegacyFile).ToArray();
            var notStarted = today is { } date && !days.Any(item => DayOf(item.Name) >= date) ? 1 : 0;
            var done = new List<string>();
            foreach (var source in days.Skip(Math.Max(0, keepRaw - notStarted)))
            {
                var runs = RunsOf(source);
                if (runs.Count == 0 || runs.Any(run => !run.Closed)) continue;
                new SqliteQuoteStore(Path.Combine(root, source.Name)).FoldWal();
                Compress(source.Name, runs); done.Add(source.Name);
            }
            return done;
        }
    }

    private void Compress(string name, IReadOnlyList<RecordedQuoteRun> runs)
    {
        var path = Path.Combine(root, name); var archive = path + ArchiveSuffix; var partial = archive + ".tmp";
        byte[] expected;
        using (var input = File.OpenRead(path)) using (var output = File.Create(partial))
        {
            expected = SHA256.HashData(input); input.Position = 0;
            using var brotli = new BrotliStream(output, new BrotliCompressionOptions { Quality = 9 });
            input.CopyTo(brotli);
        }
        using (var check = File.OpenRead(partial)) using (var brotli = new BrotliStream(check, CompressionMode.Decompress))
            if (!SHA256.HashData(brotli).AsSpan().SequenceEqual(expected))
            { File.Delete(partial); throw new InvalidDataException("ARCHIVE_VERIFICATION_FAILED"); }
        File.WriteAllBytes(ListingPath(name), JsonSerializer.SerializeToUtf8Bytes(runs));
        File.Move(partial, archive, true);
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }
}
