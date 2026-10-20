using System.Text.Json;
using Trading.Engine;
using Xunit;

namespace Trading.Tests.Engine;

public sealed class DecisionFollowupTests
{
    private readonly TradingDecisionEngine engine = new(new());

    [Theory]
    [InlineData("bars", "MONITORING_BAR_UNAVAILABLE")]
    [InlineData("constituents", "THESIS_CONFIRMATION_UNKNOWN")]
    [InlineData("features", "THESIS_EVIDENCE_UNKNOWN")]
    [InlineData("oldBars", "MONITORING_BAR_UNAVAILABLE")]
    public void Fresh_prices_do_not_make_missing_required_monitoring_evidence_a_safe_hold(string missing, string reason)
    {
        var input = EngineFixture.Position();
        input = missing switch
        {
            "bars" => input with { FuturesBars = [], FuturesFeatures = [] },
            "constituents" => input with { Constituents = [] },
            "features" => input with { FuturesFeatures = [] },
            _ => Refresh(input, input.AsOfUtc.AddMinutes(2))
        };

        var decision = engine.EvaluatePosition(input);

        Assert.Equal(PositionAction.WATCH_EXIT, decision.Action);
        Assert.Contains(reason, decision.ReasonCodes);
        Assert.False(decision.State.ExitLatched); // unknown risk is not a fabricated stop/fill
    }

    [Fact]
    public void Soft_weakening_is_bounded_even_if_banks_never_confirm_hard_failure_and_no_ticks_arrive()
    {
        var input = EngineFixture.Position();
        var one = Market(input, EngineFixture.Extend(EngineFixture.Entry(), 99m));
        var watching = engine.EvaluatePosition(one);
        Assert.Contains("VWAP_THESIS_WEAKENING", watching.ReasonCodes);
        Assert.Equal(PositionAction.WATCH_EXIT, watching.Action);

        var timer = Refresh(one, one.AsOfUtc.AddMinutes(2)) with { OptionQuote = null, FuturesPrice = null };
        var expired = engine.EvaluatePosition(timer, watching.State);
        Assert.Equal(PositionAction.EXIT, expired.Action);
        Assert.Contains("SOFT_THESIS_TIMEOUT", expired.ReasonCodes);
        Assert.DoesNotContain("THESIS_FAILURE", expired.ReasonCodes); // banks still support the original direction

        var recoveredAfterDeadline = Market(input, EngineFixture.Extend(EngineFixture.Entry(), 99m, 99m, 99m, 105m));
        var batch = engine.EvaluatePosition(recoveredAfterDeadline);
        Assert.Contains("SOFT_THESIS_TIMEOUT", batch.ReasonCodes); // later recovery cannot erase an earlier timeout
    }

    [Fact]
    public void Explicit_net_cash_stop_includes_costs_and_does_not_reinterpret_the_entry_risk_budget()
    {
        var settings = EngineConfiguration.FromJson("""
            {"Strategies":[{"IndexId":"BANKNIFTY","Risk":{"NetCashStop":220}}]}
            """);
        var input = EngineFixture.Position();
        input = input with { Position = input.Position with { FrozenStrategy = settings.Strategies[0] },
            OptionQuote = input.OptionQuote! with { Bid = 193m, Ask = 194m },
            NetLiquidation = input.NetLiquidation! with { GrossPnl = -175m, ProjectedExitFees = 30m, NetPnl = -225m },
            FuturesBars = [], FuturesFeatures = [] };

        var stopped = engine.EvaluatePosition(input);
        Assert.Equal(PositionAction.EXIT, stopped.Action);
        Assert.Equal(-225m, stopped.NetLiquidationPnl);
        Assert.Equal("NET_CASH_STOP", stopped.ReasonCodes[0]);
        Assert.DoesNotContain("GROSS_CASH_STOP", stopped.ReasonCodes);
        var unknownFees = engine.EvaluatePosition(input with { NetLiquidation = null });
        Assert.Equal(PositionAction.WATCH_EXIT, unknownFees.Action);
        Assert.DoesNotContain("NET_CASH_STOP", unknownFees.ReasonCodes);
        var recovered = engine.EvaluatePosition(EngineFixture.Position() with { Position = input.Position }, stopped.State);
        Assert.Equal(PositionAction.EXIT, recovered.Action); // still exposure, still awaiting execution feedback
    }

