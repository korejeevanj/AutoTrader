using System.Buffers.Binary;
using System.Text.Json;
using Trading.Domain;
using Trading.Replay;
using Xunit;

namespace Trading.Tests.Live;

// Session policy v2 behaviour, driven only by recorded events so each case replays deterministically.
public sealed class QuoteSessionPolicyTests
{
    private static readonly DateTimeOffset Open = QuoteSessionTests.Open;
    private const long Future = 258, Bank = 513;

    private static QuoteRunManifest V2(bool waiver = false) => QuoteSessionTests.Manifest() with
        { SessionPolicy = QuoteTradingSession.PolicyV2, ManualLifecycleWaiver = waiver ? "prior-run" : null };

    private sealed class Feed(QuoteRunManifest manifest)
    {
        public readonly QuoteTradingSession Session = new(manifest);
        private long sequence;
        public void Send(string kind, DateTimeOffset time, byte[] payload, MarketAdmission? admission = null)
            => Session.Apply(new("fixture", ++sequence, time, "test", kind, payload, admission));

        public void Tick(DateTimeOffset time, decimal high = 101, decimal low = 99, decimal atp = 100.5m, long? volume = null)
        {
            var v = volume ?? (long)(time - Open).TotalSeconds + 1;
            Send("Binary", time, Packet(Future, time, 100, v, atp, high, low));
            Send("Binary", time, Packet(Bank, time, 100, v, atp, high, low));
        }

        public void Health(DateTimeOffset time, bool healthy = true)
            => Send("Health", time, JsonSerializer.SerializeToUtf8Bytes(new QuoteRecorderHealth(healthy, sequence), QuoteTradingSession.Json));

        public void Control(DateTimeOffset time, string command)
            => Send("Control", time, JsonSerializer.SerializeToUtf8Bytes(new QuoteControl(command, command + "-1"), QuoteTradingSession.Json));

        public void Run(int from, int to)
        {
            for (var second = from; second <= to; second++) { var time = Open.AddSeconds(second); Tick(time); Health(time); }
        }
    }

    private static byte[] Packet(long token, DateTimeOffset time, decimal last, long volume, decimal atp, decimal high, decimal low,
        TimeSpan? tradeAge = null, TimeSpan? exchangeAge = null)
    {
        var message = QuoteSessionTests.Packet(token, time, last, volume);
        void Set(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(offset + 4), value);
        Set(12, (uint)(atp * 100)); Set(32, (uint)(high * 100)); Set(36, (uint)(low * 100));
        Set(44, (uint)(time - (tradeAge ?? TimeSpan.Zero)).ToUnixTimeSeconds()); Set(60, (uint)(time - (exchangeAge ?? TimeSpan.Zero)).ToUnixTimeSeconds());
        return message;
    }

    [Fact]
    public void Research_risk_sizes_to_the_cash_stop_and_unchanged_volatility_instead_of_the_structural_stop_and_worst_volatility()
    {
        var inputs = new Trading.Risk.OptionScenarioInputs(55000, 55000, Open.AddDays(2), Open, 400, 401, .05m, 30, true, 150, 54850, "fees");
        var model = new Trading.Risk.OptionScenarioAssessment("v", "MODEL_ESTIMATE", .2, 401.05m, 38, 76, 12069, NetRisk: 3533, FavorableNetProfit: -2030,
            1.2m, 112, 0, [new(1m, .8, 1200d, 395m, -2030m), new(1m, 1d, 1200d, 420m, 520m), new(1m, 1.2, 1200d, 445m, 1270m), new(-1m, 1d, 1200d, 380m, -700m)], inputs);

        var research = QuoteTradingSession.ApplyResearchRisk(model, 200);

        Assert.Equal(276m, research.NetRisk);                  // gross cash stop 200 + round-trip fees 76 (friction 112 is smaller)
        Assert.Equal(520m, research.FavorableNetProfit);       // +1R after 20 minutes at unchanged volatility
        Assert.Equal("RESEARCH_COUNTERFACTUAL_RISK", research.EvidenceClass);
        Assert.Equal(3533m, model.NetRisk);                    // the live assessment is untouched
    }

