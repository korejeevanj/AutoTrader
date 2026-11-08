using System.Text;
using Trading.Data;
using Trading.Domain;
using Xunit;

namespace Trading.Tests.Live;

public sealed class ContextIndexTests
{
    private static KiteMasterSnapshot Master(bool vix)
    {
        var rows = new List<string>
        {
            "instrument_token,exchange_token,tradingsymbol,name,last_price,expiry,strike,tick_size,lot_size,instrument_type,segment,exchange",
            "260105,1017,NIFTY BANK,NIFTY BANK,0,,0,0.05,1,EQ,INDICES,NSE",
            "256265,1001,NIFTY 50,NIFTY 50,0,,0,0.05,1,EQ,INDICES,NSE",
            "12466946,48675,BANKNIFTY26OCTFUT,BANKNIFTY,0,2026-10-27,0,0.05,30,FUT,NFO-FUT,NFO",
            "12467202,48676,BANKNIFTY26OCT55800CE,BANKNIFTY,0,2026-10-27,55800,0.05,30,CE,NFO-OPT,NFO",
        };
        if (vix) rows.Add("264969,1035,INDIA VIX,INDIA VIX,0,,0,0.01,1,EQ,INDICES,NSE");
        return KiteInstrumentMaster.Parse(Encoding.UTF8.GetBytes(string.Join("\n", rows) + "\n"), DateTimeOffset.Parse("2026-10-21T02:00:00Z"));
    }

    [Fact]
    public void Resolver_adds_india_vix_and_nifty_50_as_research_only_context_indices_beside_the_cash_index()
    {
        var resolved = LiveBankNiftyReferences.Resolve(Master(vix: true), new(2026, 10, 21), null);

        Assert.Equal(["NSE:INDIA VIX", "NSE:NIFTY 50"], resolved.Where(item => item.Role == HistoricalInstrumentRole.ContextIndex).Select(item => item.Id).Order().ToArray());
        Assert.Equal("NSE:NIFTY BANK", Assert.Single(resolved, item => item.Role == HistoricalInstrumentRole.CashIndex).Id);
        Assert.Contains(resolved, item => item.Role == HistoricalInstrumentRole.Futures);
    }

    [Fact]
    public void A_missing_context_index_is_skipped_rather_than_failing_the_run()
    {
        var resolved = LiveBankNiftyReferences.Resolve(Master(vix: false), new(2026, 10, 21), null);

        Assert.Equal("NSE:NIFTY 50", Assert.Single(resolved, item => item.Role == HistoricalInstrumentRole.ContextIndex).Id);
    }
}
