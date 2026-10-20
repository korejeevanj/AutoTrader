using System.IO.Compression;

namespace Trading.Data;

// Request/attempt-local resource enforcement. Adapters retain status, retry,
// credential and source interpretation rules, including their own limit errors.
internal sealed class BoundedSourceRead : IDisposable
{
    private readonly CancellationTokenSource deadline;
    private readonly CancellationTokenSource cancellation;
    public CancellationToken Token => cancellation.Token;

    public BoundedSourceRead(TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
    {
        // Disabling HttpClient's timeout must not create an unbounded source read.
        if (timeout == Timeout.InfiniteTimeSpan) timeout = TimeSpan.FromSeconds(30);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        deadline = new(timeout, clock);
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
    }

    public async Task<byte[]> ResponseAsync(HttpContent content, int maximumBytes, Func<Exception> limitExceeded)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        Token.ThrowIfCancellationRequested();
        if (content.Headers.ContentLength > maximumBytes) throw limitExceeded();
        using var input = await content.ReadAsStreamAsync(Token);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var size = (int)Math.Min(buffer.Length, (long)maximumBytes - output.Length + 1);
            var count = await input.ReadAsync(buffer.AsMemory(0, size), Token);
            if (count == 0) break;
            if (output.Length + count > maximumBytes) throw limitExceeded();
            output.Write(buffer, 0, count);
        }
        Token.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    // Consumes, but does not own, an expanded archive stream. Read at most one
    // byte past the budget to detect overflow without buffering the excess.
    public static byte[] Read(Stream input, int maximumBytes, Func<Exception> limitExceeded)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var size = (int)Math.Min(buffer.Length, (long)maximumBytes - output.Length + 1);
            var count = input.Read(buffer, 0, size);
            if (count == 0) break;
            if (output.Length + count > maximumBytes) throw limitExceeded();
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public static byte[] ExpandGzip(byte[] raw, int maximumBytes, Func<Exception> limitExceeded)
    {
        if (raw.Length < 2 || raw[0] != 0x1f || raw[1] != 0x8b)
        {
            if (raw.Length > maximumBytes) throw limitExceeded();
            return raw;
        }
        using var input = new MemoryStream(raw);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return Read(gzip, maximumBytes, limitExceeded);
    }

    public void Dispose() { cancellation.Dispose(); deadline.Dispose(); }
}