    [Fact]
    public void V2_gates_the_futures_quote_on_exchange_age_not_on_how_long_ago_it_last_traded()
    {
        var feed = new Feed(V2(waiver: true));
        feed.Send("Connected", Open, []);
        for (var second = 0; second <= 20; second++)
        {
            var time = Open.AddSeconds(second);
            feed.Send("Binary", time, Packet(Future, time, 100, second + 1, 100.5m, 101, 99, tradeAge: TimeSpan.FromSeconds(10)));
            feed.Send("Binary", time, Packet(Bank, time, 100, second + 1, 100.5m, 101, 99));
            feed.Health(time);
        }
        feed.Control(Open.AddSeconds(20), "arm");
        Assert.True(feed.Session.AutoEnabled);
        Assert.Equal(Open.AddSeconds(20), feed.Session.Quotes.Single(q => q.InstrumentToken == Future).ExchangeTimeUtc);
        Assert.Equal(Open.AddSeconds(10), feed.Session.Quotes.Single(q => q.InstrumentToken == Future).LastTradeTimeUtc);
    }

    [Fact]
    public void V2_still_blocks_when_the_exchange_timestamp_itself_is_stale()
    {
        var feed = new Feed(V2(waiver: true));
        feed.Send("Connected", Open, []);
        for (var second = 0; second <= 20; second++)
        {
            var time = Open.AddSeconds(second);
            feed.Send("Binary", time, Packet(Future, time, 100, second + 1, 100.5m, 101, 99, exchangeAge: TimeSpan.FromSeconds(10)));
            feed.Send("Binary", time, Packet(Bank, time, 100, second + 1, 100.5m, 101, 99));
            feed.Health(time);
        }
        Assert.Throws<InvalidOperationException>(() => feed.Control(Open.AddSeconds(20), "arm"));
    }

    private static Feed WithOutage(QuoteRunManifest manifest, int outageSeconds, decimal resumeHigh = 101)
    {
        var feed = new Feed(manifest);
        feed.Send("Connected", Open, []);
        feed.Run(0, 299);
        feed.Send("Disconnected", Open.AddSeconds(300), System.Text.Encoding.UTF8.GetBytes("FEED_WATCHDOG"));
        feed.Send("Connected", Open.AddSeconds(300 + outageSeconds - 2), []);
        for (var second = 300 + outageSeconds; second <= 320 + outageSeconds; second++)
        {
            var time = Open.AddSeconds(second); feed.Tick(time, resumeHigh); feed.Health(time);
        }
        return feed;
    }

    [Fact]
    public void Short_gap_with_unmoved_day_extremes_restores_coverage_and_switches_to_exchange_atp()
    {
        var feed = WithOutage(V2(), 8);
        var bars = feed.Session.Checkpoint().Bars;
        Assert.True(bars["future"].CompleteCoverage); Assert.True(bars["bank"].CompleteCoverage);
        Assert.Contains("FEED_GAP_RECOVERED_EXCHANGE_ATP", feed.Session.Issues);
        Assert.Equal(Open.AddMinutes(4), feed.Session.RecoveredFrom["future"]); // the last packet before the gap was in minute 4
        var atp = feed.Session.Checkpoint().AtpByMinute!["future"];
        Assert.Equal(100.5m, atp[(Open.AddMinutes(5)).ToUnixTimeSeconds() / 60]);
        Assert.Equal(JsonSerializer.Serialize(feed.Session.Checkpoint(), QuoteTradingSession.Json),
            JsonSerializer.Serialize(Replayed(feed), QuoteTradingSession.Json));
    }

    private static QuoteSessionCheckpoint Replayed(Feed feed)
    {
        var again = WithOutage(V2(), 8);
        return again.Session.Checkpoint();
    }

    [Fact]
    public void A_moved_day_extreme_means_a_swing_may_have_been_missed_so_coverage_stays_void()
    {
        var feed = WithOutage(V2(), 8, resumeHigh: 102);
        Assert.False(feed.Session.Checkpoint().Bars["future"].CompleteCoverage);
        Assert.Contains("FEED_GAP_NOT_RECOVERABLE:DAY_EXTREME_MOVED_DURING_GAP", feed.Session.Issues);
        Assert.Empty(feed.Session.RecoveredFrom);
    }

