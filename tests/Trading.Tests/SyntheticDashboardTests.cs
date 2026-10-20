using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Trading.Tests;

public sealed class SyntheticDashboardTests
{
    [Fact]
    public async Task Scenarios_preserve_contract_identity_and_disclose_missing_stale_and_unpriced_evidence()
    {
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        using var descriptors = JsonDocument.Parse(await client.GetStringAsync("/api/dashboard/scenarios"));
        string[] ids = ["normal", "missing-inputs", "stale-quotes", "feed-loss", "open-position"];
        Assert.Equal(ids, descriptors.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        using var normal = JsonDocument.Parse(await client.GetStringAsync("/api/dashboard/snapshot"));
        var contracts = normal.RootElement.GetProperty("options").EnumerateArray()
            .Select(option => option.GetProperty("contract").GetRawText()).ToArray();
        foreach (var id in ids.Skip(1))
        {
            using var document = JsonDocument.Parse(await client.GetStringAsync($"/api/dashboard/snapshot?scenarioId={id}"));
            var snapshot = document.RootElement;
            Assert.Equal(contracts, snapshot.GetProperty("options").EnumerateArray()
                .Select(option => option.GetProperty("contract").GetRawText()));
            Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("planning").GetProperty("roundTripFees").ValueKind);
            Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("planning").GetProperty("requiredCash").ValueKind);
            var participation = snapshot.GetProperty("participation");
            var position = snapshot.GetProperty("position");
            if (id == "missing-inputs")
            {
                Assert.Equal("partial", snapshot.GetProperty("freshness").GetProperty("state").GetString());
                Assert.Equal(30m, participation.GetProperty("missingWeightPercent").GetDecimal());
                Assert.Equal(70m, participation.GetProperty("aboveVwapWeightPercent").GetDecimal());
                Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("options")[0].GetProperty("quote").GetProperty("bid").ValueKind);
                Assert.Equal("INSUFFICIENT_DATA", snapshot.GetProperty("options")[0].GetProperty("decision").GetProperty("action").GetString());
            }
            else if (id == "open-position")
            {
                Assert.Equal("open", position.GetProperty("state").GetString());
                Assert.Equal(25, position.GetProperty("units").GetInt32());
                Assert.Equal(250m, position.GetProperty("grossPnl").GetDecimal());
                Assert.Equal(JsonValueKind.Null, position.GetProperty("netPnl").ValueKind);
            }
            else
            {
                var state = id == "stale-quotes" ? "stale" : "disconnected";
                Assert.Equal(state, snapshot.GetProperty("freshness").GetProperty("state").GetString());
                Assert.Equal(state, snapshot.GetProperty("options")[0].GetProperty("quote").GetProperty("state").GetString());
                Assert.True(snapshot.GetProperty("options")[0].GetProperty("quoteAgeSeconds").GetDecimal() >= 300m);
                Assert.Equal(100m, participation.GetProperty("missingWeightPercent").GetDecimal());
                Assert.Equal(JsonValueKind.Null, participation.GetProperty("aboveVwapWeightPercent").ValueKind);
                Assert.Equal("unpriced", position.GetProperty("state").GetString());
                Assert.Equal(JsonValueKind.Null, position.GetProperty("currentBid").ValueKind);
                Assert.Equal(JsonValueKind.Null, position.GetProperty("grossPnl").ValueKind);
                Assert.Equal(JsonValueKind.Null, position.GetProperty("netPnl").ValueKind);
                Assert.Equal(normal.RootElement.GetProperty("chart").GetRawText(), snapshot.GetProperty("chart").GetRawText());
            }
        }
        using var unknown = await client.GetAsync("/api/dashboard/snapshot?scenarioId=unknown");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Snapshot_is_repeatable_synthetic_evidence_with_completed_futures_overlays()
    {
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        using var response = await client.GetAsync("/api/dashboard/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var original = await response.Content.ReadAsStringAsync();
        Assert.Equal(original, await client.GetStringAsync("/api/dashboard/snapshot?scenarioId=normal"));
        using var document = JsonDocument.Parse(original);
        var snapshot = document.RootElement;
        Assert.Equal("BANKNIFTY", snapshot.GetProperty("indexId").GetString());
        Assert.Equal("2", snapshot.GetProperty("schemaVersion").GetString());
        Assert.Equal(snapshot.GetProperty("banks").GetRawText(), snapshot.GetProperty("constituents").GetRawText());
        Assert.Equal("SYNTHETIC TEST", snapshot.GetProperty("mode").GetString());
        Assert.Equal("SYNTHETIC_FIXTURE", snapshot.GetProperty("source").GetString());
        Assert.False(string.IsNullOrWhiteSpace(snapshot.GetProperty("fixtureVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.GetProperty("schemaVersion").GetString()));
        var asOf = snapshot.GetProperty("asOfUtc").GetDateTimeOffset();
        var chart = snapshot.GetProperty("chart");
        Assert.Equal(60, chart.GetProperty("bars").GetArrayLength());
        foreach (var bar in chart.GetProperty("bars").EnumerateArray())
        {
            var start = bar.GetProperty("startUtc").GetDateTimeOffset();
            var end = bar.GetProperty("endUtc").GetDateTimeOffset();
            var available = bar.GetProperty("availableAtUtc").GetDateTimeOffset();
            Assert.Equal(TimeSpan.FromMinutes(1), end - start);
            Assert.True(end <= available && available <= asOf);
        }
        Assert.Equal(52050m, chart.GetProperty("openingRangeHigh").GetDecimal());
        Assert.Equal(51980m, chart.GetProperty("openingRangeLow").GetDecimal());
        Assert.Equal(51990.67m, chart.GetProperty("vwap")[0].GetProperty("value").GetDecimal());
        // Chart values align with their candle's period start, not the following candle.
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 3, 45, 0, TimeSpan.Zero),
            chart.GetProperty("vwap")[0].GetProperty("timeUtc").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 4, 44, 0, TimeSpan.Zero),
            chart.GetProperty("vwap")[59].GetProperty("timeUtc").GetDateTimeOffset());
        Assert.Contains("approximate", chart.GetProperty("vwapLabel").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(24, snapshot.GetProperty("options").GetArrayLength());
        foreach (var option in snapshot.GetProperty("options").EnumerateArray())
        {
            var contract = option.GetProperty("contract");
            Assert.Equal("SYNTHETIC", contract.GetProperty("exchange").GetString());
            Assert.True(contract.GetProperty("isSynthetic").GetBoolean());
            Assert.Equal(JsonValueKind.String, option.GetProperty("quote").GetProperty("lastTradeTimeUtc").ValueKind);
        }
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("planning").GetProperty("netRisk").ValueKind);
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("position").GetProperty("netPnl").ValueKind);
    }
}