    [Fact]
    public void Frozen_exit_slippage_rounds_adversely_once_and_can_trigger_a_quote_driven_stop()
    {
        var input = EngineFixture.Position();
        var json = JsonSerializer.Serialize(input.Position);
        var position = JsonSerializer.Deserialize<PaperPositionEvidence>(json[..^1] + ",\"ExitAdverseSlippage\":0.06}")!;
        input = input with { Position = position, OptionQuote = input.OptionQuote! with { Bid = 192.05m, Ask = 193.05m },
            NetLiquidation = input.NetLiquidation! with { GrossPnl = -201.25m, NetPnl = -241.25m },
            FuturesBars = [], FuturesFeatures = [] };
        var changedHostSettings = new TradingDecisionEngine(new() { PaperSimulation = new() { ExitAdverseSlippage = 9m } });

        var decision = changedHostSettings.EvaluatePosition(input);

        Assert.Equal(PositionAction.EXIT, decision.Action);
        Assert.Equal(-201.25m, decision.GrossLiquidationPnl); // floor((192.05 - .06)/.05)*.05 = 191.95
        Assert.Equal(-241.25m, decision.NetLiquidationPnl); // no second spread/slippage charge
        Assert.Equal("GROSS_CASH_STOP", decision.ReasonCodes[0]);
    }

    [Theory]
    [InlineData("entryPrice", "EXECUTION_ASSUMPTIONS")]
    [InlineData("spread", "QUOTE_COST_RECONCILIATION")]
    [InlineData("friction", "QUOTE_COST_RECONCILIATION")]
    [InlineData("moneyness", "QUOTE_COST_RECONCILIATION")]
    [InlineData("cash", "PREMIUM_CASH_FLOOR")]
    [InlineData("risk", "IMMEDIATE_LIQUIDATION_RISK_FLOOR")]
    public void Favorable_financial_assertions_cannot_override_observed_quote_costs(string field, string reason)
    {
        var input = EngineFixture.Entry();
        var candidate = input.Candidates[0];
        var risk = candidate.Risk!;
        risk = field switch
        {
            "entryPrice" => risk with { AssumedEntryPrice = 200.03m }, // ask is 200, tick .05, declared slippage zero
            "spread" => risk with { SpreadPercent = 0m }, // observed 199/200 is .5%
            "friction" => risk with { RoundTripFriction = 0m }, // observed one-lot crossing + fees is 65
            "moneyness" => risk with { AbsoluteStrikeDistance = 0m }, // 100 strike vs 105 futures
            "cash" => risk with { RequiredCash = 1m }, // cannot buy 25 units at 200 for one rupee
            _ => risk with { NetRisk = 1m }
        };

        var result = engine.EvaluateIndex(input with { Candidates = [candidate with { Risk = risk }] });
        var decision = Assert.Single(engine.Allocate(input.Identity, input.AsOfUtc, input.Session, input.Portfolio, [result]).Decisions);

        Assert.False(decision.Eligible);
        Assert.Null(decision.Rank);
        Assert.Equal(EntryAction.SKIP, decision.Action);
        Assert.Contains(reason, decision.ReasonCodes);
    }

