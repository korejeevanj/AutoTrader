using System.Threading.Channels;

namespace Trading.Data;

// One serial writer owns durable paper publication. Filesystem latency never runs
// under the live feed/protection lock. Only the committed callback publishes state.
public sealed class BufferedPaperCheckpoints : IAsyncDisposable
{
    private sealed record Write(string RunId, long Sequence, Func<string>? Json, Action Committed, Func<string>? Decisions);
    private readonly Channel<Write> queue = Channel.CreateBounded<Write>(new BoundedChannelOptions(64)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly SqliteQuoteStore store;
    private readonly SqliteQuoteStore? decisionStore; // separate file when set, so a diagnostic insert never queues ahead of money state
    private readonly CancellationTokenSource stop = new();
    private readonly Task writer;
    private string? failure;
    public string? Failure => Volatile.Read(ref failure);
    public bool Healthy => Failure is null && queue.Reader.Count < 8;
    public int Pending => queue.Reader.Count;
    public BufferedPaperCheckpoints(SqliteQuoteStore store, SqliteQuoteStore? decisionStore = null)
    { this.store = store; this.decisionStore = decisionStore; writer = Task.Run(LoopAsync); }
    public bool TrySave(string runId, long sequence, string json, Action committed, string? decisions = null)
        => TrySave(runId, sequence, () => json, committed, decisions is null ? null : () => decisions);
    // The delegates run on the writer thread, so serialization never happens under the host feed/protection lock.
    // Callers must capture immutable snapshots only.
    public bool TrySave(string runId, long sequence, Func<string> json, Action committed, Func<string>? decisions = null)
    {
        if (queue.Writer.TryWrite(new(runId, sequence, json, committed, decisions))) return true;
        Volatile.Write(ref failure, "PAPER_CHECKPOINT_CAPACITY_EXCEEDED"); return false;
    }
    public bool TryDecision(string runId, long sequence, string decisions) => TryDecision(runId, sequence, () => decisions);
    public bool TryDecision(string runId, long sequence, Func<string> decisions)
    {
        if (queue.Writer.TryWrite(new(runId, sequence, null, () => { }, decisions))) return true;
        Volatile.Write(ref failure, "PAPER_CHECKPOINT_CAPACITY_EXCEEDED"); return false;
    }
    private async Task LoopAsync()
    {
        SqliteQuoteStore.QuoteWriter? paper = null, decisionWriter = null;
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stop.Token))
            {
                var json = item.Json?.Invoke(); var decisions = item.Decisions?.Invoke();
                while (true)
                {
                    try
                    {
                        paper ??= store.OpenWriter();
                        if (decisionStore is null)
                        {
                            if (json is not null) paper.Checkpoint(item.RunId, item.Sequence, json, decisions: decisions);
                            else paper.AppendDecision(item.RunId, item.Sequence, decisions!);
                        }
                        else
                        {
                            if (decisions is not null) (decisionWriter ??= decisionStore.OpenWriter(durable: false)).AppendDecision(item.RunId, item.Sequence, decisions);
                            if (json is not null) paper.Checkpoint(item.RunId, item.Sequence, json);
                        }
                        item.Committed(); break;
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
                    {
                        Volatile.Write(ref failure, "PAPER_CHECKPOINT_WRITE_FAILED");
                        paper?.Dispose(); paper = null; decisionWriter?.Dispose(); decisionWriter = null;
                        await Task.Delay(1000, stop.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { paper?.Dispose(); decisionWriter?.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        try { await writer.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { Volatile.Write(ref failure, "PAPER_CHECKPOINT_SHUTDOWN_INCOMPLETE"); stop.Cancel(); await writer; }
        finally { stop.Dispose(); }
    }
}
