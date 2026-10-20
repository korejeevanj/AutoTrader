using System.Text.Json;
using Microsoft.Data.Sqlite;
using Trading.Data;
using Trading.Domain;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Live;

public sealed class MarketStoreSetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "market-set-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    // Records one fixture lifecycle under its own run id in the store for the given trading date.
    private static string Record(MarketStoreSet set, DateOnly date, string runId, bool closed)
    {
        var manifest = QuoteSessionTests.Manifest() with { Id = runId };
        var store = set.ForDate(date);
        store.Create(runId, manifest.CreatedAtUtc, JsonSerializer.Serialize(manifest, QuoteTradingSession.Json));
        var events = QuoteSessionTests.Lifecycle().Select(item => item with { RunId = runId }).ToArray();
        store.Append(events);
        if (closed) store.Checkpoint(runId, events[^1].Sequence, "{}", true);
        return runId;
    }

    private static string[] Payloads(IEnumerable<RecordedMarketEvent> events) => events.Select(item => Convert.ToHexString(item.Payload)).ToArray();

    [Fact]
    public void Each_trading_date_records_into_its_own_file_and_any_run_reads_back_by_id()
    {
        var set = new MarketStoreSet(root);
        var first = Record(set, new(2026, 10, 21), "live-20261021-a", closed: true);
        var second = Record(set, new(2026, 10, 22), "live-20261022-b", closed: false);

        Assert.True(File.Exists(Path.Combine(root, "market-2026-10-21.sqlite")));
        Assert.True(File.Exists(Path.Combine(root, "market-2026-10-22.sqlite")));
        Assert.False(File.Exists(Path.Combine(root, "market.sqlite")));
        Assert.Equal(new[] { second, first }, set.Runs().Select(run => run.Id).ToArray());
        Assert.Equal(Payloads(QuoteSessionTests.Lifecycle()), Payloads(set.Read(first)));
        Assert.Equal(Payloads(QuoteSessionTests.Lifecycle()), Payloads(set.Read(second)));
        Assert.Throws<InvalidOperationException>(() => set.Read("missing").ToArray());
    }

    [Fact]
    public void Legacy_single_market_file_stays_listed_and_readable()
    {
        var legacy = new SqliteQuoteStore(Path.Combine(root, "market.sqlite"));
        var manifest = QuoteSessionTests.Manifest();
        legacy.Create(manifest.Id, manifest.CreatedAtUtc, JsonSerializer.Serialize(manifest, QuoteTradingSession.Json));
        legacy.Append(QuoteSessionTests.Lifecycle());

        var set = new MarketStoreSet(root);

        Assert.Equal(manifest.Id, Assert.Single(set.Runs()).Id);
        Assert.Equal(Payloads(QuoteSessionTests.Lifecycle()), Payloads(set.Read(manifest.Id)));
    }

    [Fact]
    public void Closed_days_older_than_the_two_newest_compress_losslessly_and_stay_listed_and_replayable()
    {
        var set = new MarketStoreSet(root);
        var oldest = Record(set, new(2026, 10, 19), "live-20261019-a", closed: true);
        Record(set, new(2026, 10, 20), "live-20261020-b", closed: true);
        Record(set, new(2026, 10, 21), "live-20261021-c", closed: true);
        set.ForDate(new(2026, 10, 19)).FoldWal();
        var rawBytes = new FileInfo(Path.Combine(root, "market-2026-10-19.sqlite")).Length;

        var archived = set.ArchiveClosedDays();

        Assert.Equal(["market-2026-10-19.sqlite"], archived);
        Assert.False(File.Exists(Path.Combine(root, "market-2026-10-19.sqlite")));
        Assert.False(File.Exists(Path.Combine(root, "market-2026-10-19.sqlite-wal")));
        Assert.True(File.Exists(Path.Combine(root, "market-2026-10-20.sqlite")));
        Assert.True(File.Exists(Path.Combine(root, "market-2026-10-21.sqlite")));
        var compressed = new FileInfo(Path.Combine(root, "market-2026-10-19.sqlite.br")).Length;
        Assert.InRange(compressed, 1, rawBytes / 2);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        Assert.Equal(3, set.Runs().Count);
        Assert.Contains(set.Runs(), run => run.Id == oldest && run.Closed);
        Assert.Equal(Payloads(QuoteSessionTests.Lifecycle()), Payloads(set.Read(oldest)));
        Assert.Empty(set.ArchiveClosedDays());
    }

    [Fact]
    public void A_day_holding_an_unclosed_run_is_never_compressed()
    {
        var set = new MarketStoreSet(root);
        Record(set, new(2026, 10, 19), "live-20261019-a", closed: false);
        Record(set, new(2026, 10, 20), "live-20261020-b", closed: true);
        Record(set, new(2026, 10, 21), "live-20261021-c", closed: true);

        Assert.Empty(set.ArchiveClosedDays());
        Assert.True(File.Exists(Path.Combine(root, "market-2026-10-19.sqlite")));
        Assert.False(File.Exists(Path.Combine(root, "market-2026-10-19.sqlite.br")));
    }

    [Fact]
    public void Listing_runs_never_creates_a_database()
    {
        Assert.Empty(new MarketStoreSet(root).Runs());
        Assert.False(Directory.Exists(root));
    }
}