    [Fact]
    public void An_outage_longer_than_the_recoverable_limit_stays_void()
    {
        var feed = WithOutage(V2(), 70);
        Assert.False(feed.Session.Checkpoint().Bars["future"].CompleteCoverage);
        Assert.Contains("FEED_GAP_NOT_RECOVERABLE:GAP_TOO_LONG", feed.Session.Issues);
    }

    [Fact]
    public void Legacy_runs_without_a_session_policy_keep_the_original_void_on_any_gap()
    {
        var feed = WithOutage(QuoteSessionTests.Manifest(), 8);
        Assert.False(feed.Session.Checkpoint().Bars["future"].CompleteCoverage);
        Assert.DoesNotContain("FEED_GAP_RECOVERED_EXCHANGE_ATP", feed.Session.Issues);
        Assert.DoesNotContain(feed.Session.Issues, issue => issue.StartsWith("FEED_GAP_NOT_RECOVERABLE", StringComparison.Ordinal));
    }

    [Fact]
    public void Auto_arms_on_a_prior_run_waiver_and_rearms_after_a_clean_reconnect()
    {
        var feed = new Feed(V2(waiver: true));
        feed.Send("Connected", Open, []);
        feed.Run(0, 20);
        feed.Control(Open.AddSeconds(20), "arm");
        Assert.True(feed.Session.AutoEnabled);

        feed.Send("Disconnected", Open.AddSeconds(21), System.Text.Encoding.UTF8.GetBytes("FEED_WATCHDOG"));
        Assert.False(feed.Session.AutoEnabled); Assert.True(feed.Session.RearmPending);
        feed.Send("Connected", Open.AddSeconds(24), []);
        for (var second = 25; second <= 33; second++) { var time = Open.AddSeconds(second); feed.Tick(time); feed.Health(time); }
        Assert.False(feed.Session.AutoEnabled);
        for (var second = 34; second <= 40; second++) { var time = Open.AddSeconds(second); feed.Tick(time); feed.Health(time); }
        Assert.True(feed.Session.AutoEnabled); Assert.False(feed.Session.RearmPending);
        Assert.Contains("AUTO_REARMED_AFTER_RECOVERY", feed.Session.Issues);
    }

    [Fact]
    public void An_operator_pause_is_never_rearmed()
    {
        var feed = new Feed(V2(waiver: true));
        feed.Send("Connected", Open, []);
        feed.Run(0, 20);
        feed.Control(Open.AddSeconds(20), "arm");
        feed.Control(Open.AddSeconds(20.5), "pause");
        feed.Send("Disconnected", Open.AddSeconds(21), []);
        feed.Send("Connected", Open.AddSeconds(23), []);
        for (var second = 24; second <= 60; second++) { var time = Open.AddSeconds(second); feed.Tick(time); feed.Health(time); }
        Assert.False(feed.Session.AutoEnabled);
    }

    [Fact]
    public void Without_a_waiver_or_manual_lifecycle_arm_is_refused_and_legacy_never_rearms()
    {
        var feed = new Feed(V2());
        feed.Send("Connected", Open, []);
        feed.Run(0, 20);
        Assert.Throws<InvalidOperationException>(() => feed.Control(Open.AddSeconds(20), "arm"));
    }

    [Fact]
    public void A_one_second_paper_persistence_stall_blocks_entries_but_does_not_void_a_v2_session()
    {
        foreach (var (manifest, voided) in new[] { (V2(), false), (QuoteSessionTests.Manifest(), true) })
        {
            var feed = new Feed(manifest);
            feed.Send("Connected", Open, []);
            feed.Run(0, 10);
            feed.Send("Health", Open.AddSeconds(11), JsonSerializer.SerializeToUtf8Bytes(new QuoteRecorderHealth(true, 0), QuoteTradingSession.Json),
                new(Open.AddSeconds(11), true, 0, false, 0));
            Assert.False(feed.Session.RecorderHealthy);
            feed.Send("Health", Open.AddSeconds(12), JsonSerializer.SerializeToUtf8Bytes(new QuoteRecorderHealth(true, 0), QuoteTradingSession.Json),
                new(Open.AddSeconds(12), true, 0, true, 0));
            Assert.Equal(voided, feed.Session.Issues.Contains("PAPER_PERSISTENCE_NOT_HEALTHY_AT_INGRESS"));
        }
    }
}