    [Fact]
    public void Raising_net_profit_protection_is_an_explicit_action_and_never_loosens_on_a_pullback()
    {
        var input = EngineFixture.Position();
        input = input with { Position = input.Position with { FrozenStrategy = input.Position.FrozenStrategy with
            { Position = input.Position.FrozenStrategy.Position with { ProfitExit = new() { Mode = ProfitExitMode.TrailingNetProfit } } } },
            OptionQuote = input.OptionQuote! with { Bid = 221.6m, Ask = 222.6m },
            NetLiquidation = input.NetLiquidation! with { GrossPnl = 540m, NetPnl = 500m } };
        var armed = engine.EvaluatePosition(input);
        Assert.Equal("TIGHTEN_STOP", armed.Action.ToString());
        Assert.Equal(300m, armed.State.ProfitTrailThreshold);
        Assert.Contains("NET_PROFIT_TRAIL_TIGHTENED", armed.ReasonCodes);

        var higher = input with { OptionQuote = input.OptionQuote! with { Bid = 233.6m, Ask = 234.6m },
            NetLiquidation = input.NetLiquidation! with { GrossPnl = 840m, NetPnl = 800m } };
        var tightened = engine.EvaluatePosition(higher, armed.State);
        Assert.Equal("TIGHTEN_STOP", tightened.Action.ToString());
        Assert.Equal(600m, tightened.State.ProfitTrailThreshold);
        var pullback = engine.EvaluatePosition(input, tightened.State);
        Assert.Equal(PositionAction.EXIT, pullback.Action);
        Assert.Equal(600m, pullback.State.ProfitTrailThreshold);
        Assert.Contains("NET_PROFIT_TRAIL", pullback.ReasonCodes);
    }

    [Fact]
    public void Thirty_five_alternatives_can_select_beyond_atm_but_a_future_contract_cannot_win()
    {
        var input = EngineFixture.Entry();
        var seed = input.Candidates[0];
        var options = Enumerable.Range(0, 35).Select(i =>
        {
            var id = $"option-{i:D2}";
            var contract = seed.Contract with { Id = id, Strike = 100m + i,
                AvailableAtUtc = i == 34 ? input.AsOfUtc.AddSeconds(1) : seed.Contract.AvailableAtUtc };
            var quote = seed.Quote! with { Id = $"quote-{id}", InstrumentId = id, Bid = i == 30 ? 199.5m : i == 34 ? 200m : 199m };
            var risk = seed.Risk! with { Id = $"risk-{id}", ContractId = id, QuoteId = quote.Id,
                SpreadPercent = i == 30 ? 0.25m : i == 34 ? 0m : 0.5m,
                RoundTripFriction = i == 30 ? 52.5m : i == 34 ? 40m : 65m,
                AbsoluteStrikeDistance = Math.Abs(contract.Strike - 105m) };
            return new OptionCandidateEvidence(contract, quote, risk);
        }).ToArray();
        input = input with { Candidates = options, UniverseVersion = "frozen-35-alternatives-v1" };
        var forward = engine.EvaluateIndex(input);
        var reversed = engine.EvaluateIndex(input with { Candidates = options.Reverse().ToArray() });
        Assert.Equal(JsonSerializer.Serialize(forward), JsonSerializer.Serialize(reversed));
        var allocation = engine.Allocate(input.Identity, input.AsOfUtc, input.Session, input.Portfolio, [forward]);
        Assert.Equal("option-30", Assert.Single(allocation.Decisions, candidate => candidate.Action == EntryAction.ENTER).ContractId);
        Assert.Equal(25m, forward.Candidates.Single(candidate => candidate.ContractId == "option-30").FinancialEvidence!.AbsoluteStrikeDistance);
        Assert.Null(forward.Candidates.Single(candidate => candidate.ContractId == "option-34").Rank);
        Assert.Contains("OPTION_DEFINITION_UNAVAILABLE", forward.Candidates.Single(candidate => candidate.ContractId == "option-34").ReasonCodes);
    }

