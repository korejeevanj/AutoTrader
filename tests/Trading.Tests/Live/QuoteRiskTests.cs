using Trading.Risk;
using Xunit;

namespace Trading.Tests.Live;

public sealed class QuoteRiskTests
{
    [Fact]
    public void Undiscounted_at_the_money_one_year_call_matches_reference_and_calibrates()
    {
        // F=K=100, sigma=.2, T=1: 100*(2*Phi(.1)-1).
        var price = ExperimentalOptionScenarios.Price(100m, 100m, 1d, .2d, true);
        Assert.InRange(price, 7.96555m, 7.96559m);
        Assert.InRange(ExperimentalOptionScenarios.ImpliedVolatility(100m, 100m, 1d, price)!.Value, .19999d, .20001d);
        Assert.Null(ExperimentalOptionScenarios.ImpliedVolatility(100m, 100m, 1d, 101m));
    }
}
