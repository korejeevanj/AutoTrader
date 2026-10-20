using Trading.Data;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class ReplayOperationLogTests
{
    private const string Job = "0123456789abcdef0123456789abcdef", OtherJob = "fedcba9876543210fedcba9876543210";
    private static readonly DateTimeOffset At = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    private static ReplayOperationEntry Entry(string code, string job = Job) => new(job, 0, null, At, null, "Information", code, "{}");

    private static ReplayOperationLog Log(ReplayFixture files) => new(new ReplayEvidenceJournal(Path.Combine(files.Root, "paper-replay.sqlite")));

    [Fact]
    public async Task Operations_recorded_before_storage_is_ready_are_queued_and_reach_the_journal_in_order()
    {
        using var files = new ReplayFixture(); var log = Log(files);

        Assert.Equal(OperationLogStatus.Queued, log.Record(Entry("FIRST")));
        Assert.Equal(OperationLogStatus.Queued, log.Record(Entry("SECOND")));
        Assert.False(log.Ready); Assert.Empty(log.Recent(Job));

        await log.WhenReady(CancellationToken.None);

        Assert.True(log.Ready);
        Assert.Equal(["SECOND", "FIRST"], log.Recent(Job).Select(row => row.Code)); // newest first
    }

    [Fact]
    public void Each_jobs_backlog_before_storage_is_ready_is_bounded_without_affecting_other_jobs()
    {
        using var files = new ReplayFixture(); var log = Log(files);
        for (var i = 0; i < ReplayOperationLog.BacklogLimit; i++) Assert.Equal(OperationLogStatus.Queued, log.Record(Entry("EARLY" + i)));

        Assert.Equal(OperationLogStatus.BacklogFull, log.Record(Entry("OVERFLOW")));
        Assert.Equal(OperationLogStatus.Queued, log.Record(Entry("OTHER", OtherJob)));
    }

    [Fact]
    public async Task Once_storage_is_ready_operations_are_written_straight_through_after_the_backlog()
    {
        using var files = new ReplayFixture(); var log = Log(files);
        log.Record(Entry("QUEUED")); await log.WhenReady(CancellationToken.None);

        Assert.Equal(OperationLogStatus.Recorded, log.Record(Entry("DIRECT")));

        Assert.Equal(["DIRECT", "QUEUED"], log.Recent(Job).Select(row => row.Code));
    }

    [Fact]
    public void An_operation_recorded_after_another_path_initialized_storage_still_flushes_the_backlog_first()
    {
        using var files = new ReplayFixture();
        var journal = new ReplayEvidenceJournal(Path.Combine(files.Root, "paper-replay.sqlite")); var log = new ReplayOperationLog(journal);
        log.Record(Entry("QUEUED"));
        journal.Initialize(); // e.g. durable evidence opened the file before the log's own initialization ran

        log.Record(Entry("DIRECT"));

        Assert.Equal(["DIRECT", "QUEUED"], log.Recent(Job).Select(row => row.Code));
    }
}