    [Fact]
    public void Soft_confirmation_deadline_is_not_extended_by_ticks_and_recovery_before_it_clears_the_warning()
    {
        var initial = EngineFixture.Position();
        var one = Market(initial, EngineFixture.Extend(EngineFixture.Entry(), 99m));
        var first = engine.EvaluatePosition(one);
        Assert.Equal(one.FuturesBars[^1].AvailableAtUtc.AddMinutes(2), first.State.SoftWeakeningDeadlineUtc);
        Assert.All(first.WatchResolutions!, resolution => Assert.Equal(first.State.SoftWeakeningDeadlineUtc, resolution.DeadlineUtc));
        var repeated = engine.EvaluatePosition(Refresh(one, one.AsOfUtc.AddSeconds(30)), first.State);
        Assert.Equal(first.State.SoftWeakeningDeadlineUtc, repeated.State.SoftWeakeningDeadlineUtc);
        var recovered = Market(initial, EngineFixture.Extend(EngineFixture.Entry(), 99m, 105m));
        var decision = engine.EvaluatePosition(recovered, repeated.State);
        Assert.Equal(PositionAction.HOLD, decision.Action);
        Assert.Null(decision.State.SoftWeakeningDeadlineUtc);
        Assert.Empty(decision.WatchResolutions!);
    }

    [Fact]
    public void Removing_already_processed_features_cannot_preserve_a_safe_hold()
    {
        var input = Market(EngineFixture.Position(), EngineFixture.Extend(EngineFixture.Entry(), 105m));
        var valid = engine.EvaluatePosition(input);
        Assert.Equal(PositionAction.HOLD, valid.Action);
        var missing = engine.EvaluatePosition(input with { FuturesFeatures = [] }, valid.State);
        Assert.Equal(PositionAction.WATCH_EXIT, missing.Action);
        Assert.Contains("THESIS_EVIDENCE_UNKNOWN", missing.ReasonCodes);
    }

    [Fact]
    public void An_older_monitoring_prefix_cannot_clear_a_warning_observed_on_a_later_bar()
    {
        var initial = EngineFixture.Position();
        var current = Market(initial, EngineFixture.Extend(EngineFixture.Entry(), 99m));
        var warning = engine.EvaluatePosition(current);
        var missingTail = Refresh(initial, current.AsOfUtc);
        var repeated = engine.EvaluatePosition(missingTail, warning.State);
        Assert.Equal(warning.State.SoftWeakeningDeadlineUtc, repeated.State.SoftWeakeningDeadlineUtc);
        Assert.Contains("MONITORING_BAR_UNAVAILABLE", repeated.ReasonCodes);
    }

    [Fact]
    public void An_adverse_latest_entry_period_is_soft_weakening_even_before_the_first_post_entry_close()
    {
        var initial = EngineFixture.Position();
        var bars = initial.FuturesBars.SkipLast(1).Append(EngineFixture.Bar(15, 99m)).ToArray();
        var decision = engine.EvaluatePosition(initial with { FuturesBars = bars, FuturesFeatures = EngineFixture.Features(bars) });
        Assert.Equal(PositionAction.WATCH_EXIT, decision.Action);
        Assert.Contains("VWAP_THESIS_WEAKENING", decision.ReasonCodes);
        Assert.NotNull(decision.State.SoftWeakeningDeadlineUtc);
    }

    [Fact]
    public void A_structural_risk_value_must_reconcile_the_signal_close_and_its_frozen_stop()
    {
        var input = EngineFixture.Entry();
        input = input with { FuturesFeatures = input.FuturesFeatures.SkipLast(1).Append(input.FuturesFeatures[^1] with
            { LongInitialRisk = 0.01m }).ToArray() };
        var decision = Assert.Single(engine.EvaluateIndex(input).Candidates);
        Assert.False(decision.Eligible);
        Assert.Contains("STRUCTURAL_RISK_UNKNOWN", decision.ReasonCodes);
    }

