using System.Buffers.Binary;
using System.Text.Json;
using Trading.Domain;
using Trading.Engine;
using Trading.Replay;
using Trading.Risk;
using Xunit;

namespace Trading.Tests.Live;

public sealed class QuoteSessionTests
{
    public static readonly DateTimeOffset Open = DateTimeOffset.Parse("2026-10-21T03:45:00Z");
    public static QuoteRunManifest Manifest()
    {
        var list = new List<HistoricalInstrument>();
        HistoricalInstrument Make(string id, long token, HistoricalInstrumentRole role, decimal strike = 100) => new()
        { Id = id, InstrumentToken = token, Role = role, IndexId = "BANKNIFTY", Exchange = "NFO", Expiry = new(2026, 10, 27),
            Strike = strike, LotSize = 30, TickSize = .01m, Verified = true, DefinitionVersion = "fixture-v1",
            AvailableAtUtc = Open.AddMinutes(-1), ValidFromUtc = Open.AddDays(-1), ValidToUtc = Open.AddDays(1) };
        list.Add(Make("future", 258, HistoricalInstrumentRole.Futures));
        list.Add(Make("bank", 513, HistoricalInstrumentRole.Constituent));
        for (var strike = 93; strike <= 107; strike++)
        {
            list.Add(Make("call-" + strike, strike * 512 + 2, HistoricalInstrumentRole.Call, strike));
            list.Add(Make("put-" + strike, strike * 512 + 258, HistoricalInstrumentRole.Put, strike));
        }
        var settings = new EngineConfiguration { PaperSimulation = new() { Version = "quote-fill-test-v1", EntryAdverseSlippage = .01m, ExitAdverseSlippage = .01m } };
        return new("fixture", new(2026, 10, 21), Open.AddMinutes(-1),
            new("BANKNIFTY", "Bank Nifty fixture", "SYNTHETIC", "Asia/Kolkata", "Banks", "fixture-v1", Open.AddDays(-1), null, true, new(true, true, true)), list,
            new("BANKNIFTY", "fixture-weights", "synthetic", Open.AddDays(-1), null, Open.AddDays(-1), true, [new("bank", 100)]),
            settings.ToJson(), new() { ProfileId = "paper-fees", Version = "test-fees", Source = "test worked example",
                AvailableAtUtc = Open.AddDays(-1), EffectiveFromUtc = Open.AddDays(-1), BrokeragePerOrder = 1 }, "test-build", []);
    }

