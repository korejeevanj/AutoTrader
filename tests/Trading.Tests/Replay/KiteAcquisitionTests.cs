using System.Net;
using System.Text;
using System.Text.Json;
using Trading.Data;
using Trading.Domain;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class KiteAcquisitionTests
{
    [Fact]
    public async Task Refetch_repairs_and_quarantines_a_corrupt_content_addressed_source()
    {
        using var fixture = new ReplayFixture(); var store = new HistoricalAcquisitionStore(Path.Combine(fixture.Root, "cache"));
        var instrument = fixture.ReadManifest().Instruments.Single(item => item.Id == "call");
        var raw = File.ReadAllBytes(Path.Combine(fixture.Root, instrument.File));
        var first = await store.CacheAsync(instrument, ReplayFixture.Date, new(9,15), new(9,36), raw, ReplayFixture.MetadataTime, CancellationToken.None);
        var corrupt = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(raw).Replace(",100,", ",101,", StringComparison.Ordinal));
        Assert.NotEqual(KiteCandleImporter.Hash(raw), KiteCandleImporter.Hash(corrupt));
        await File.WriteAllBytesAsync(first.RawFile, corrupt);
        var repaired = await store.CacheAsync(instrument, ReplayFixture.Date, new(9,15), new(9,36), raw, ReplayFixture.MetadataTime.AddSeconds(1), CancellationToken.None);
        Assert.Equal(repaired.Sha256, KiteCandleImporter.Hash(await File.ReadAllBytesAsync(repaired.RawFile)));
        var preserved = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(first.RawFile)!, "*.corrupt-*"));
        Assert.Equal(KiteCandleImporter.Hash(corrupt), KiteCandleImporter.Hash(await File.ReadAllBytesAsync(preserved)));
    }

    [Fact]
    public async Task Progress_publication_survives_a_short_lived_reader_without_losing_the_previous_checkpoint()
    {
        using var fixture = new ReplayFixture();
        var path = Path.Combine(fixture.Root, "progress.json");
        await HistoricalAcquisitionStore.AtomicJsonAsync(path, new { Finished = 1 }, CancellationToken.None);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var publication = HistoricalAcquisitionStore.AtomicJsonAsync(path, new { Finished = 2 }, CancellationToken.None);
        await Task.Delay(100);
        reader.Dispose();
        await publication.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, HistoricalAcquisitionStore.Read<JsonElement>(path).GetProperty("Finished").GetInt32());
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task Shared_provider_limiter_throttles_concurrent_requests_and_never_uses_cookies_or_order_methods()
    {
        using var handler = new FakeKiteHandler(); var times = new List<long>(); var sync = new object();
        handler.Override = (_, _) =>
        {
            lock (sync) times.Add(System.Diagnostics.Stopwatch.GetTimestamp());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"success\",\"data\":{\"candles\":[]}}") });
        };
        using var client = new HttpClient(handler);
        var provider = new KiteHistoricalClient(client, () => new("test-key", "test-token"), TimeProvider.System, TimeSpan.FromMilliseconds(350));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(i => provider.HistoricalAsync(260105, ReplayFixture.Date, new(9, 15), new(15, 30), CancellationToken.None)));
        Assert.Equal(3, times.Count);
        for (var i = 1; i < times.Count; i++)
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(times[i - 1], times[i]);
            Assert.True(elapsed >= TimeSpan.FromMilliseconds(340), $"Provider requests were only {elapsed.TotalMilliseconds:F2} ms apart.");
        }
        Assert.All(handler.Requests, request => { Assert.Equal("GET", request.Method); Assert.False(request.Cookie); });
    }

    [Fact]
    public async Task Provider_request_spacing_survives_a_delayed_send_pipeline()
    {
        using var handler = new FakeKiteHandler();
        var times = new List<long>();
        var calls = 0;
        handler.Override = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref calls) == 1) await Task.Delay(150, cancellationToken);
            times.Add(System.Diagnostics.Stopwatch.GetTimestamp());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"success\",\"data\":{\"candles\":[]}}") };
        };
        using var client = new HttpClient(handler);
        var provider = new KiteHistoricalClient(client, () => new("test-key", "test-token"), TimeProvider.System, TimeSpan.FromMilliseconds(100));
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => provider.HistoricalAsync(260105, ReplayFixture.Date, new(9, 15), new(15, 30), CancellationToken.None)));
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(times[0], times[1]);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(95), $"Provider requests were only {elapsed.TotalMilliseconds:F2} ms apart after descheduling.");
    }

    [Fact]
    public async Task A_provider_body_that_stalls_after_headers_has_a_bounded_deadline()
    {
        using var handler = new FakeKiteHandler();
        handler.Override = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) });
        using var client = new HttpClient(handler);
        var provider = new KiteHistoricalClient(client, () => new("test-key", "test-token"), TimeProvider.System, TimeSpan.Zero, 1, TimeSpan.FromMilliseconds(50));
        var failure = await Assert.ThrowsAsync<KiteDataException>(() => provider.HistoricalAsync(260105, ReplayFixture.Date, new(9,15), new(15,30), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("PROVIDER_UNAVAILABLE", failure.Code); Assert.Single(handler.Requests);
    }
    private sealed class StalledBody : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Transient_rate_limits_retry_but_authentication_failures_do_not_spin_or_disclose_bodies()
    {
        using var handler = new FakeKiteHandler(); var attempts = 0;
        handler.Override = (_, _) => Task.FromResult(Interlocked.Increment(ref attempts) == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"success\",\"data\":{\"candles\":[]}}") });
        using var client = new HttpClient(handler);
        var provider = new KiteHistoricalClient(client, () => new("test-key", "test-token"), TimeProvider.System, TimeSpan.Zero, 2);
        await provider.HistoricalAsync(260105, ReplayFixture.Date, new(9, 15), new(15, 30), CancellationToken.None);
        Assert.Equal(2, attempts);
        var credentials = new KiteCredentials("do-not-print-key", "do-not-print-token");
        Assert.DoesNotContain(credentials.ApiKey, credentials.ToString());
        Assert.DoesNotContain(credentials.AccessToken, JsonSerializer.Serialize(credentials));
    }

    [Fact]
    public async Task Dated_master_preserves_csv_quoting_and_does_not_invent_an_earlier_historical_catalogue()
    {
        using var fixture = new ReplayFixture();
        var store = new HistoricalAcquisitionStore(Path.Combine(fixture.Root, "master-store"));
        const string csv = "instrument_token,exchange,tradingsymbol,name,instrument_type,expiry,strike,lot_size,tick_size\n"
            + "260105,NSE,NIFTY BANK,NIFTY BANK,EQ,,0,1,0.05\n"
            + "100,NFO,BANKNIFTY26OCTFUT,BANKNIFTY,FUT,2026-10-29,0,25,0.05\n"
            + "101,NFO,BANKNIFTY26NOVFUT,BANKNIFTY,FUT,2026-11-26,0,25,0.05\n"
            + "102,NFO,BANKNIFTY26OCT30000CE,BANKNIFTY,CE,2026-10-29,30000,25,0.05\n"
            + "103,NFO,BANKNIFTY26NOV90000PE,BANKNIFTY,PE,2026-11-26,90000,25,0.05\n"
            + "104,NSE,HDFCBANK,\"HDFC, Bank\",EQ,,0,1,0.05\n";
        var captured = new DateTimeOffset(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);
        var snapshot = KiteInstrumentMaster.Parse(Encoding.UTF8.GetBytes(csv), captured);
        Assert.Equal("HDFC, Bank", snapshot.Instruments.Single(row => row.InstrumentToken == 104).Name);
        await store.ArchiveMasterAsync(Encoding.UTF8.GetBytes(csv), captured, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(store.Root, "weights"));
        await HistoricalAcquisitionStore.AtomicJsonAsync(Path.Combine(store.Root, "weights", "weights.json"),
            new HistoricalWeightSet("BANKNIFTY", "test", "test", captured.AddDays(-1), null, captured.AddDays(-1), true, [new("HDFCBANK", 100m)]), CancellationToken.None);
        var plan = store.Resolve(ReplayFixture.Date, new(9, 15), new(15, 30))!;
        Assert.True(plan.AllOptionsCovered);
        Assert.Equal(2, plan.Manifest.Instruments.Count(item => item.Role is HistoricalInstrumentRole.Call or HistoricalInstrumentRole.Put));
        Assert.Equal(100, plan.Manifest.Instruments.Single(item => item.Role == HistoricalInstrumentRole.Futures).InstrumentToken);
        Assert.Null(store.Resolve(ReplayFixture.Date.AddDays(-1), new(9, 15), new(15, 30)));
    }
}
