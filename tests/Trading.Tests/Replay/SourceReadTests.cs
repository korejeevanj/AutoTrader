using System.IO.Compression;
using System.Net;
using Trading.Data;
using Trading.Domain;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class SourceReadTests
{
    public enum Source { Kite, NseContracts, BankWeights, BankEquities }

    [Theory]
    [InlineData(Source.Kite, 64 * 1024 * 1024)]
    [InlineData(Source.NseContracts, 32 * 1024 * 1024)]
    [InlineData(Source.BankWeights, 64 * 1024 * 1024)]
    [InlineData(Source.BankEquities, 32 * 1024 * 1024)]
    public async Task An_oversized_declared_response_is_rejected_before_any_body_is_buffered(Source source, int maximum)
    {
        using var fixture = new ReplayFixture();
        using var content = new UnreadableContent((long)maximum + 1);
        using var handler = new SourceHandler(() => new(HttpStatusCode.OK) { Content = content });
        using var client = new HttpClient(handler);
        var failure = await Record.ExceptionAsync(() => Read(source, client, fixture.Root, CancellationToken.None));
        AssertLimitFailure(source, failure);
        Assert.False(content.ReadAttempted);
        Assert.True(content.Disposed);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method); Assert.False(request.Cookie);
        Assert.Equal(source == Source.Kite, request.Authorization);
        Assert.Equal(source == Source.Kite ? "api.kite.trade" : source == Source.BankWeights ? "www.niftyindices.com" : "nsearchives.nseindia.com", request.Host);
    }

    [Theory]
    [InlineData(Source.Kite, 64 * 1024 * 1024)]
    [InlineData(Source.NseContracts, 32 * 1024 * 1024)]
    [InlineData(Source.BankWeights, 64 * 1024 * 1024)]
    [InlineData(Source.BankEquities, 32 * 1024 * 1024)]
    public async Task A_response_without_content_length_stops_at_one_byte_past_its_streaming_budget(Source source, int maximum)
    {
        using var fixture = new ReplayFixture(); using var body = new GeneratedBody((long)maximum + 4096);
        using var handler = new SourceHandler(() => new(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var client = new HttpClient(handler);
        var failure = await Record.ExceptionAsync(() => Read(source, client, fixture.Root, CancellationToken.None));
        AssertLimitFailure(source, failure);
        Assert.Equal((long)maximum + 1, body.Consumed);
        Assert.True(body.Disposed);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(Source.Kite)]
    [InlineData(Source.NseContracts)]
    [InlineData(Source.BankWeights)]
    [InlineData(Source.BankEquities)]
    public async Task The_request_deadline_still_applies_when_a_body_stalls_after_headers(Source source)
    {
        using var fixture = new ReplayFixture(); using var body = new StalledBody(); var clock = new DeadlineClock();
        using var handler = new SourceHandler(() => new(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var client = new HttpClient(handler);
        var request = Read(source, client, fixture.Root, CancellationToken.None, clock);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Expire();
        var failure = await Record.ExceptionAsync(() => request.WaitAsync(TimeSpan.FromSeconds(2)));
        if (source == Source.Kite) Assert.Equal("PROVIDER_UNAVAILABLE", Assert.IsType<KiteDataException>(failure).Code);
        else Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.True(body.Cancelled); Assert.True(body.Disposed);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(Source.Kite)]
    [InlineData(Source.NseContracts)]
    [InlineData(Source.BankWeights)]
    [InlineData(Source.BankEquities)]
    public async Task Caller_cancellation_remains_cancellation_without_retry_or_source_reclassification(Source source)
    {
        using var fixture = new ReplayFixture(); using var body = new StalledBody(); var clock = new DeadlineClock();
        using var caller = new CancellationTokenSource();
        using var handler = new SourceHandler(() => new(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var client = new HttpClient(handler);
        var request = Read(source, client, fixture.Root, caller.Token, clock);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(body.Cancelled); Assert.True(body.Disposed);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(Source.NseContracts)]
    [InlineData(Source.BankWeights)]
    [InlineData(Source.BankEquities)]
    public async Task Disabling_the_HttpClient_timeout_still_leaves_a_finite_body_deadline(Source source)
    {
        using var fixture = new ReplayFixture(); using var body = new StalledBody(); var clock = new DeadlineClock();
        using var handler = new SourceHandler(() => new(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var request = Read(source, client, fixture.Root, CancellationToken.None, clock);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(30), clock.LastDueTime);
        clock.Expire();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(body.Disposed); Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData(Source.NseContracts)]
    [InlineData(Source.BankWeights)]
    [InlineData(Source.BankEquities)]
    public async Task Public_source_not_found_remains_unavailable_without_reading_its_body(Source source)
    {
        using var fixture = new ReplayFixture(); var bodies = new List<UnreadableContent>();
        using var handler = new SourceHandler(() =>
        { var body = new UnreadableContent(64L * 1024 * 1024 + 1); bodies.Add(body); return new(HttpStatusCode.NotFound) { Content = body }; });
        using var client = new HttpClient(handler);
        object? result = source switch
        {
            Source.NseContracts => await new NseHistoricalContractClient(client).DownloadAsync(ReplayFixture.Date, CancellationToken.None),
            Source.BankWeights => await new PublishedBankInputsClient(client).WeightsAsync(fixture.Root, ReplayFixture.Date, CancellationToken.None),
            Source.BankEquities => await new PublishedBankInputsClient(client).EquitiesAsync(fixture.Root, ReplayFixture.Date, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        Assert.Null(result); Assert.NotEmpty(bodies);
        Assert.All(bodies, body => { Assert.False(body.ReadAttempted); Assert.True(body.Disposed); });
    }

    [Fact]
    public async Task An_exact_budget_response_succeeds_without_truncation()
    {
        const int maximum = 32 * 1024 * 1024;
        using var body = new GeneratedBody(maximum);
        using var handler = new SourceHandler(() => new(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var client = new HttpClient(handler);
        var raw = await new NseHistoricalContractClient(client).DownloadAsync(ReplayFixture.Date, CancellationToken.None);
        Assert.Equal(maximum, raw!.Length); Assert.Equal(maximum, body.Consumed); Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData(Source.NseContracts)]
    [InlineData(Source.BankEquities)]
    public void Expanded_master_limits_apply_during_gzip_reading(Source source)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var body = new GeneratedBody(128L * 1024 * 1024 + 1)) body.CopyTo(gzip);
        var raw = output.ToArray(); Assert.True(raw.Length < 32 * 1024 * 1024);
        var error = Assert.Throws<InvalidDataException>(() =>
        {
            if (source == Source.NseContracts) NseHistoricalContractClient.Parse(raw, ReplayFixture.Date);
            else PublishedBankInputsClient.ParseEquities(raw);
        });
        Assert.Contains("Expanded", error.Message); Assert.Contains("too large", error.Message);
    }

    [Fact]
    public void An_oversized_weight_PDF_is_rejected_before_document_parsing()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("NIFTY_Bank_Sep2026.pdf", CompressionLevel.Fastest).Open())
        using (var body = new GeneratedBody(8L * 1024 * 1024 + 1)) body.CopyTo(entry);
        var raw = output.ToArray();
        using (var zip = new ZipArchive(new MemoryStream(raw))) Assert.Equal(8L * 1024 * 1024 + 1, Assert.Single(zip.Entries).Length);
        var error = Assert.Throws<InvalidDataException>(() => PublishedBankInputsClient.ParseWeightArchive(raw, "Sep2026", new(2026, 9, 30)));
        Assert.Equal("Weight PDF too large.", error.Message);
    }

    [Fact]
    public async Task A_valid_weight_archive_keeps_its_raw_bytes_and_diagnostic_publication_provenance_when_cached()
    {
        using var fixture = new ReplayFixture(); var raw = PublishedBankInputsFixture.WeightArchive();
        using var handler = new SourceHandler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) });
        using var client = new HttpClient(handler); var adapter = new PublishedBankInputsClient(client);
        var weights = await adapter.WeightsAsync(fixture.Root, ReplayFixture.Date, CancellationToken.None);
        Assert.NotNull(weights); Assert.Equal(new DateOnly(2026, 9, 30), weights.SnapshotDate);
        Assert.Equal(HistoricalAvailabilityMethod.HttpLastModified, weights.PublicationEvidence!.Method);
        Assert.Equal("LATEST_PUBLISHED_MONTH_END_CARRIED_FORWARD", weights.WeightConvention);
        Assert.Collection(weights.Members, row => { Assert.Equal("NSE:AAA", row.InstrumentId); Assert.Equal(60m, row.WeightPercent); },
            row => { Assert.Equal("NSE:BBB", row.InstrumentId); Assert.Equal(40m, row.WeightPercent); });
        var cached = await adapter.WeightsAsync(fixture.Root, ReplayFixture.Date, CancellationToken.None);
        Assert.Equal(weights.Version, cached!.Version); Assert.Single(handler.Requests);
        Assert.Equal(raw, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "published-weights", "2026-09", KiteCandleImporter.Hash(raw) + ".zip")));
    }

    [Fact]
    public async Task A_valid_equity_archive_keeps_dated_rows_lot_tick_and_closing_auction_fields_when_cached()
    {
        using var fixture = new ReplayFixture(); var raw = PublishedBankInputsFixture.EquityArchive();
        using var handler = new SourceHandler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) });
        using var client = new HttpClient(handler); var adapter = new PublishedBankInputsClient(client);
        var result = (await adapter.EquitiesAsync(fixture.Root, ReplayFixture.Date, CancellationToken.None))!.Value;
        Assert.Equal(raw, result.Raw);
        var row = Assert.Single(result.Rows);
        Assert.Equal(123, row.ExchangeToken); Assert.Equal("AAA", row.Symbol);
        Assert.Equal(1, row.LotSize); Assert.Equal(0.05m, row.TickSize); Assert.True(row.ClosingAuction);
        var cached = (await adapter.EquitiesAsync(fixture.Root, ReplayFixture.Date, CancellationToken.None))!.Value;
        Assert.Equal(raw, cached.Raw); Assert.Equal(row, Assert.Single(cached.Rows)); Assert.Single(handler.Requests);
    }

    private static async Task Read(Source source, HttpClient client, string root, CancellationToken token, TimeProvider? clock = null)
    {
        switch (source)
        {
            case Source.Kite:
                await new KiteHistoricalClient(client, () => new("test-key", "test-token"), clock ?? TimeProvider.System,
                    TimeSpan.Zero, 1, client.Timeout).HistoricalAsync(260105, ReplayFixture.Date, new(9, 15), new(15, 30), token);
                break;
            case Source.NseContracts: await new NseHistoricalContractClient(client, clock).DownloadAsync(ReplayFixture.Date, token); break;
            case Source.BankWeights: await new PublishedBankInputsClient(client, clock).WeightsAsync(root, ReplayFixture.Date, token); break;
            case Source.BankEquities: await new PublishedBankInputsClient(client, clock).EquitiesAsync(root, ReplayFixture.Date, token); break;
            default: throw new ArgumentOutOfRangeException(nameof(source));
        }
    }
    private static void AssertLimitFailure(Source source, Exception? error)
    {
        if (source == Source.Kite) Assert.Equal("RESPONSE_TOO_LARGE", Assert.IsType<KiteDataException>(error).Code);
        else Assert.Contains("too large", Assert.IsType<InvalidDataException>(error).Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SourceHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<(string Method, string Host, bool Authorization, bool Cookie)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.Host, request.Headers.Authorization is not null, request.Headers.Contains("Cookie")));
            var value = response();
            value.Content.Headers.LastModified = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            return Task.FromResult(value);
        }
    }
    private sealed class UnreadableContent(long length) : HttpContent
    {
        public bool ReadAttempted { get; private set; }
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long value) { value = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { ReadAttempted = true; throw new InvalidOperationException("The body must not be buffered before checking its declared length."); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private abstract class ReadOnlyBody : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class GeneratedBody(long length) : ReadOnlyBody
    {
        public long Consumed { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var size = (int)Math.Min(count, length - Consumed); buffer.AsSpan(offset, size).Clear(); Consumed += size; return size;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = (int)Math.Min(buffer.Length, length - Consumed); buffer.Span[..size].Clear(); Consumed += size;
            return ValueTask.FromResult(size);
        }
    }
    private sealed class StalledBody : ReadOnlyBody
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }
    private sealed class DeadlineClock : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        public TimeSpan LastDueTime { get; private set; }
        public int ActiveTimers => timers.Count(timer => !timer.Disposed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { LastDueTime = dueTime; var timer = new ManualTimer(callback, state); timers.Add(timer); return timer; }
        public void Expire() { foreach (var timer in timers.ToArray()) timer.Fire(); }
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return default; }
        }
    }
}
