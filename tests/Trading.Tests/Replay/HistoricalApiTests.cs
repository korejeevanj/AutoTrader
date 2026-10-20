using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Trading.Api;
using Trading.Data;
using Trading.Domain;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class HistoricalApiTests
{
    [Fact]
    public async Task Retry_requested_after_failure_is_published_but_before_acquisition_cleanup_is_not_dropped()
    {
        using var clock = new FailureCleanupClock();
        await using var fixture = new HistoricalApiFixture { Clock = clock };
        var providerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Override = async (request, token) =>
        {
            providerEntered.TrySetResult(); await providerRelease.Task.WaitAsync(token);
            var id = long.Parse(request.RequestUri!.AbsolutePath.Split('/')[3], System.Globalization.CultureInfo.InvariantCulture);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Handler.Data[id]) };
        };
        fixture.Start(); var job = await fixture.Prepare();
        await providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.BlockWhen = () =>
        {
            var view = fixture.ReplayService.ViewAsync(job.Id).GetAwaiter().GetResult();
            return view?.Job.State == "Failed" && view.Job.PreparingData;
        };
        var checkpoint = Path.Combine(fixture.Root, "jobs", job.Id, "job.json");
        try
        {
            File.SetAttributes(checkpoint, FileAttributes.ReadOnly);
            providerRelease.TrySetResult();
            await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var failing = await fixture.View(job.Id);
            Assert.Equal("Failed", failing.Job.State);
            Assert.True(failing.Job.PreparingData);
            File.SetAttributes(checkpoint, FileAttributes.Normal);
            await fixture.Command(job.Id, "retry");
        }
        finally
        {
            File.SetAttributes(checkpoint, FileAttributes.Normal);
            providerRelease.TrySetResult(); clock.Release.Set();
        }
        fixture.Handler.Override = null;
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay && view.Job.PreparationComplete && view.Job.Attempt >= 2);
        Assert.Null(recovered.DiagnosticError);
        Assert.Equal(1, fixture.Handler.Requests.Count(request => request.Path.Contains("/260105/", StringComparison.Ordinal)));
    }

    private sealed class FailureCleanupClock : TimeProvider, IDisposable
    {
        private int blocked;
        public Func<bool>? BlockWhen { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public override DateTimeOffset GetUtcNow()
        {
            if (BlockWhen?.Invoke() == true && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Failure cleanup was not released by the test.");
            }
            return new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);
        }
        public void Dispose() => Release.Dispose();
    }

    [Fact]
    public async Task Missing_intervals_exclude_minutes_outside_a_verified_contract_validity_window()
    {
        await using var fixture = new HistoricalApiFixture();
        var catalogue = HistoricalAcquisitionStore.Read<HistoricalCatalogDocument>(fixture.Catalog)!;
        var validFrom = new DateTimeOffset(2026,10,9,3,50,0,TimeSpan.Zero); // 09:20 IST
        await HistoricalAcquisitionStore.AtomicJsonAsync(fixture.Catalog, catalogue with { Manifest = catalogue.Manifest with
            { Instruments = catalogue.Manifest.Instruments.Select(item => item.Id == "call" ? item with { ValidFromUtc = validFrom } : item).ToArray() } }, CancellationToken.None);
        using var raw = JsonDocument.Parse(fixture.Handler.Data[12656898]);
        var rows = raw.RootElement.GetProperty("data").GetProperty("candles").EnumerateArray().Skip(5).Where((_, i) => i != 5).ToArray();
        fixture.Handler.Data[12656898] = JsonSerializer.SerializeToUtf8Bytes(new { status = "success", data = new { candles = rows } });
        fixture.Start(); var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        var health = (await fixture.Client.GetFromJsonAsync<ReplayDataHealth>($"/api/replay/jobs/{job.Id}/data-health"))!;
        var option = health.Instruments.Single(row => row.Id == "call");
        Assert.Equal(1, option.MissingMinutes);
        Assert.Equal(1d, option.MissingIntervals.Sum(interval => (interval.EndUtc - interval.StartUtc).TotalMinutes));
        Assert.All(option.MissingIntervals, interval => Assert.True(interval.StartUtc >= validFrom));
    }

    [Fact]
    public async Task Intraday_weight_replacements_are_retained_and_available_only_at_their_effective_time()
    {
        await using var fixture = new HistoricalApiFixture();
        var catalogue = HistoricalAcquisitionStore.Read<HistoricalCatalogDocument>(fixture.Catalog)!;
        var boundary = new DateTimeOffset(2026, 10, 9, 4, 2, 0, TimeSpan.Zero);
        var before = catalogue.Manifest.Weights[0] with { EffectiveToUtc = boundary };
        var after = before with { Version = "replacement-weights", EffectiveFromUtc = boundary, EffectiveToUtc = null };
        await HistoricalAcquisitionStore.AtomicJsonAsync(fixture.Catalog, catalogue with { Manifest = catalogue.Manifest with { Weights = [before, after] } }, CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        var manifest = HistoricalAcquisitionStore.Read<HistoricalDatasetManifest>(Path.Combine(fixture.Root, "jobs", job.Id, "attempt-1", "dataset.json"))!;
        Assert.Equal(2, manifest.Weights.Count);
        var health = (await fixture.Client.GetFromJsonAsync<ReplayDataHealth>($"/api/replay/jobs/{job.Id}/data-health"))!;
        Assert.True(health.Qualification!.Qualified);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_unqualified_expiry_cannot_remove_an_option_before_readiness_checks(bool missingExpiry)
    {
        await using var fixture = new HistoricalApiFixture();
        var catalogue = HistoricalAcquisitionStore.Read<HistoricalCatalogDocument>(fixture.Catalog)!;
        var option = catalogue.Manifest.Instruments.Single(item => item.Role == HistoricalInstrumentRole.Call) with
            { Id = "unresolved-expiry", InstrumentToken = 999998, Verified = missingExpiry,
                Expiry = missingExpiry ? null : ReplayFixture.Date.AddDays(-1) };
        fixture.Handler.Data[option.InstrumentToken] = fixture.Handler.Data[12656898];
        await HistoricalAcquisitionStore.AtomicJsonAsync(fixture.Catalog, catalogue with { Manifest = catalogue.Manifest with
            { Instruments = catalogue.Manifest.Instruments.Append(option).ToArray() } }, CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare();
        var ready = await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        Assert.Contains(ready.Job.Instruments, row => row.Id == option.Id);
        var health = await fixture.Client.GetFromJsonAsync<JsonElement>($"/api/replay/jobs/{job.Id}/data-health");
        Assert.False(health.GetProperty("qualification").GetProperty("qualified").GetBoolean());
    }

    [Fact]
    public async Task Failed_retry_checkpoint_can_be_retried_after_storage_is_restored()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        var path = Path.Combine(fixture.Root, "jobs", job.Id, "job.json");
        try
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
            using var response = await fixture.Client.PostAsJsonAsync($"/api/replay/jobs/{job.Id}/commands", new HistoricalReplayCommand("retry"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("Failed", (await fixture.View(job.Id)).Job.State);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        await fixture.Command(job.Id, "retry");
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete && view.Job.CanReplay && view.Job.Attempt >= 2);
    }

    [Fact]
    public async Task Frozen_recovery_ignores_later_calendar_changes()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        var calendarPath = Path.Combine(fixture.Root, "calendars", "2026.json");
        var calendar = HistoricalAcquisitionStore.Read<ExchangeCalendarDocument>(calendarPath)!;
        await HistoricalAcquisitionStore.AtomicJsonAsync(calendarPath, calendar with { SpecialSessions = [], ClosedDates = [ReplayFixture.Date] }, CancellationToken.None);
        await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.State == "MarketClosed" || view.Job.CanReplay && view.Job.Attempt >= 2);
        Assert.True(recovered.Job.CanReplay);
        Assert.Equal(18, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task End_of_day_only_option_identities_stay_in_the_declared_universe_as_unknown()
    {
        await using var fixture = new HistoricalApiFixture();
        var catalogue = HistoricalAcquisitionStore.Read<HistoricalCatalogDocument>(fixture.Catalog)!;
        var option = catalogue.Manifest.Instruments.Single(item => item.Role == HistoricalInstrumentRole.Call) with
            { Id = "late-identity", InstrumentToken = 999999, Verified = false,
                AvailableAtUtc = new(2026, 10, 9, 4, 6, 0, TimeSpan.Zero) }; // 09:36 close
        fixture.Handler.Data[option.InstrumentToken] = fixture.Handler.Data[12656898];
        await HistoricalAcquisitionStore.AtomicJsonAsync(fixture.Catalog, catalogue with { Manifest = catalogue.Manifest with
            { Instruments = catalogue.Manifest.Instruments.Append(option).ToArray() } }, CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare();
        var ready = await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        Assert.Contains(ready.Job.Instruments, row => row.Id == option.Id);
        var health = await fixture.Client.GetFromJsonAsync<JsonElement>($"/api/replay/jobs/{job.Id}/data-health");
        Assert.False(health.GetProperty("qualification").GetProperty("qualified").GetBoolean());
    }

    [Fact]
    public async Task Missing_required_tail_blocks_dataset_qualification_even_after_the_entry_window()
    {
        await using var fixture = new HistoricalApiFixture();
        using var raw = JsonDocument.Parse(fixture.Handler.Data[12466946]);
        var rows = raw.RootElement.GetProperty("data").GetProperty("candles").EnumerateArray().SkipLast(1).ToArray();
        fixture.Handler.Data[12466946] = JsonSerializer.SerializeToUtf8Bytes(new { status = "success", data = new { candles = rows } });
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        var health = await fixture.Client.GetFromJsonAsync<JsonElement>($"/api/replay/jobs/{job.Id}/data-health");
        Assert.False(health.GetProperty("qualification").GetProperty("qualified").GetBoolean());
    }

    [Fact]
    public async Task Data_health_and_final_report_agree_that_complete_candles_with_unqualified_weights_are_incomplete()
    {
        await using var fixture = new HistoricalApiFixture();
        var catalogue = HistoricalAcquisitionStore.Read<HistoricalCatalogDocument>(fixture.Catalog)!;
        await HistoricalAcquisitionStore.AtomicJsonAsync(fixture.Catalog, catalogue with { Manifest = catalogue.Manifest with
            { Weights = [catalogue.Manifest.Weights[0] with { PublicationEvidence = null }] } }, CancellationToken.None);
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        var health = await fixture.Client.GetFromJsonAsync<JsonElement>($"/api/replay/jobs/{job.Id}/data-health");
        Assert.Contains("Incomplete", health.GetProperty("readiness").GetString());
        Assert.False(health.GetProperty("qualification").GetProperty("qualified").GetBoolean());
        await fixture.Command(job.Id, "run");
        var done = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        var report = await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/reports/{done.Job.Run}/summary.md");
        Assert.Contains("WEIGHTS_PUBLICATION_UNQUALIFIED", report);
        Assert.Contains("0 complete sessions", report);
        var runs = await fixture.Client.GetFromJsonAsync<JsonElement>($"/api/replay/runs?date={ReplayFixture.Date:yyyy-MM-dd}");
        var run = Assert.Single(runs.EnumerateArray());
        Assert.True(run.GetProperty("complete").GetBoolean());
        Assert.False(run.GetProperty("qualified").GetBoolean());
    }

    [Fact]
    public async Task Refreshing_coverage_gaps_refetches_empty_series_and_preserves_the_previous_attempt()
    {
        await using var fixture = new HistoricalApiFixture();
        var full = fixture.Handler.Data[12657154];
        fixture.Handler.Data[12657154] = Encoding.UTF8.GetBytes("{\"status\":\"success\",\"data\":{\"candles\":[]}}");
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        var oldFiles = Directory.GetFiles(Path.Combine(fixture.Root, "jobs", job.Id, "attempt-1"), "*.json")
            .ToDictionary(path => path, path => KiteCandleImporter.Hash(File.ReadAllBytes(path)));
        fixture.Handler.Data[12657154] = full;
        await fixture.Command(job.Id, "refreshGaps");
        var ready = await fixture.Wait(job.Id, view => view.Job.PreparationComplete && view.Job.Attempt == 2);
        Assert.All(ready.Job.Instruments, row => Assert.Equal("Complete", row.State));
        Assert.Equal(19, fixture.Handler.Requests.Count);
        Assert.All(oldFiles, pair => Assert.Equal(pair.Value, KiteCandleImporter.Hash(File.ReadAllBytes(pair.Key))));
    }

    [Fact]
    public async Task Persistent_checkpoint_failure_is_visible_and_does_not_kill_the_acquisition_worker()
    {
        await using var fixture = new HistoricalApiFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Override = async (request, token) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token);
            var id = long.Parse(request.RequestUri!.AbsolutePath.Split('/')[3], System.Globalization.CultureInfo.InvariantCulture);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Handler.Data[id]) };
        };
        fixture.Start(); var job = await fixture.Prepare();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var checkpoint = Path.Combine(fixture.Root, "jobs", job.Id, "job.json");
        try
        {
            File.SetAttributes(checkpoint, FileAttributes.ReadOnly); release.TrySetResult();
            var failed = await fixture.Wait(job.Id, view => view.Job.State == "Failed");
            Assert.Contains("LOCAL_STORAGE_UNAVAILABLE", failed.Job.Warnings);
            Assert.NotNull(failed.DiagnosticError);
        }
        finally { File.SetAttributes(checkpoint, FileAttributes.Normal); release.TrySetResult(); }
        fixture.Handler.Override = null;
        await fixture.Command(job.Id, "retry");
        var recovered = await fixture.Wait(job.Id, view => view.Job.CanReplay && view.Job.PreparationComplete);
        Assert.Null(recovered.DiagnosticError);
        Assert.Equal(1, fixture.Handler.Requests.Count(request => request.Path.Contains("/260105/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Essential_index_futures_and_banks_are_acquired_before_the_option_catalogue()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.PreparationComplete);
        var tokens = fixture.Handler.Requests.Select(request => long.Parse(request.Path.Split('/')[3], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(260105, tokens[0]);
        Assert.Equal(12466946, tokens[1]);
        Assert.All(tokens[2..16], token => Assert.InRange(token, 2000000, 2000013));
        Assert.Equal(new long[] { 12656898, 12657154 }, tokens[16..]);
    }

    [Fact]
    public async Task Date_only_job_downloads_validates_and_replays_with_no_future_candles_or_real_orders()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var job = await fixture.Prepare();
        var ready = await fixture.Wait(job.Id, view => view.Job.CanReplay || view.Job.State == "Failed");
        Assert.True(ready.Job.CanReplay, ready.Job.Message);
        Assert.Empty(ready.Bars); Assert.Null(ready.SimulatedTimeUtc);
        Assert.Equal(18, ready.Job.Total); Assert.All(ready.Job.Instruments, item => Assert.Equal("Complete", item.State));
        var duplicate = await fixture.Prepare(); Assert.Equal(job.Id, duplicate.Id);
        Assert.Equal(18, fixture.Handler.Requests.Count);
        Assert.All(fixture.Handler.Requests, request =>
        {
            Assert.Equal("GET", request.Method); Assert.StartsWith("/instruments/historical/", request.Path);
            Assert.Equal("3", request.Version); Assert.Equal("token test-api-key:daily-test-token", request.Authorization); Assert.False(request.Cookie);
            var query = QueryHelpers.ParseQuery(request.Query);
            Assert.Equal("2026-10-09 09:15:00", query["from"]); Assert.Equal("2026-10-09 09:35:59", query["to"]);
            Assert.Equal("0", query["continuous"]); Assert.Equal("1", query["oi"]);
        });
        await fixture.Command(job.Id, "step");
        var step = await fixture.View(job.Id); Assert.Empty(step.Bars); Assert.NotNull(step.SimulatedTimeUtc);
        await fixture.Command(job.Id, "run");
        var complete = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable || view.Job.State == "Failed");
        Assert.True(complete.Job.ReportsAvailable, complete.Job.Message);
        Assert.All(complete.Bars, bar => { Assert.True(bar.EndUtc <= complete.SimulatedTimeUtc); Assert.True(bar.AvailableAtUtc <= complete.SimulatedTimeUtc); });
        Assert.Single(complete.Trades); Assert.Equal(-34m, complete.Trades[0].NetPnl);
        var report = await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}/reports/{complete.Job.Run}/summary.md");
        Assert.Contains("Hypothetical candle research", report);
        var serialized = await fixture.Client.GetStringAsync($"/api/replay/jobs/{job.Id}");
        Assert.DoesNotContain("daily-test-token", serialized); Assert.DoesNotContain("test-api-key", serialized);
    }

    [Fact]
    public async Task Confirmed_holiday_and_closed_day_retry_never_call_provider_and_future_dates_fail()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var closed = await fixture.Prepare(date: new(2026, 10, 8));
        Assert.Equal("MarketClosed", closed.State);
        await fixture.Command(closed.Id, "retry");
        await fixture.Wait(closed.Id, view => view.Job.State == "MarketClosed");
        Assert.Empty(fixture.Handler.Requests);
        using var invalid = await fixture.Client.PostAsJsonAsync("/api/replay/jobs", new { date = "2099-01-01" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Authentication_expiry_is_redacted_and_a_new_daily_token_retries_without_server_restart()
    {
        await using var fixture = new HistoricalApiFixture();
        fixture.Handler.Override = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("{\"status\":\"error\",\"error_type\":\"TokenException\",\"message\":\"daily-test-token\"}") });
        fixture.Start(); var job = await fixture.Prepare();
        var failed = await fixture.Wait(job.Id, view => view.Job.State == "AuthenticationRequired");
        Assert.DoesNotContain("daily-test-token", JsonSerializer.Serialize(failed));
        Assert.Single(fixture.Handler.Requests);
        fixture.Credentials = new("test-api-key", "renewed-test-token"); fixture.Handler.Override = null;
        await fixture.Command(job.Id, "retry");
        var ready = await fixture.Wait(job.Id, view => view.Job.CanReplay || view.Job.State == "Failed");
        Assert.True(ready.Job.CanReplay, ready.Job.Message);
        Assert.Contains(fixture.Handler.Requests, request => request.Authorization == "token test-api-key:renewed-test-token");
        var guide = await fixture.Client.GetStringAsync("/api/replay/setup");
        Assert.Contains("kite.credentials.Local.json", guide); Assert.DoesNotContain("renewed-test-token", guide);
    }

    [Fact]
    public async Task Refresh_retries_only_failed_instruments_and_cache_revisions_remain_immutable()
    {
        await using var fixture = new HistoricalApiFixture(); var failedToken = 2000003L;
        fixture.Handler.Override = (request, _) =>
        {
            var token = long.Parse(request.RequestUri!.AbsolutePath.Split('/')[3], System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(token == failedToken ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Handler.Data[token]) });
        };
        fixture.Start(); var job = await fixture.Prepare(refresh: true);
        await fixture.Wait(job.Id, view => view.Job.CanReplay || view.Job.State == "Failed");
        Assert.Equal(18, fixture.Handler.Requests.Count);
        fixture.Handler.Override = null; await fixture.Command(job.Id, "retry");
        var ready = await fixture.Wait(job.Id, view => view.Job.CanReplay && view.Job.Attempt >= 2);
        Assert.Equal(19, fixture.Handler.Requests.Count);
        Assert.Equal(17, ready.Job.Instruments.Count(item => item.Cached));
        Assert.All(ready.Job.Instruments, item => Assert.Equal("Complete", item.State));
    }

    [Fact]
    public async Task Recovery_pins_earlier_data_and_configuration_despite_a_newer_refreshed_revision()
    {
        await using var fixture = new HistoricalApiFixture(); fixture.Start();
        var original = await fixture.Prepare(); await fixture.Wait(original.Id, view => view.Job.CanReplay);
        var firstAttempt = Path.Combine(fixture.Root, "jobs", original.Id, "attempt-1");
        var originalManifest = HistoricalAcquisitionStore.Read<HistoricalDatasetManifest>(Path.Combine(firstAttempt, "dataset.json"))!;
        var firstFile = originalManifest.Instruments[0].File;
        var originalHash = KiteCandleImporter.Hash(File.ReadAllBytes(Path.Combine(firstAttempt, firstFile)));
        var target = originalManifest.Instruments[0];
        fixture.Handler.Data[target.InstrumentToken] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(fixture.Handler.Data[target.InstrumentToken]).Replace(",100,", ",101,"));
        var refreshed = await fixture.Prepare(refresh: true); await fixture.Wait(refreshed.Id, view => view.Job.CanReplay || view.Job.State == "Failed");
        File.WriteAllText(fixture.EnginePath, (fixture.Files.Engine with { Version = "new-not-recovered" }).ToJson());
        await fixture.Restart(); await fixture.Command(original.Id, "retry");
        var recovered = await fixture.Wait(original.Id, view => view.Job.CanReplay || view.Job.State == "Failed");
        Assert.True(recovered.Job.CanReplay, recovered.Job.Message);
        Assert.Equal("paper-policy-v1", recovered.Settings!.Version);
        var recoveredFile = Path.Combine(fixture.Root, "jobs", original.Id, "attempt-2", firstFile);
        Assert.Equal(originalHash, KiteCandleImporter.Hash(File.ReadAllBytes(recoveredFile)));
    }

    [Fact]
    public async Task Invalid_catalogue_fails_one_job_without_stopping_api_and_no_catalogue_downloads_index_only()
    {
        await using var fixture = new HistoricalApiFixture();
        File.Copy(fixture.Catalog, Path.Combine(Path.GetDirectoryName(fixture.Catalog)!, "conflict.json"));
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.State == "Failed");
        using var health = await fixture.Client.GetAsync("/api/health"); Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        File.Delete(Path.Combine(Path.GetDirectoryName(fixture.Catalog)!, "conflict.json")); File.Delete(fixture.Catalog);
        var partial = await fixture.Prepare(); var view = await fixture.Wait(partial.Id, view => view.Job.CanReplay);
        Assert.True(view.Job.CanReplay); Assert.Equal("PriceOnly", view.PlaybackKind);
        Assert.Single(view.Job.Instruments); Assert.Equal(260105, view.Job.Instruments[0].Token);
    }

    [Fact]
    public async Task Permission_denial_does_not_masquerade_as_an_expired_token()
    {
        await using var fixture = new HistoricalApiFixture();
        fixture.Handler.Override = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("{\"status\":\"error\",\"error_type\":\"PermissionException\"}") });
        fixture.Start(); var job = await fixture.Prepare();
        var view = await fixture.Wait(job.Id, view => view.Job.State == "Failed");
        Assert.Equal("ACCESS_DENIED", view.Job.Message);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Missing_historical_catalogue_still_allows_causal_index_playback_without_strategy_trades()
    {
        await using var fixture = new HistoricalApiFixture();
        File.Delete(fixture.Catalog); fixture.Start();
        var job = await fixture.Prepare();
        var ready = await fixture.Wait(job.Id, view => view.Job.CanReplay || view.Job.State is "NeedsMetadata" or "Failed");
        Assert.True(ready.Job.CanReplay, "Downloaded historical candles must remain replayable: " + ready.Job.Message);
        Assert.Contains("HISTORICAL_CATALOGUE_UNAVAILABLE", ready.Job.Warnings);
        Assert.Empty(ready.Bars);
        await fixture.Command(job.Id, "step"); await fixture.Command(job.Id, "step");
        var step = await fixture.View(job.Id);
        Assert.Single(step.Bars); Assert.Empty(step.Trades); Assert.Null(step.Feature?.Vwap);
        await fixture.Command(job.Id, "run");
        var complete = await fixture.Wait(job.Id, view => view.Job.ReportsAvailable || view.Job.State == "Failed");
        Assert.True(complete.Job.ReportsAvailable, complete.Job.Message);
        Assert.Empty(complete.Trades);
    }

    [Fact]
    public async Task All_strikes_and_expiries_download_and_assess_beyond_one_engine_batch()
    {
        await using var fixture = new HistoricalApiFixture();
        var catalogue = HistoricalAcquisitionStore.Read<HistoricalCatalogDocument>(fixture.Catalog)!;
        var call = catalogue.Manifest.Instruments.Single(item => item.Role == HistoricalInstrumentRole.Call);
        var extra = Enumerable.Range(0, 80).Select(i => call with { Id = $"far-call-{i:00}", InstrumentToken = 9000000 + i,
            Strike = 50000 + i * 100m, Expiry = i % 2 == 0 ? new(2026, 10, 29) : new(2026, 11, 26) }).ToArray();
        foreach (var option in extra) fixture.Handler.Data[option.InstrumentToken] = fixture.Handler.Data[call.InstrumentToken];
        File.WriteAllText(fixture.Catalog, JsonSerializer.Serialize(catalogue with { Manifest = catalogue.Manifest with
            { Instruments = catalogue.Manifest.Instruments.Concat(extra).ToArray() } }, KiteCandleImporter.JsonOptions));
        fixture.Start(); var job = await fixture.Prepare();
        var ready = await fixture.Wait(job.Id, view => view.Job.CanReplay || view.Job.State == "Failed");
        Assert.True(ready.Job.CanReplay, ready.Job.Message); Assert.Equal(98, ready.Job.Total);
        Assert.Equal(98, fixture.Handler.Requests.Count);
        for (var i = 0; i < 17; i++) await fixture.Command(job.Id, "step");
        var view = await fixture.View(job.Id);
        Assert.Contains(view.Decisions, row => row.ContractId == "far-call-79");
        Assert.DoesNotContain(view.Decisions, row => row.Reasons.Contains("BOUNDED_UNIVERSE"));
    }

    [Fact]
    public async Task Successful_empty_option_responses_are_coverage_not_repeated_download_failures()
    {
        await using var fixture = new HistoricalApiFixture();
        fixture.Handler.Data[12657154] = Encoding.UTF8.GetBytes("{\"status\":\"success\",\"data\":{\"candles\":[]}}");
        fixture.Start(); var job = await fixture.Prepare(); await fixture.Wait(job.Id, view => view.Job.CanReplay);
        var requests = fixture.Handler.Requests.Count;
        await fixture.Command(job.Id, "retry"); await fixture.Wait(job.Id, view => view.Job.CanReplay && view.Job.Attempt >= 2);
        Assert.Equal(requests, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task Selecting_a_completed_price_preview_resumes_its_unfinished_preparation()
    {
        await using var fixture = new HistoricalApiFixture(); File.Delete(fixture.Catalog);
        var archive = new BlockFirstArchive(); fixture.ExchangeHandler = archive;
        fixture.Start(); var job = await fixture.Prepare();
        await fixture.Wait(job.Id, view => view.Job.CanReplay);
        await archive.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Command(job.Id, "run");
        await fixture.Wait(job.Id, view => view.Job.ReportsAvailable);
        await fixture.Command(job.Id, "cancelPreparation");
        await fixture.Wait(job.Id, view => !view.Job.PreparingData);
        var selectedAgain = await fixture.Prepare();
        Assert.Equal(job.Id, selectedAgain.Id);
        Assert.NotEqual("Completed", selectedAgain.State);
        await fixture.Wait(job.Id, view => !view.Job.PreparingData && view.Job.Attempt >= 2);
        Assert.True(archive.Calls >= 2);
    }

    private sealed class BlockFirstArchive : HttpMessageHandler
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 1) { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            return new(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Distinct_trading_days_prepare_and_run_with_their_own_candles_clocks_and_ledgers()
    {
        await using var fixture = new HistoricalApiFixture(new(2026,10,9), new(2026,10,16)); fixture.Start();
        var keys = new List<string>();
        foreach (var date in new[] { new DateOnly(2026,10,9), new DateOnly(2026,10,16) })
        {
            var job = await fixture.Prepare(date: date);
            var ready = await fixture.Wait(job.Id, view => view.Job.CanReplay || view.Job.State == "Failed");
            Assert.True(ready.Job.CanReplay, ready.Job.Message); Assert.True(ready.Job.PreparationComplete);
            Assert.Equal("Strategy", ready.PlaybackKind); keys.Add(ready.PaperRunId!);
            for (var i = 0; i < 17; i++) await fixture.Command(job.Id, "step");
            var view = await fixture.View(job.Id);
            Assert.Equal(date, view.Job.Date);
            Assert.All(view.Bars, bar => Assert.Equal(date, DateOnly.FromDateTime(bar.StartUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime)));
            Assert.Single(view.Trades);
            Assert.Equal(date, DateOnly.FromDateTime(view.Trades[0].EntryTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime));
        }
        Assert.NotEqual(keys[0], keys[1]);
    }
}