    [Theory]
    [InlineData("feature")]
    [InlineData("constituents")]
    public void Support_published_after_the_soft_deadline_cannot_retroactively_clear_it(string delayed)
    {
        var initial = EngineFixture.Position();
        var one = Market(initial, EngineFixture.Extend(EngineFixture.Entry(), 99m));
        var warning = engine.EvaluatePosition(one);
        var recovery = Market(initial, EngineFixture.Extend(EngineFixture.Entry(), 99m, 105m));
        var publication = warning.State.SoftWeakeningDeadlineUtc!.Value.AddSeconds(10);
        recovery = Refresh(recovery, publication.AddSeconds(1));
        recovery = delayed == "feature" ? recovery with { FuturesFeatures = recovery.FuturesFeatures.SkipLast(1)
            .Append(recovery.FuturesFeatures[^1] with { AvailableAtUtc = publication }).ToArray() }
            : recovery with { Constituents = recovery.Constituents.Select(row => row.EndUtc == recovery.FuturesBars[^1].EndUtc
                ? row with { AvailableAtUtc = publication } : row).ToArray() };

        var decision = engine.EvaluatePosition(recovery, warning.State);
        Assert.Equal(PositionAction.EXIT, decision.Action);
        Assert.Contains("SOFT_THESIS_TIMEOUT", decision.ReasonCodes);
    }

    [Fact]
    public void Shared_reservation_cannot_net_away_the_purchase_premium_of_two_long_options()
    {
        var allocator = new TradingDecisionEngine(new() { Strategies = [new() { IndexId = "BANKNIFTY" }, new() { IndexId = "NIFTY" }],
            Portfolio = new() { MaximumActiveOrPendingEntries = 2 } });
        var bank = EngineFixture.Entry();
        var nifty = EngineFixture.Entry("NIFTY");
        var result = allocator.Allocate(bank.Identity, bank.AsOfUtc, bank.Session, bank.Portfolio,
            [allocator.EvaluateIndex(bank), allocator.EvaluateIndex(nifty)], reservationAssessor: new UnderfundedReservation());
        Assert.Single(result.Decisions, decision => decision.Action == EntryAction.ENTER);
        var rejected = result.Decisions.Single(decision => decision.IndexId == "NIFTY");
        Assert.Contains("PORTFOLIO_PREMIUM_CASH_FLOOR", rejected.ReasonCodes);
        Assert.Contains(rejected.Gates, gate => gate.Code == "PORTFOLIO_PREMIUM_CASH_FLOOR" && gate.Observed == 8500m && gate.Threshold == 10000m);
    }

    private sealed class UnderfundedReservation : IPortfolioReservationAssessor
    {
        public ReservationAssessment Assess(PortfolioEvidence portfolio, string configurationVersion,
            IReadOnlyList<EntryRiskEvidence> proposedReservations, DateTimeOffset asOfUtc) =>
            new(portfolio.Version, configurationVersion, proposedReservations.Select(risk => risk.Id).ToArray(), 8500m, "synthetic-shared-risk-v1");
    }

    private static PositionEvaluationInput Market(PositionEvaluationInput input, IndexEvaluationInput market) =>
        Refresh(input, market.AsOfUtc) with
        {
            FuturesBars = market.FuturesBars, FuturesFeatures = market.FuturesFeatures,
            Constituents = market.FuturesBars.Skip(16).SelectMany(bar => EngineFixture.Banks(bar)).ToArray()
        };

    private static PositionEvaluationInput Refresh(PositionEvaluationInput input, DateTimeOffset time) => input with
    {
        AsOfUtc = time,
        OptionQuote = EngineFixture.Quote(input.Position.Contract.Id, time, input.OptionQuote?.Bid ?? 200m),
        NetLiquidation = input.NetLiquidation is { } net ? net with
        {
            QuoteId = EngineFixture.Quote(input.Position.Contract.Id, time).Id,
            AvailableAtUtc = time, ValidUntilUtc = time.AddSeconds(2)
        } : null,
        FuturesPrice = EngineFixture.Underlying(time, input.FuturesPrice?.Price ?? 105m),
        Progress = input.Progress is { } progress ? progress with
        {
            UnderlyingQuoteId = EngineFixture.Underlying(time).Id, AvailableAtUtc = time
        } : null,
        Portfolio = input.Portfolio with { AvailableAtUtc = time }
    };
}
