using System.Threading.Channels;
using System.Collections.Concurrent;
using Trading.Data;
using Trading.Domain;

namespace Trading.Recorder;

public sealed class BufferedQuoteRecorder : IAsyncDisposable
{
    private readonly Channel<RecordedMarketEvent> queue;
    private readonly SqliteQuoteStore store;
    private readonly TimeProvider clock;
    private readonly Task writer;
    private readonly CancellationTokenSource stopWriter = new();
    private long durableSequence;
    private long receivedSequence;
    private long bufferedBytes;
    private readonly ConcurrentQueue<(long Sequence, long Timestamp)> pendingTimes = new();
    private string? failure;
    public long DurableSequence => Interlocked.Read(ref durableSequence);
    public long ReceivedSequence => Interlocked.Read(ref receivedSequence);
    public int QueueDepth => queue.Reader.Count;
    public string? Failure => Volatile.Read(ref failure);
    public double LagMilliseconds
    {
        get
        {
            while (pendingTimes.TryPeek(out var committed) && committed.Sequence <= DurableSequence) pendingTimes.TryDequeue(out _);
            return pendingTimes.TryPeek(out var oldest) ? clock.GetElapsedTime(oldest.Timestamp).TotalMilliseconds : 0;
        }
    }
    public bool Healthy => Failure is null && LagMilliseconds <= 250;

    public BufferedQuoteRecorder(SqliteQuoteStore store, TimeProvider clock, int capacity = 8192)
    {
        this.store = store; this.clock = clock;
        queue = Channel.CreateBounded<RecordedMarketEvent>(new BoundedChannelOptions(capacity)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        writer = Task.Run(WriteAsync);
    }

    public bool TryRecord(RecordedMarketEvent item)
    {
        var bytes = Interlocked.Add(ref bufferedBytes, item.Payload.LongLength);
        if (bytes > 128L * 1024 * 1024 || !queue.Writer.TryWrite(item))
        { Interlocked.Add(ref bufferedBytes, -item.Payload.LongLength); Volatile.Write(ref failure, "RECORDER_CAPACITY_EXCEEDED"); return false; }
        pendingTimes.Enqueue((item.Sequence, clock.GetTimestamp()));
        Interlocked.Exchange(ref receivedSequence, item.Sequence);
        return true;
    }

    private async Task WriteAsync()
    {
        try
        {
        while (await queue.Reader.WaitToReadAsync(stopWriter.Token))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), clock, stopWriter.Token);
            var batch = new List<RecordedMarketEvent>(2048);
            while (batch.Count < 2048 && queue.Reader.TryRead(out var item)) batch.Add(item);
            try
            {
                store.Append(batch);
                Interlocked.Add(ref bufferedBytes, -batch.Sum(item => item.Payload.LongLength));
                if (batch.Count != 0) Interlocked.Exchange(ref durableSequence, batch[^1].Sequence);
                while (pendingTimes.TryPeek(out var head) && head.Sequence <= DurableSequence) pendingTimes.TryDequeue(out _);
            }
            catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidDataException)
            {
                Volatile.Write(ref failure, "RECORDER_WRITE_FAILED");
                // Keep the failed batch; bounded ingress will eventually fail closed too.
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, stopWriter.Token);
                    try
                    {
                        store.Append(batch); Interlocked.Exchange(ref durableSequence, batch[^1].Sequence);
                        Interlocked.Add(ref bufferedBytes, -batch.Sum(item => item.Payload.LongLength));
                        while (pendingTimes.TryPeek(out var head) && head.Sequence <= DurableSequence) pendingTimes.TryDequeue(out _);
                        break;
                    }
                    catch (Exception retry) when (retry is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidDataException) { }
                }
                // Failure remains latched: operator must start/recover a run explicitly.
            }
        }
        }
        catch (OperationCanceledException) when (stopWriter.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        try { await writer.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException)
        {
            Volatile.Write(ref failure, "RECORDER_SHUTDOWN_INCOMPLETE"); stopWriter.Cancel();
            await writer;
        }
        finally { stopWriter.Dispose(); }
    }
}
