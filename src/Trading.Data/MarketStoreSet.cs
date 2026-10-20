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
    private const string LegacyFile = "market.sqlite";
    private const string ArchiveSuffix = ".br";
    private const string ListingSuffix = ".br.runs.json";  // beside market-DATE.sqlite.br

    [GeneratedRegex(@"^market-(\d{4}-\d{2}-\d{2})\.sqlite$")]
    private static partial Regex DayFile();

    public SqliteQuoteStore ForDate(DateOnly tradingDate) => new(Path.Combine(root, $"market-{tradingDate:yyyy-MM-dd}.sqlite"));

    private sealed record Source(string Name, bool Archived);

    // Newest trading date first; the legacy file sorts last. A raw file wins over its archive (a crash can leave both).
    private IEnumerable<Source> Sources()
    {
        if (!Directory.Exists(root)) yield break;
        var names = Directory.EnumerateFiles(root, "market*").Select(Path.GetFileName).OfType<string>().ToArray();
        var raw = names.Where(name => name == LegacyFile || DayFile().IsMatch(name)).ToHashSet();
        var archived = names.Where(name => name.EndsWith(".sqlite" + ArchiveSuffix, StringComparison.Ordinal)
            && DayFile().IsMatch(name[..^ArchiveSuffix.Length]) && File.Exists(ListingPath(name[..^ArchiveSuffix.Length])))
            .Select(name => name[..^ArchiveSuffix.Length]).Where(name => !raw.Contains(name));
        foreach (var name in raw.Select(name => new Source(name, false)).Concat(archived.Select(name => new Source(name, true)))
            .OrderByDescending(source => source.Name == LegacyFile ? "" : source.Name, StringComparer.Ordinal))
            yield return name;
    }

    private string ListingPath(string name) => Path.Combine(root, name + ListingSuffix);

    private IReadOnlyList<RecordedQuoteRun> RunsOf(Source source) => source.Archived
        ? JsonSerializer.Deserialize<RecordedQuoteRun[]>(File.ReadAllBytes(ListingPath(source.Name)))!
        : new SqliteQuoteStore(Path.Combine(root, source.Name)).Runs();

    public IReadOnlyList<RecordedQuoteRun> Runs() =>
        Sources().SelectMany(RunsOf).OrderByDescending(run => run.CreatedAtUtc).ToArray();

    public SqliteQuoteStore StoreOf(string runId)
    {
        var source = Sources().FirstOrDefault(item => RunsOf(item).Any(run => run.Id == runId))
            ?? throw new InvalidOperationException("RECORDING_NOT_FOUND");
        return source.Archived ? new(Extract(source.Name)) : new(Path.Combine(root, source.Name));
    }

    public IEnumerable<RecordedMarketEvent> Read(string runId) => StoreOf(runId).Read(runId);

    // Keeps one decompressed day at a time; the previous extraction is dropped before a different day is written.
    private string Extract(string name)
    {
        lock (extracting) return ExtractLocked(name);
    }

    private string ExtractLocked(string name)
    {
        var cache = Path.Combine(root, "cache"); var target = Path.Combine(cache, name);
        if (File.Exists(target)) return target;
        Directory.CreateDirectory(cache);
        foreach (var stale in Directory.EnumerateFiles(cache)) File.Delete(stale);
        var partial = target + ".tmp";
        using (var input = File.OpenRead(Path.Combine(root, name + ArchiveSuffix))) using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
        using (var output = File.Create(partial)) brotli.CopyTo(output);
        File.Move(partial, target);
        return target;
    }

    // Compresses every fully closed day file except the newest `keepRaw`, verifying the archive byte for byte
    // before the raw file is removed. Returns the raw file names that were archived.
    public IReadOnlyList<string> ArchiveClosedDays(int keepRaw = 2)
    {
        lock (archiving) return Archive(keepRaw);
    }

    private IReadOnlyList<string> Archive(int keepRaw)
    {
        var done = new List<string>();
        foreach (var source in Sources().Where(item => !item.Archived && item.Name != LegacyFile).Skip(keepRaw))
        {
            var store = new SqliteQuoteStore(Path.Combine(root, source.Name));
            var runs = store.Runs();
            if (runs.Count == 0 || runs.Any(run => !run.Closed)) continue;
            store.FoldWal(); SqliteConnection.ClearAllPools();
            Compress(source.Name, runs); done.Add(source.Name);
        }
        return done;
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
