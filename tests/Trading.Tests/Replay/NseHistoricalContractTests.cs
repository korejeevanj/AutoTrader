using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Trading.Data;
using Trading.Domain;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class NseHistoricalContractTests
{
    [Fact]
    public async Task A_newer_master_dropping_an_expired_contract_does_not_hide_its_archived_exact_mapping()
    {
        using var fixture = new ReplayFixture(); var store = new HistoricalAcquisitionStore(Path.Combine(fixture.Root, "archive-mappings"));
        const string header = "instrument_token,exchange,tradingsymbol,name,instrument_type,expiry,strike,lot_size,tick_size\n";
        const string index = "260105,NSE,NIFTY BANK,NIFTY BANK,EQ,,0,1,0.05\n";
        await store.ArchiveMasterAsync(Encoding.UTF8.GetBytes(header + index
            + "12466946,NFO,BANKNIFTY26OCTFUT,BANKNIFTY,FUT,2026-10-27,0,30,0.2\n"
            + "12656898,NFO,BANKNIFTY26OCT55000CE,BANKNIFTY,CE,2026-10-27,55000,30,0.05\n"
            + "12657154,NFO,BANKNIFTY26OCT55000PE,BANKNIFTY,PE,2026-10-27,55000,30,0.05\n"), new(2026,10,17,3,0,0,TimeSpan.Zero), CancellationToken.None);
        await store.ArchiveMasterAsync(Encoding.UTF8.GetBytes(header + index), new(2026,11,1,3,0,0,TimeSpan.Zero), CancellationToken.None);
        using var handler = new PublicArchiveHandler(Archive());
        var plan = await store.ResolveNseAsync(new(new HttpClient(handler)), new(2026,10,14), new(9,15), new(15,30), CancellationToken.None);
        Assert.All(plan!.Manifest.Instruments.Where(item => item.Role != HistoricalInstrumentRole.CashIndex), item => Assert.True(item.ProviderMappingVerified));
    }

    internal static byte[] Archive()
    {
        var expiry = (long)(new DateTime(2026, 10, 27, 0, 0, 0) - new DateTime(1980, 1, 1)).TotalSeconds;
        var csv = "FinInstrmId,FinInstrmNm,TckrSymb,XpryDt,StrkPric,OptnTp,PrtdToTrad,NewBrdLotQty,BidIntrvl,StockNm,DelFlg,TradgStsNrmlMkt,ElgbltyNrmlMkt,AdmssnDt,StockRcrdDt\n"
            + $"48699,FUTIDX,BANKNIFTY,{expiry},-1,XX,0,30,20,BANKNIFTY26OCTFUT,N,2,1,0,0\n"
            + $"49441,OPTIDX,BANKNIFTY,{expiry},5500000,CE,0,30,5,BANKNIFTY26OCT55000CE,N,2,1,0,0\n"
            + $"49442,OPTIDX,BANKNIFTY,{expiry},5500000,PE,0,30,5,BANKNIFTY26OCT55000PE,N,2,1,0,0\n"
            + $"50000,OPTIDX,BANKNIFTY,{expiry},7000000,CE,1,30,5,BANKNIFTY26OCT70000CE,N,2,1,0,0\n";
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(csv));
        return output.ToArray();
    }

    [Fact]
    public void Dated_exchange_file_defines_contracts_lots_ticks_and_reserved_status_not_current_broker_universe()
    {
        var day = NseHistoricalContractClient.Parse(Archive(), new(2026, 10, 14));
        var previous = day with { TradingDate = new(2026, 10, 13) };
        KiteMasterRow[] current =
        [new(12466946, "NFO", "BANKNIFTY26OCTFUT", "BANKNIFTY", "FUT", new(2026,10,27), 0m, 999, 9.99m),
         new(12656898, "NFO", "BANKNIFTY26OCT55000CE", "BANKNIFTY", "CE", new(2026,10,27), 55000m, 999, 9.99m),
         new(12657154, "NFO", "BANKNIFTY26OCT55000PE", "BANKNIFTY", "PE", new(2026,10,27), 55000m, 999, 9.99m),
         new(999999, "NFO", "BANKNIFTY26OCT99000CE", "BANKNIFTY", "CE", new(2026,10,27), 99000m, 999, 9.99m)];
        var plan = HistoricalContractResolution.Build(day, previous, current, new(9,15), new(15,30));
        var future = plan.Manifest.Instruments.Single(item => item.Role == HistoricalInstrumentRole.Futures);
        Assert.Equal(30, future.LotSize); Assert.Equal(0.2m, future.TickSize);
        var call = plan.Manifest.Instruments.Single(item => item.Role == HistoricalInstrumentRole.Call);
        Assert.Equal(55000m, call.Strike); Assert.Equal(0.05m, call.TickSize); Assert.Equal(12656898, call.InstrumentToken);
        Assert.True(call.ProviderMappingVerified); Assert.True(call.Verified);
        Assert.DoesNotContain(plan.Manifest.Instruments, item => item.Strike is 70000m or 99000m);
        Assert.Equal(4, day.Contracts.Count); Assert.Single(day.Contracts, item => item.Reserved);
        Assert.Contains("14102026", plan.Source);
        var unresolved = HistoricalContractResolution.Build(day, null, [], new(9,15), new(15,30));
        Assert.All(unresolved.Manifest.Instruments.Where(item => item.Role != HistoricalInstrumentRole.CashIndex), item =>
        { Assert.False(item.ProviderMappingVerified); Assert.False(item.Verified); });
    }

    [Fact]
    public void Front_month_preopen_status_six_does_not_skip_the_contract_for_the_whole_session()
    {
        var parsed = NseHistoricalContractClient.Parse(Archive(), new(2026, 10, 14));
        var october = parsed.Contracts.Single(row => row.Kind == "FUTIDX") with { NormalMarketStatus = 6 };
        var november = october with { ExchangeToken = 61466, Symbol = "BANKNIFTY26NOVFUT", Expiry = new(2026, 11, 23), NormalMarketStatus = 2 };
        var day = parsed with { Contracts = parsed.Contracts.Where(row => row.Kind != "FUTIDX").Concat([october, november]).ToArray() };
        var plan = HistoricalContractResolution.Build(day, day with { TradingDate = day.TradingDate.AddDays(-1) }, [], new(9,15), new(15,30));
        Assert.Equal("NFO:BANKNIFTY26OCTFUT", plan.Manifest.Instruments.Single(row => row.Role == HistoricalInstrumentRole.Futures).Id);
    }

    [Fact]
    public async Task Missing_local_catalogue_automatically_uses_selected_days_exchange_archive_not_live_contracts()
    {
        await using var fixture = new HistoricalApiFixture(); File.Delete(fixture.Catalog);
        fixture.ExchangeHandler = new PublicArchiveHandler(Archive());
        var store = new HistoricalAcquisitionStore(fixture.Root);
        var csv = "instrument_token,exchange,tradingsymbol,name,instrument_type,expiry,strike,lot_size,tick_size\n"
            + "260105,NSE,NIFTY BANK,NIFTY BANK,EQ,,0,1,0.05\n"
            + "12466946,NFO,BANKNIFTY26OCTFUT,BANKNIFTY,FUT,2026-10-27,0,999,9.99\n"
            + "12656898,NFO,BANKNIFTY26OCT55000CE,BANKNIFTY,CE,2026-10-27,55000,999,9.99\n"
            + "12657154,NFO,BANKNIFTY26OCT55000PE,BANKNIFTY,PE,2026-10-27,55000,999,9.99\n";
        await store.ArchiveMasterAsync(Encoding.UTF8.GetBytes(csv), new(2026,10,17,3,0,0,TimeSpan.Zero), CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare();
        var ready = await fixture.Wait(job.Id, view => view.Job.CanReplay && !view.Job.PreparingData || view.Job.State == "Failed");
        Assert.True(ready.Job.CanReplay, ready.Job.Message); Assert.Equal("Strategy", ready.PlaybackKind);
        Assert.Contains("09102026", ready.Job.CatalogSource!);
        Assert.Equal(4, ready.Job.Instruments.Count);
        var dayFile = Path.Combine(fixture.Root, "jobs", job.Id, "attempt-1", "dataset.json");
        var manifest = HistoricalAcquisitionStore.Read<HistoricalDatasetManifest>(dayFile)!;
        Assert.All(manifest.Instruments.Where(item => item.Role != HistoricalInstrumentRole.CashIndex), item => Assert.Equal(30, item.LotSize));
        await fixture.Command(job.Id, "step"); await fixture.Command(job.Id, "step");
        var view = await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}?instrumentId={Uri.EscapeDataString("NFO:BANKNIFTY26OCTFUT")}");
        using var document = JsonDocument.Parse(view);
        Assert.Single(document.RootElement.GetProperty("bars").EnumerateArray());
        Assert.Equal("NFO:BANKNIFTY26OCTFUT", document.RootElement.GetProperty("chartInstrumentId").GetString());
    }

    private sealed class PublicArchiveHandler(byte[] raw) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("nsearchives.nseindia.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) });
        }
    }
}
