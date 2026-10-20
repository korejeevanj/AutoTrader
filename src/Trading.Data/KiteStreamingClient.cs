using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Trading.Data;

// Only WebSocket subscription/mode messages. There is deliberately no order transport.
public sealed class KiteStreamingClient(TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);

    public async Task ConnectAsync(KiteCredentials credentials, CancellationToken token)
    {
        if (!credentials.Configured) throw new KiteDataException("CREDENTIALS_MISSING");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await socket.ConnectAsync(new Uri("wss://ws.kite.trade/?api_key=" + Uri.EscapeDataString(credentials.ApiKey)
                + "&access_token=" + Uri.EscapeDataString(credentials.AccessToken)), timeout.Token);
        }
        catch (Exception error) when (error is WebSocketException or HttpRequestException)
        { throw new KiteDataException("STREAM_CONNECTION_FAILED"); } // never propagate a credential-bearing URI
    }

    public async Task SubscribeAsync(IReadOnlyList<long> tokens, CancellationToken token)
    {
        if (tokens.Count is < 1 or > 3000 || tokens.Any(item => item <= 0)) throw new ArgumentException("Invalid subscription.");
        await SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { a = "subscribe", v = tokens }), token);
        await SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { a = "mode", v = new object[] { "full", tokens } }), token);
    }

    private async Task SendAsync(byte[] payload, CancellationToken token)
    {
        await sendGate.WaitAsync(token);
        try { await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, token); }
        finally { sendGate.Release(); }
    }

    public async Task ReceiveAsync(Func<string, byte[], DateTimeOffset, Task> receive, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        while (!token.IsCancellationRequested)
        {
            using var message = new MemoryStream(); ValueWebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(buffer.AsMemory(), token);
                if (part.MessageType == WebSocketMessageType.Close) throw new KiteDataException("STREAM_CLOSED");
                if (message.Length + part.Count > 1024 * 1024) throw new KiteDataException("STREAM_MESSAGE_TOO_LARGE");
                message.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            var received = (clock ?? TimeProvider.System).GetUtcNow();
            await receive(part.MessageType == WebSocketMessageType.Binary ? "Binary" : "Text", message.ToArray(), received);
        }
    }

    public ValueTask DisposeAsync() { socket.Abort(); socket.Dispose(); sendGate.Dispose(); return ValueTask.CompletedTask; }
}