    public static byte[] Packet(long token, DateTimeOffset time, decimal last, long volume, decimal bid = 1, decimal ask = 1.01m, int depth = 30)
    {
        var message = new byte[188]; BinaryPrimitives.WriteUInt16BigEndian(message, 1); BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 184);
        void Set(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(offset + 4), value);
        Set(0, (uint)token); Set(4, (uint)(last * 100)); Set(16, (uint)volume);
        Set(44, (uint)time.ToUnixTimeSeconds()); Set(60, (uint)time.ToUnixTimeSeconds());
        Set(64, (uint)depth); Set(68, (uint)(bid * 100)); Set(124, (uint)depth); Set(128, (uint)(ask * 100)); return message;
    }

    public static IReadOnlyList<RecordedMarketEvent> Lifecycle()
    {
        var events = new List<RecordedMarketEvent>();
        void Add(string kind, DateTimeOffset time, byte[] bytes) => events.Add(new("fixture", events.Count + 1, time, "test", kind, bytes));
        void Quote(long token, DateTimeOffset time, decimal price, int volume, decimal bid = 1, decimal ask = 1.01m) => Add("Binary", time, Packet(token, time, price, volume, bid, ask));
        void Health(DateTimeOffset time) => Add("Health", time, JsonSerializer.SerializeToUtf8Bytes(new QuoteRecorderHealth(true, events.Count), QuoteTradingSession.Json));
        Add("Connected", Open, []);
        for (var second = 0; second <= 900; second++)
        {
            var time = Open.AddSeconds(second);
            Quote(258, time, second % 60 == 1 ? 80 : second % 60 == 2 ? 120 : 100, second + 1);
            Quote(513, time, 100, second + 1);
        }
        var now = Open.AddMinutes(15);
        Quote(51202, now, 1, 100);
        Health(now);
        Add("Control", now, JsonSerializer.SerializeToUtf8Bytes(new QuoteControl("manual", "manual-test", "call-100"), QuoteTradingSession.Json));
        Quote(258, now.AddMilliseconds(300), 100, 902);
        Quote(51202, now.AddMilliseconds(300), 1, 101);
        Add("Control", now.AddMilliseconds(400), JsonSerializer.SerializeToUtf8Bytes(new QuoteControl("exit", "exit-1"), QuoteTradingSession.Json));
        Quote(51202, now.AddMilliseconds(700), 2, 102, 2, 2.01m);
        return events;
    }

    [Fact]
    public void Recorded_quote_lifecycle_replays_identically_without_future_bars_or_candle_fills()
    {
        var events = Lifecycle(); var first = new QuoteTradingSession(Manifest()); var second = new QuoteTradingSession(Manifest());
        foreach (var item in events) { first.Apply(item); second.Apply(item); }
        var trade = Assert.Single(first.Paper.Trades);
        Assert.Equal(1.02m, trade.EntryPrice); Assert.Equal(1.99m, trade.ExitPrice);
        Assert.Equal(27.10m, trade.NetPnl);
        Assert.Equal(15, first.FuturesBars.Count);
        Assert.All(first.FuturesBars, bar => Assert.True(bar.EndUtc <= first.CurrentTimeUtc && bar.AvailableAtUtc >= bar.EndUtc));
        Assert.Equal(JsonSerializer.Serialize(first.Checkpoint(), QuoteTradingSession.Json), JsonSerializer.Serialize(second.Checkpoint(), QuoteTradingSession.Json));
        var restored = new QuoteTradingSession(Manifest(), first.Checkpoint());
        Assert.False(restored.AutoEnabled); Assert.False(restored.FeedConnected);
        Assert.Equal(first.Paper.Ledger, restored.Paper.Ledger);
    }

    [Fact]
    public void Gap_blocks_strategy_and_a_stale_option_cannot_fill_a_pending_exit()
    {
        var session = new QuoteTradingSession(Manifest()); var events = Lifecycle();
        foreach (var item in events.Take(events.Count - 1)) session.Apply(item);
        Assert.NotNull(session.Paper.Position);
        var last = events[^1];
        session.Apply(last with { Kind = "Disconnected", Payload = [] });
        session.Apply(last with { Sequence = last.Sequence + 1, ReceivedAtUtc = last.ReceivedAtUtc.AddSeconds(6) });
        Assert.NotNull(session.Paper.Position);
        Assert.NotNull(session.Paper.Position!.ExitRequestedAtUtc);
        Assert.False(session.AutoEnabled); Assert.True(session.RecordingIncomplete);
    }

    [Fact]
    public void Held_option_outage_requests_exit_even_while_future_and_recorder_remain_healthy()
    {
        var session = new QuoteTradingSession(Manifest()); var events = Lifecycle();
        foreach (var item in events.Take(events.Count - 2)) session.Apply(item);
        Assert.NotNull(session.Paper.Position);
        var sequence = session.LastSequence; var time = session.CurrentTimeUtc!.Value;
        for (var second = 1; second <= 9; second++)
        {
            var now = time.AddSeconds(second);
            session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(258, now, 100, 1000 + second)));
            session.Apply(new("fixture", ++sequence, now, "test", "Health", JsonSerializer.SerializeToUtf8Bytes(new QuoteRecorderHealth(true, sequence - 1), QuoteTradingSession.Json)));
        }
        Assert.Equal("DATA_OR_RECORDER_LOSS", session.Paper.Position!.ExitReason);
        Assert.Empty(session.Paper.Trades);
    }

    [Fact]
    public void Recovery_reconstructs_durable_market_tail_without_retrospective_entry_fills()
    {
        var events = Lifecycle(); var original = new QuoteTradingSession(Manifest());
        foreach (var item in events.Take(events.Count - 4)) original.Apply(item);
        Assert.NotNull(original.Paper.Pending);
        var recovered = new QuoteTradingSession(Manifest(), original.Checkpoint());
        recovered.RecoverTail(events);
        Assert.Null(recovered.Paper.Position); Assert.Empty(recovered.Paper.Trades);
        Assert.Equal(events[^1].Sequence, recovered.LastSequence);
        Assert.Equal(15, recovered.FuturesBars.Count);
        Assert.False(recovered.FeedConnected); Assert.False(recovered.AutoEnabled);
        Assert.Contains("RECOVERY_TAIL_MARKET_ONLY_NO_RETROACTIVE_FILLS", recovered.Issues);
    }

    [Fact]
    public void Pending_entry_is_canceled_before_processing_a_quote_after_entry_cutoff()
    {
        var events = Lifecycle(); var original = new QuoteTradingSession(Manifest());
        foreach (var item in events.Take(events.Count - 4)) original.Apply(item);
        var checkpoint = original.Checkpoint();
        var cutoff = Open.AddHours(2).AddMinutes(15);
        var approval = checkpoint.Paper.Pending! with { AcceptedAtUtc = cutoff.AddMilliseconds(-100), AcceptedSequence = checkpoint.LastSequence };
        var session = new QuoteTradingSession(Manifest(), checkpoint with { Paper = checkpoint.Paper with { Pending = approval, Attempts = [approval] } });
        var seq = session.LastSequence;
        session.Apply(new("fixture", ++seq, cutoff.AddMilliseconds(300), "test", "Connected", []));
        session.Apply(new("fixture", ++seq, cutoff.AddMilliseconds(300), "test", "Binary", Packet(51202, cutoff.AddMilliseconds(300), 1, 200)));
        Assert.Null(session.Paper.Pending); Assert.Null(session.Paper.Position); Assert.Empty(session.Paper.Trades);
    }

    [Fact]
    public void Auto_uses_existing_breakout_bank_confirmation_and_risk_gates_after_manual_validation()
    {
        var manifest = Manifest();
        // A separate synthetic policy makes a high-premium fixture feasible. Production
        // ₹400 is not widened: this tests integration, not policy profitability.
        var settings = EngineConfiguration.FromJson(manifest.EngineJson);
        settings = settings with { Version = "synthetic-auto-integration", Strategies = [settings.Strategies[0] with
            { Risk = settings.Strategies[0].Risk with { MaximumNetRisk = 1500m } }] };
        var session = new QuoteTradingSession(manifest with { EngineJson = settings.ToJson() });
        foreach (var item in Lifecycle()) session.Apply(item);
        Assert.Single(session.Paper.Trades);
        var sequence = session.LastSequence;
        var time = session.CurrentTimeUtc!.Value;
        session.Apply(new("fixture", ++sequence, time, "test", "Control", JsonSerializer.SerializeToUtf8Bytes(new QuoteControl("arm", "arm"), QuoteTradingSession.Json)));
        for (var second = 1; second <= 61; second++)
        {
            var now = Open.AddMinutes(15).AddSeconds(second);
            session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(258, now, 121, 1000 + second)));
            session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(513, now, 110, 1000 + second)));
            session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(54786, now, 14.12m, 1000 + second, 14.10m, 14.15m)));
            session.Apply(new("fixture", ++sequence, now, "test", "Health", JsonSerializer.SerializeToUtf8Bytes(new QuoteRecorderHealth(true, sequence - 1), QuoteTradingSession.Json)));
        }
        Assert.Equal("STRATEGY", session.Paper.Position?.Approval.Origin);
        Assert.Equal(2, session.Paper.Attempts.Count);
    }

    [Fact]
    public void A_quiet_constituent_keeps_bar_coverage_while_the_feed_stays_alive()
    {
        var session = new QuoteTradingSession(Manifest()); var sequence = 0L;
        session.Apply(new("fixture", ++sequence, Open, "test", "Connected", []));
        for (var second = 1; second <= 120; second++)
        {
            var now = Open.AddSeconds(second);
            session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(258, now, 100, second)));
            if (second is 1 or 40 or 100) session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(513, now, 100, second)));
        }
        var bars = session.Checkpoint().Bars;
        Assert.True(bars["bank"].CompleteCoverage); Assert.True(bars["future"].CompleteCoverage);
        Assert.DoesNotContain("FEED_SILENCE", session.Issues);
    }

    [Fact]
    public void Feed_wide_silence_in_session_hours_voids_bar_coverage_for_every_instrument()
    {
        var session = new QuoteTradingSession(Manifest()); var sequence = 0L;
        session.Apply(new("fixture", ++sequence, Open, "test", "Connected", []));
        foreach (var second in new[] { 1, 2, 3, 4, 12, 13 })
        {
            var now = Open.AddSeconds(second);
            session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(258, now, 100, second)));
            session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(513, now, 100, second)));
        }
        var bars = session.Checkpoint().Bars;
        Assert.False(bars["bank"].CompleteCoverage); Assert.False(bars["future"].CompleteCoverage);
        Assert.Contains("FEED_SILENCE", session.Issues);
    }

    [Fact]
    public void Silence_before_the_session_opens_does_not_void_bar_coverage()
    {
        var session = new QuoteTradingSession(Manifest()); var sequence = 0L;
        var early = Open.AddMinutes(-30);
        session.Apply(new("fixture", ++sequence, early, "test", "Binary", Packet(258, early, 100, 1)));
        session.Apply(new("fixture", ++sequence, Open.AddSeconds(1), "test", "Binary", Packet(258, Open.AddSeconds(1), 100, 2)));
        Assert.True(session.Checkpoint().Bars["future"].CompleteCoverage);
        Assert.DoesNotContain("FEED_SILENCE", session.Issues);
    }

    [Fact]
    public void An_unhealthy_recorder_admission_voids_bar_coverage_in_the_original_replay_but_not_in_a_what_if()
    {
        QuoteTradingSession Run(bool assumeArmed)
        {
            var session = new QuoteTradingSession(Manifest(), assumeArmed: assumeArmed); var sequence = 0L;
            session.Apply(new("fixture", ++sequence, Open, "test", "Connected", []));
            for (var second = 1; second <= 3; second++)
            {
                var now = Open.AddSeconds(second);
                var admission = new MarketAdmission(now, second != 2, sequence, true, sequence);
                session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(258, now, 100, second), admission));
            }
            return session;
        }

        Assert.False(Run(false).Checkpoint().Bars["future"].CompleteCoverage);
        var whatIf = Run(true);
        Assert.True(whatIf.Checkpoint().Bars["future"].CompleteCoverage);
        Assert.Contains("RECORDER_NOT_HEALTHY_AT_INGRESS", whatIf.Issues);
    }

    [Fact]
    public void A_disconnect_before_the_session_starts_keeps_bar_coverage_but_one_during_it_voids_it()
    {
        QuoteTradingSession Run(DateTimeOffset disconnectAt)
        {
            var session = new QuoteTradingSession(Manifest()); var sequence = 0L;
            session.Apply(new("fixture", ++sequence, Open.AddMinutes(-9), "test", "Connected", []));
            session.Apply(new("fixture", ++sequence, disconnectAt, "test", "Disconnected", []));
            session.Apply(new("fixture", ++sequence, disconnectAt.AddSeconds(1), "test", "Connected", []));
            var now = disconnectAt > Open ? disconnectAt.AddSeconds(2) : Open.AddSeconds(2);
            session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(258, now, 100, 1)));
            return session;
        }

        Assert.True(Run(Open.AddMinutes(-8)).Checkpoint().Bars["future"].CompleteCoverage);
        Assert.False(Run(Open.AddMinutes(1)).Checkpoint().Bars["future"].CompleteCoverage);
    }

    [Fact]
    public void A_tick_stamped_inside_a_minute_but_received_just_after_it_ends_stays_in_that_bar_without_a_gap()
    {
        var session = new QuoteTradingSession(Manifest()); var sequence = 0L;
        session.Apply(new("fixture", ++sequence, Open, "test", "Connected", []));
        for (var second = 1; second <= 59; second++)
        {
            var now = Open.AddSeconds(second);
            session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(258, now, 100, second)));
        }
        // Another instrument's packet moves the host clock past the boundary before the straggler arrives.
        var boundary = Open.AddSeconds(60).AddMilliseconds(100);
        session.Apply(new("fixture", ++sequence, boundary, "test", "Binary", Packet(513, boundary, 100, 1)));
        // Stamped 09:15:59 by the exchange, received 400 ms into the next minute.
        var late = Open.AddSeconds(60).AddMilliseconds(400);
        session.Apply(new("fixture", ++sequence, late, "test", "Binary", Packet(258, Open.AddSeconds(59), 101, 60)));
        var next = Open.AddSeconds(61);
        session.Apply(new("fixture", ++sequence, next, "test", "Binary", Packet(258, next, 101, 61)));
        var bars = session.Checkpoint().Bars[session.Future.Id];
        Assert.True(bars.CompleteCoverage);
        Assert.Equal(101m, bars.Completed.Single().Close);
    }

    [Fact]
    public void What_if_replay_trades_the_strategy_without_a_recorded_arm_while_the_original_replay_does_not()
    {
        var manifest = Manifest();
        var settings = EngineConfiguration.FromJson(manifest.EngineJson);
        settings = settings with { Version = "synthetic-auto-integration", Strategies = [settings.Strategies[0] with
            { Risk = settings.Strategies[0].Risk with { MaximumNetRisk = 1500m } }] };
        QuoteTradingSession Run(bool assumeArmed)
        {
            var session = new QuoteTradingSession(manifest with { EngineJson = settings.ToJson() }, assumeArmed: assumeArmed);
            // The recording never armed paper-auto, and its recorder reported unhealthy once.
            var number = 0L;
            foreach (var item in Lifecycle().Where(item => item.Kind != "Control")) session.Apply(item with { Sequence = ++number });
            var sequence = session.LastSequence;
            session.Apply(new("fixture", ++sequence, session.CurrentTimeUtc!.Value, "test", "Health", JsonSerializer.SerializeToUtf8Bytes(new QuoteRecorderHealth(false, sequence - 1), QuoteTradingSession.Json)));
            for (var second = 1; second <= 61; second++)
            {
                var now = Open.AddMinutes(15).AddSeconds(second);
                session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(258, now, 121, 1000 + second)));
                session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(513, now, 110, 1000 + second)));
                session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(54786, now, 14.12m, 1000 + second, 14.10m, 14.15m)));
                session.Apply(new("fixture", ++sequence, now, "test", "Health", JsonSerializer.SerializeToUtf8Bytes(new QuoteRecorderHealth(true, sequence - 1), QuoteTradingSession.Json)));
            }
            return session;
        }

        var original = Run(false); var whatIf = Run(true);

        Assert.Empty(original.Paper.Attempts); Assert.False(original.AutoEnabled);
        Assert.Equal("STRATEGY", whatIf.Paper.Position?.Approval.Origin);
        Assert.True(whatIf.OverriddenRecordingGaps); Assert.False(whatIf.RecordingIncomplete);
    }

    [Fact]
    public void Processing_delay_cannot_turn_an_early_received_quote_into_a_delayed_fill()
    {
        var events = Lifecycle(); var session = new QuoteTradingSession(Manifest());
        foreach (var item in events.Take(events.Count - 4)) session.Apply(item);
        var intentTime = session.CurrentTimeUtc!.Value;
        var received = intentTime.AddMilliseconds(100);
        var available = intentTime.AddMilliseconds(400);
        session.Apply(new("fixture", session.LastSequence + 1, received, "test", "Binary", Packet(51202, received, 1, 200),
            new(available, true, long.MaxValue, true, long.MaxValue)));
        Assert.Null(session.Paper.Position); Assert.NotNull(session.Paper.Pending);
        Assert.Equal(received, session.Quotes.Single(tick => tick.InstrumentToken == 51202).ReceivedAtUtc);
        Assert.Equal(available, session.CurrentTimeUtc);
    }

    [Fact]
    public void Persistence_failure_still_advances_protection_and_requests_an_unfilled_exit()
    {
        var events = Lifecycle(); var session = new QuoteTradingSession(Manifest());
        foreach (var item in events.Take(events.Count - 2)) session.Apply(item);
        var sequence = session.LastSequence; var start = session.CurrentTimeUtc!.Value;
        for (var second = 1; second <= 7; second++)
        {
            var now = start.AddSeconds(second);
            session.Apply(new("fixture", ++sequence, now, "test", "Binary", Packet(258, now, 100, 2000 + second),
                new(now, true, sequence - 1, false, 0)));
        }
        Assert.Equal("DATA_OR_RECORDER_LOSS", session.Paper.Position!.ExitReason);
        Assert.False(session.AutoEnabled); Assert.Empty(session.Paper.Trades);
    }

    [Fact]
    public void Recorded_recovery_boundary_discards_a_market_durable_but_unacknowledged_fill_in_original_replay()
    {
        var events = Lifecycle(); var live = new QuoteTradingSession(Manifest());
        foreach (var item in events.Take(events.Count - 4)) live.Apply(item);
        var acknowledged = live.Checkpoint();
        var durableSource = events.Take(events.Count - 2).ToArray();
        foreach (var item in durableSource.Where(item => item.Sequence > live.LastSequence)) live.Apply(item);
        Assert.NotNull(live.Paper.Position); // fill was computed, but its checkpoint never acknowledged
        var recovered = new QuoteTradingSession(Manifest(), acknowledged);
        recovered.RecoverTail(durableSource);
        var boundary = new RecordedMarketEvent("fixture", durableSource[^1].Sequence + 1, durableSource[^1].ReceivedAtUtc.AddMilliseconds(50),
            "recovered", "Recovered", JsonSerializer.SerializeToUtf8Bytes(recovered.Checkpoint(), QuoteTradingSession.Json));
        recovered.Apply(boundary);
        var replay = new QuoteTradingSession(Manifest());
        foreach (var item in durableSource) replay.Apply(item);
        replay.Apply(boundary);
        Assert.Null(replay.Paper.Position);
        Assert.Equal(JsonSerializer.Serialize(recovered.Paper), JsonSerializer.Serialize(replay.Paper));
    }
}
