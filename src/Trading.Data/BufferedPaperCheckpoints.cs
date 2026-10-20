using System.Threading.Channels;

namespace Trading.Data;

// One serial writer owns durable paper publication. Filesystem latency never runs
// under the live feed/protection lock. Only the committed callback publishes state.
public sealed class BufferedPaperCheckpoints : IAsyncDisposable
{
    private sealed record Write(string RunId, long Sequence, string? Json, Action Committed, string? Decisions);
    private readonly Channel<Write> queue = Channel.CreateBounded<Write>(new BoundedChannelOptions(64)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly SqliteQuoteStore store;
    private readonly CancellationTokenSource stop = new();
    private readonly Task writer;
    private string? failure;
    public string? Failure => Volatile.Read(ref failure);
    public bool Healthy => Failure is null && queue.Reader.Count < 8;
    public int Pending => queue.Reader.Count;
    public BufferedPaperCheckpoints(SqliteQuoteStore store) { this.store = store; writer = Task.Run(LoopAsync); }
    public bool TrySave(string runId, long sequence, string json, Action committed, string? decisions = null)
    {
        if (queue.Writer.TryWrite(new(runId, sequence, json, committed, decisions))) return true;
        Volatile.Write(ref failure, "PAPER_CHECKPOINT_CAPACITY_EXCEEDED"); return false;
    }
    public bool TryDecision(string runId, long sequence, string decisions)
    {
        if (queue.Writer.TryWrite(new(runId, sequence, null, () => { }, decisions))) return true;
        Volatile.Write(ref failure, "PAPER_CHECKPOINT_CAPACITY_EXCEEDED"); return false;
    }
    private async Task LoopAsync()
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stop.Token))
            {
                while (true)
                {
                    try
                    {
                        if (item.Json is not null) store.Checkpoint(item.RunId, item.Sequence, item.Json, decisions: item.Decisions);
                        else store.AppendDecision(item.RunId, item.Sequence, item.Decisions!);
                        item.Committed(); break;
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
                    { Volatile.Write(ref failure, "PAPER_CHECKPOINT_WRITE_FAILED"); await Task.Delay(1000, stop.Token); }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        try { await writer.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { Volatile.Write(ref failure, "PAPER_CHECKPOINT_SHUTDOWN_INCOMPLETE"); stop.Cancel(); await writer; }
        finally { stop.Dispose(); }
    }
}
