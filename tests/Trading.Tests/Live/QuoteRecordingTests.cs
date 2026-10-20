using Trading.Data;
using Trading.Domain;
using Trading.Recorder;
using Xunit;

namespace Trading.Tests.Live;

public sealed class QuoteRecordingTests
{
    [Fact]
    public async Task Paper_publication_waits_for_sqlite_commit_without_blocking_the_producer()
    {
        var folder = Path.Combine(Path.GetTempPath(), "paper-publication-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "paper.sqlite"); var store = new SqliteQuoteStore(path);
        store.Create("run", DateTimeOffset.UnixEpoch, "{}");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            connection.Open(); using var blocker = connection.BeginTransaction();
            await using var writer = new BufferedPaperCheckpoints(store);
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(writer.TrySave("run", 1, "{\"acknowledged\":true}", () => committed.SetResult(), "{\"sequence\":1,\"reason\":\"NO_ACTIVE_SETUP\"}"));
            await Task.Delay(50);
            Assert.False(committed.Task.IsCompleted);
            blocker.Rollback();
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, Assert.Single(store.Runs()).LastSequence);
        Assert.Equal("NO_ACTIVE_SETUP", Assert.Single(store.Decisions("run")).GetProperty("reason").GetString());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true);
    }

    [Fact]
    public async Task Recorder_flushes_original_messages_in_order_and_store_retries_are_idempotent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "quote-recording-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteQuoteStore(Path.Combine(folder, "market.sqlite"));
        store.Create("run", DateTimeOffset.UnixEpoch, "{}");
        var first = new RecordedMarketEvent("run", 1, DateTimeOffset.UnixEpoch, "connection", "Binary", [1, 2, 3]);
        await using (var recorder = new BufferedQuoteRecorder(store, TimeProvider.System, 10))
        {
            Assert.True(recorder.TryRecord(first));
            Assert.True(recorder.TryRecord(first with { Sequence = 2 }));
        }
        store.Append([first]);
        var saved = store.Read("run").ToArray();
        Assert.Equal(new long[] { 1, 2 }, saved.Select(item => item.Sequence));
        Assert.Equal(first.Payload, saved[0].Payload);
        Assert.Throws<InvalidDataException>(() => store.Append([first with { Payload = [4] }]));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(folder, true);
    }
}
