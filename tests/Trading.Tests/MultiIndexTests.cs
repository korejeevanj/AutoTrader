using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Trading.Api;
using Xunit;

namespace Trading.Tests;

public sealed class MultiIndexTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public async Task Disabled_capabilities_expose_only_available_evidence_and_cannot_create_an_option_plan(
        bool futuresChart, bool optionComparison, bool weightedConstituents)
    {
        JsonNode enabled;
        await using (var baseline = new WebApplicationFactory<Program>())
        {
            using var client = baseline.CreateClient();
            enabled = JsonNode.Parse(await client.GetStringAsync("/api/dashboard/snapshot?indexId=NIFTY"))!;
        }
        var path = Path.Combine(Path.GetTempPath(), "opencode", $"capabilities-{Guid.NewGuid():N}.db");
        WebApplicationFactory<Program> CreateHost() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<SyntheticDashboardOptions>(options =>
                {
                    var seed = options.Indices.Single(x => x.Definition.Id == "NIFTY");
                    seed.Definition = seed.Definition with
                    {
                        Capabilities = new Trading.Domain.IndexCapabilities(futuresChart, optionComparison, weightedConstituents)
                    };
                });
                services.PostConfigure<Trading.Data.JournalStorageOptions>(options => options.SqliteJournalPath = path);
            }));
        try
        {
            await using (var host = CreateHost())
            {
                using var client = host.CreateClient();
                var catalog = JsonNode.Parse(await client.GetStringAsync("/api/dashboard/indices"))!.AsArray();
                var capabilities = catalog.Single(x => (string)x!["id"]! == "NIFTY")!["capabilities"]!;
                Assert.Equal(futuresChart, (bool)capabilities["futuresChart"]!);
                Assert.Equal(optionComparison, (bool)capabilities["optionComparison"]!);
                Assert.Equal(weightedConstituents, (bool)capabilities["weightedConstituents"]!);
                foreach (var scenario in new[] { "normal", "missing-inputs", "stale-quotes", "feed-loss", "open-position" })
                {
                    var snapshot = JsonNode.Parse(await client.GetStringAsync($"/api/dashboard/snapshot?indexId=NIFTY&scenarioId={scenario}"))!;
                    Assert.Equal(25140m, (decimal)snapshot["market"]!["indexValue"]!);
                    var chart = snapshot["chart"]!;
                    if (!futuresChart)
                    {
                        Assert.Empty(chart["bars"]!.AsArray());
                        Assert.Empty(chart["vwap"]!.AsArray());
                        Assert.Null(chart["openingRangeHigh"]);
                        Assert.Null(chart["openingRangeLow"]);
                        Assert.Null(snapshot["market"]!["futuresValue"]);
                        Assert.Contains("unavailable", (string)chart["vwapLabel"]!, StringComparison.OrdinalIgnoreCase);
                        Assert.Contains("chart unavailable", (string)snapshot["freshness"]!["message"]!, StringComparison.OrdinalIgnoreCase);
                    }
                    else Assert.Equal(60, chart["bars"]!.AsArray().Count);
                    if (!weightedConstituents)
                    {
                        Assert.Equal(4, snapshot["constituents"]!.AsArray().Count);
                        Assert.All(snapshot["constituents"]!.AsArray(), row => Assert.Null(row!["weightPercent"]));
                        Assert.Equal(1002m, (decimal)snapshot["constituents"]![0]!["lastPrice"]!);
                        Assert.Equal(1000m, (decimal)snapshot["constituents"]![0]!["vwap"]!);
                        Assert.Null(snapshot["participation"]!["aboveVwapWeightPercent"]);
                        Assert.Null(snapshot["participation"]!["belowVwapWeightPercent"]);
                        Assert.Equal(100m, (decimal)snapshot["participation"]!["missingWeightPercent"]!);
                        Assert.Contains("unavailable", (string)snapshot["participation"]!["coverageLabel"]!, StringComparison.OrdinalIgnoreCase);
                    }
                    if (!optionComparison)
                    {
                        Assert.Empty(snapshot["options"]!.AsArray());
                        var position = snapshot["position"]!;
                        Assert.Equal("none", (string)position["state"]!);
                        foreach (var field in new[] { "positionId", "contractId", "lots", "units", "entryPrice", "currentBid", "grossPnl", "netPnl" }) Assert.Null(position[field]);
                        Assert.Contains("unavailable", (string)position["message"]!, StringComparison.OrdinalIgnoreCase);
                        // A previously valid contract cannot bypass the now-disabled capability.
                        var request = Selection(enabled);
                        request["scenarioId"] = scenario;
                        request["snapshotId"] = (string)snapshot["snapshotId"]!;
                        foreach (var route in new[] { "/api/journal/plans/preview", "/api/journal/plans" })
                        {
                            using var response = await client.PostAsJsonAsync(route, request);
                            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                            Assert.Contains("contract", (string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["detail"]!, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    else Assert.Equal(24, snapshot["options"]!.AsArray().Count);
                }
                Assert.Empty(JsonNode.Parse(await client.GetStringAsync("/api/journal/plans"))!.AsArray());
            }
            await using (var recovered = CreateHost())
            {
                using var client = recovered.CreateClient();
                Assert.Empty(JsonNode.Parse(await client.GetStringAsync("/api/journal/plans"))!.AsArray());
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(path + suffix); }
    }

    [Fact]
    public async Task Adding_an_index_in_configuration_exposes_catalog_snapshot_and_preview_without_symbol_code()
    {
        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<SyntheticDashboardOptions>(options =>
            {
                var template = options.Indices[1];
                options.Indices.Add(new SyntheticIndexSeed
                {
                    Definition = template.Definition with { Id = "MIDCAP", DisplayName = "Fixture Midcap" },
                    FuturesSymbol = "SYNTH-MIDCAP-FUT", ContractPrefix = "synth-MIDCAP",
                    FuturesBasePrice = 12000m, IndexValue = 12140m, FirstStrike = 11500m, StrikeStep = 50m,
                    OptionBaseBid = 110m, LotSize = 40, TickSize = 0.10m, Expiry = template.Expiry,
                    ValidFromUtc = template.ValidFromUtc, ValidToUtc = template.ValidToUtc,
                    Constituents = template.Constituents.Select(x => x with { Id = x.Id.Replace("nifty", "midcap") }).ToList()
                });
            })));
        using var client = host.CreateClient();
        var catalog = JsonNode.Parse(await client.GetStringAsync("/api/dashboard/indices"))!.AsArray();
        Assert.Contains(catalog, x => (string)x!["id"]! == "MIDCAP");
        var snapshot = JsonNode.Parse(await client.GetStringAsync("/api/dashboard/snapshot?indexId=MIDCAP"))!;
        Assert.Equal("MIDCAP", (string)snapshot["indexId"]!);
        using var preview = await client.PostAsJsonAsync("/api/journal/plans/preview", Selection(snapshot));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(80, (int)JsonNode.Parse(await preview.Content.ReadAsStringAsync())!["units"]!);
    }

    [Fact]
    public async Task Journal_resolves_index_evidence_atomically_and_filters_saved_plans_across_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "opencode", $"multi-index-{Guid.NewGuid():N}.db");
        WebApplicationFactory<Program> CreateHost() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<Trading.Data.JournalStorageOptions>(x => x.SqliteJournalPath = path)));
        var saved = new Dictionary<string, string>();
        try
        {
            await using (var host = CreateHost())
            {
                using var client = host.CreateClient();
                var bank = JsonNode.Parse(await client.GetStringAsync("/api/dashboard/snapshot"))!;
                var nifty = JsonNode.Parse(await client.GetStringAsync("/api/dashboard/snapshot?indexId=NIFTY"))!;
                foreach (var (id, units) in new[] { ("BANKNIFTY", 50), ("NIFTY", 150), ("SENSEX", 40) })
                {
                    var snapshot = JsonNode.Parse(await client.GetStringAsync($"/api/dashboard/snapshot?indexId={id}"))!;
                    var selection = Selection(snapshot);
                    using var preview = await client.PostAsJsonAsync("/api/journal/plans/preview", selection);
                    Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
                    Assert.Equal(units, (int)JsonNode.Parse(await preview.Content.ReadAsStringAsync())!["units"]!);
                    using var response = await client.PostAsJsonAsync("/api/journal/plans", selection);
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    var entry = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                    Assert.Equal(id, (string)entry["indexId"]!);
                    Assert.Equal(id, (string)entry["snapshot"]!["indexId"]!);
                    saved[id] = response.Headers.Location!.ToString();
                    foreach (var route in new[] { "/api/journal/plans", "/api/journal/plans/preview" })
                    {
                        foreach (var invalid in new[] { "missing", "unknown", "cross-index", "cross-contract", "cross-snapshot" })
                        {
                            var request = (JsonObject)selection.DeepClone();
                            if (invalid == "missing") request.Remove("indexId");
                            if (invalid == "unknown") request["indexId"] = "UNKNOWN";
                            if (invalid == "cross-index") request["indexId"] = id == "BANKNIFTY" ? "NIFTY" : "BANKNIFTY";
                            if (invalid == "cross-contract") request["contractId"] = (string)(id == "BANKNIFTY" ? nifty : bank)["options"]![0]!["contract"]!["id"]!;
                            if (invalid == "cross-snapshot") request["snapshotId"] = id == "BANKNIFTY" ? "synthetic-dashboard-v2:NIFTY:illustrative-v1:normal" : (string)bank["snapshotId"]!;
                            using var rejected = await client.PostAsJsonAsync(route, request);
                            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
                            Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
                        }
                    }
                }
            }
            await using (var host = CreateHost())
            {
                using var client = host.CreateClient();
                Assert.Equal(3, JsonNode.Parse(await client.GetStringAsync("/api/journal/plans"))!.AsArray().Count);
                foreach (var (id, location) in saved)
                {
                    var entry = JsonNode.Parse(await client.GetStringAsync(location))!;
                    Assert.Equal(id, (string)entry["indexId"]!);
                    var filtered = JsonNode.Parse(await client.GetStringAsync($"/api/journal/plans?indexId={id}"))!.AsArray();
                    Assert.Single(filtered);
                    Assert.Equal(entry.ToJsonString(), filtered[0]!.ToJsonString());
                }
                using var unknown = await client.GetAsync("/api/journal/plans?indexId=UNKNOWN");
                Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(path + suffix); }
    }

    private static JsonObject Selection(JsonNode snapshot) => new()
    {
        ["indexId"] = (string)snapshot["indexId"]!, ["scenarioId"] = (string)snapshot["scenarioId"]!,
        ["snapshotId"] = (string)snapshot["snapshotId"]!, ["contractId"] = (string)snapshot["options"]![0]!["contract"]!["id"]!,
        ["lots"] = 2, ["status"] = "planned", ["thesis"] = "", ["stopPlan"] = "", ["invalidation"] = "", ["note"] = ""
    };

    [Fact]
    public async Task Catalog_drives_distinct_repeatable_index_workspaces_with_default_bank_compatibility()
    {
        await using var host = new WebApplicationFactory<Program>();
        using var client = host.CreateClient();
        var catalog = JsonNode.Parse(await client.GetStringAsync("/api/dashboard/indices"))!.AsArray();
        Assert.Equal(new[] { "BANKNIFTY", "NIFTY", "SENSEX" }, catalog.Select(x => (string)x!["id"]!));
        foreach (var definition in catalog)
        {
            Assert.Equal(new[] { "id", "displayName", "exchange", "timeZone", "constituentLabel", "definitionVersion", "effectiveFromUtc", "effectiveToUtc", "isSynthetic", "capabilities" }, definition!.AsObject().Select(x => x.Key));
            Assert.Equal("Asia/Kolkata", (string)definition["timeZone"]!);
            Assert.True((bool)definition["isSynthetic"]!);
            Assert.Null(definition["effectiveToUtc"]);
            Assert.Equal(new[] { "futuresChart", "optionComparison", "weightedConstituents" }, definition["capabilities"]!.AsObject().Select(x => x.Key));
        }
        var contracts = new HashSet<string>();
        foreach (var (id, lot, tick, price, strike) in new[]
        {
            ("BANKNIFTY", 25, 0.05m, 52140m, 51500m),
            ("NIFTY", 75, 0.10m, 25140m, 24500m),
            ("SENSEX", 20, 0.25m, 85140m, 84000m)
        })
        {
            var original = await client.GetStringAsync($"/api/dashboard/snapshot?indexId={id}");
            var snapshot = JsonNode.Parse(original)!;
            Assert.Equal(id, (string)snapshot["indexId"]!);
            Assert.Equal("2", (string)snapshot["schemaVersion"]!);
            Assert.Contains(id, (string)snapshot["snapshotId"]!);
            Assert.Contains(id, (string)snapshot["fixtureVersion"]!);
            Assert.Equal(price, (decimal)snapshot["market"]!["indexValue"]!);
            Assert.Equal(4, snapshot["constituents"]!.AsArray().Count);
            Assert.Equal(id == "BANKNIFTY" ? 4 : 0, snapshot["banks"]!.AsArray().Count);
            if (id == "BANKNIFTY") Assert.Equal(original, await client.GetStringAsync("/api/dashboard/snapshot"));
            var contract = snapshot["options"]![0]!["contract"]!;
            Assert.Equal(lot, (int)contract["lotSize"]!);
            Assert.Equal(tick, (decimal)contract["tickSize"]!);
            Assert.Equal(strike, (decimal)contract["strike"]!);
            Assert.Equal(id, (string)contract["underlying"]!);
            Assert.Equal("SYNTHETIC", (string)contract["exchange"]!);
            foreach (var option in snapshot["options"]!.AsArray()) Assert.True(contracts.Add((string)option!["contract"]!["id"]!));
            foreach (var scenario in new[] { "normal", "missing-inputs", "stale-quotes", "feed-loss", "open-position" })
            {
                var json = await client.GetStringAsync($"/api/dashboard/snapshot?indexId={id}&scenarioId={scenario}");
                Assert.Equal(json, await client.GetStringAsync($"/api/dashboard/snapshot?indexId={id}&scenarioId={scenario}"));
                var current = JsonNode.Parse(json)!;
                Assert.Equal(snapshot["options"]!.AsArray().Select(x => x!["contract"]!.ToJsonString()),
                    current["options"]!.AsArray().Select(x => x!["contract"]!.ToJsonString()));
                var asOf = (DateTimeOffset)current["asOfUtc"]!;
                var bars = current["chart"]!["bars"]!.AsArray();
                var vwap = current["chart"]!["vwap"]!.AsArray();
                for (var i = 0; i < bars.Count; i++)
                {
                    Assert.True((DateTimeOffset)bars[i]!["endUtc"]! <= asOf);
                    Assert.True((DateTimeOffset)bars[i]!["availableAtUtc"]! <= asOf);
                    Assert.Equal((string)bars[i]!["startUtc"]!, (string)vwap[i]!["timeUtc"]!);
                }
                if (scenario == "missing-inputs") Assert.Equal(30m, (decimal)current["participation"]!["missingWeightPercent"]!);
                if (scenario == "open-position") Assert.Contains(id, (string)current["position"]!["positionId"]!);
            }
        }
        using var unknown = await client.GetAsync("/api/dashboard/snapshot?indexId=UNKNOWN");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
