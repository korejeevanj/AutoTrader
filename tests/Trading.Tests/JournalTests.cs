using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Trading.Tests;

public sealed class JournalTests
{
    // Pre-increment schema-1 evidence: deliberately literal JSON, not serialized from today's DTOs.
    private const string LegacyJson = """
        {
          "id":"legacy-bank-plan","savedAtUtc":"2026-10-10T12:00:00+00:00","mode":"SYNTHETIC TEST",
          "scenarioId":"normal","snapshotId":"synthetic-dashboard-v1:normal",
          "contractId":"synth-bnf-20261029-51500-CE","lots":1,"units":25,"status":"planned",
          "thesis":"Original bank thesis","stopPlan":"Observe only","invalidation":"Missing evidence","note":"<b>old plain text</b>",
          "snapshot":{
            "schemaVersion":"1","fixtureVersion":"synthetic-dashboard-v1","snapshotId":"synthetic-dashboard-v1:normal",
            "scenarioId":"normal","scenarioLabel":"Normal","sequence":1,"asOfUtc":"2026-10-12T04:45:05+00:00",
            "source":"SYNTHETIC_FIXTURE","mode":"SYNTHETIC TEST",
            "freshness":{"state":"fresh","quoteAgeSeconds":2,"receiptAgeSeconds":1,"message":"Fixed synthetic as-of"},
            "market":{"indexName":"BANKNIFTY (synthetic cash context)","indexValue":52140,"futuresSymbol":"SYNTH-BANKNIFTY-FUT","futuresValue":52169,"changePercent":0.27},
            "chart":{"symbol":"SYNTH-BANKNIFTY-FUT","bars":[{"startUtc":"2026-10-12T03:45:00+00:00","endUtc":"2026-10-12T03:46:00+00:00","availableAtUtc":"2026-10-12T03:46:01+00:00","open":51988,"high":52000,"low":51980,"close":51992,"volume":100}],"openingRangeHigh":52050,"openingRangeLow":51980,"vwap":[{"timeUtc":"2026-10-12T03:45:00+00:00","value":51990.67}],"vwapLabel":"Approximate futures VWAP (synthetic one-minute candle typical price × volume)"},
            "banks":[{"id":"synth-bank-a","name":"Fixture Bank A","weightPercent":40,"lastPrice":102,"vwap":100,"changePercent":2,"aboveVwap":true,"state":"fresh","eventTimeUtc":"2026-10-12T04:45:03+00:00","receivedAtUtc":"2026-10-12T04:45:04+00:00"}],
            "participation":{"aboveVwapWeightPercent":80,"belowVwapWeightPercent":20,"missingWeightPercent":0,"coverageLabel":"Illustrative synthetic weights; missing weight disclosed against 100%, never renormalized"},
            "options":[{"contract":{"id":"synth-bnf-20261029-51500-CE","symbol":"SYNTH-BNF-29OCT-51500-CE","exchange":"SYNTHETIC","underlying":"BANKNIFTY","optionType":"CE","expiry":"2026-10-29","strike":51500,"lotSize":25,"tickSize":0.05,"validFromUtc":"2026-10-11T03:45:00+00:00","validToUtc":"2026-10-29T03:45:00+00:00","isSynthetic":true},"quote":{"lastPrice":210.5,"bid":210,"ask":211,"bidQuantity":100,"askQuantity":125,"eventTimeUtc":"2026-10-12T04:45:03+00:00","receivedAtUtc":"2026-10-12T04:45:04+00:00","lastTradeTimeUtc":"2026-10-12T04:45:02+00:00","state":"fresh"},"spread":1,"quoteAgeSeconds":2,"decision":{"action":"WATCH","reasonCodes":["SYNTHETIC_ILLUSTRATION","NO_SELECTION_POLICY"]}}],
            "position":{"state":"none","positionId":null,"contractId":null,"lots":null,"units":null,"entryPrice":null,"currentBid":null,"grossPnl":null,"netPnl":null,"message":"No preset position. Journal plans are not orders or fills."},
            "planning":{"roundTripFees":null,"requiredCash":null,"netRisk":null,"availabilityReason":"Unavailable: synthetic fixture has no effective-date fee, cash or net-risk model. Paper execution is disabled."}
          }
        }
        """;

    [Fact]
    public async Task Legacy_bank_evidence_is_projected_through_http_and_never_rewritten_on_restart()
    {
        using var database = new TemporaryJournal();
        await database.InsertLegacyAsync(LegacyJson);
        var original = JsonNode.Parse(LegacyJson)!;
        for (var restart = 0; restart < 2; restart++)
        {
            await using var host = database.CreateHost();
            using var client = host.CreateClient();
            var entry = JsonNode.Parse(await client.GetStringAsync("/api/journal/plans/legacy-bank-plan"))!;
            Assert.Equal("BANKNIFTY", (string)entry["indexId"]!);
            Assert.Null(entry["snapshot"]!["indexId"]);
            Assert.Null(entry["snapshot"]!["constituents"]);
            var evidence = (JsonObject)entry["snapshot"]!.DeepClone();
            evidence.Remove("indexId"); evidence.Remove("constituents");
            Assert.True(JsonNode.DeepEquals(original["snapshot"], evidence));
            Assert.Equal((string)original["note"]!, (string)entry["note"]!);
            var bank = JsonNode.Parse(await client.GetStringAsync("/api/journal/plans?indexId=BANKNIFTY"))!.AsArray();
            Assert.Single(bank);
            Assert.Equal("legacy-bank-plan", (string)bank[0]!["id"]!);
            if (restart == 0)
            {
                Assert.Empty(JsonNode.Parse(await client.GetStringAsync("/api/journal/plans?indexId=NIFTY"))!.AsArray());
                using var saved = await client.PostAsJsonAsync("/api/journal/plans", RequestFor(await client.GetStringAsync("/api/dashboard/snapshot?indexId=NIFTY")));
                Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
            }
            Assert.Single(JsonNode.Parse(await client.GetStringAsync("/api/journal/plans?indexId=NIFTY"))!.AsArray());
            Assert.Equal(2, JsonNode.Parse(await client.GetStringAsync("/api/journal/plans"))!.AsArray().Count);
            Assert.Equal(LegacyJson, await database.ReadRawAsync());
        }
    }

    [Theory]
    [InlineData("\"underlying\":\"BANKNIFTY\"", "\"underlying\":\"NIFTY\"")]
    [InlineData("synthetic-dashboard-v1", "unknown-fixture-v1")]
    public async Task Unknown_legacy_identity_is_not_silently_relabelled_as_bank(string oldValue, string newValue)
    {
        using var database = new TemporaryJournal();
        var json = LegacyJson.Replace(oldValue, newValue);
        await database.InsertLegacyAsync(json);
        await using var host = database.CreateHost();
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/api/journal/plans/legacy-bank-plan");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(json, await database.ReadRawAsync());
    }

    [Fact]
    public async Task Preview_derives_whole_lot_units_and_withholds_unusable_quote_references_without_saving()
    {
        using var database = new TemporaryJournal();
        await using var application = database.CreateHost();
        using var client = application.CreateClient();
        foreach (var scenario in new[] { "normal", "missing-inputs", "stale-quotes", "feed-loss" })
        {
            var snapshotJson = await client.GetStringAsync($"/api/dashboard/snapshot?scenarioId={scenario}");
            var request = RequestFor(snapshotJson);
            request["lots"] = 3;
            using var response = await client.PostAsJsonAsync("/api/journal/plans/preview", request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var preview = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = preview.RootElement;
            Assert.Equal(3, root.GetProperty("lots").GetInt32());
            Assert.Equal(75, root.GetProperty("units").GetInt32());
            if (scenario == "normal")
            {
                Assert.Equal(211m, root.GetProperty("referenceAsk").GetDecimal());
                Assert.Equal("fresh", root.GetProperty("quoteState").GetString());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, root.GetProperty("referenceAsk").ValueKind);
                var state = scenario switch { "missing-inputs" => "missing", "stale-quotes" => "stale", _ => "disconnected" };
                Assert.Equal(state, root.GetProperty("quoteState").GetString());
            }
            using var snapshot = JsonDocument.Parse(snapshotJson);
            Assert.Equal(snapshot.RootElement.GetProperty("planning").GetRawText(), root.GetProperty("planning").GetRawText());
            request["snapshotId"] = "wrong-evidence";
            using var mismatch = await client.PostAsJsonAsync("/api/journal/plans/preview", request);
            Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
            Assert.Equal("application/problem+json", mismatch.Content.Headers.ContentType?.MediaType);
        }
        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/journal/plans"));
        Assert.Equal(0, list.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Invalid_selection_lots_and_plan_text_return_useful_problems_without_saving()
    {
        using var database = new TemporaryJournal();
        await using var application = database.CreateHost();
        using var client = application.CreateClient();
        var valid = RequestFor(await client.GetStringAsync("/api/dashboard/snapshot"));
        (string Field, JsonNode? Value)[] invalidValues =
        [
            ("indexId", null), ("indexId", JsonValue.Create("")), ("indexId", JsonValue.Create("UNKNOWN")),
            ("status", JsonValue.Create("executed")),
            ("scenarioId", JsonValue.Create("unknown")),
            ("scenarioId", JsonValue.Create("missing-inputs")),
            ("snapshotId", JsonValue.Create("unknown-snapshot")),
            ("contractId", JsonValue.Create("unknown-contract")),
            ("lots", JsonValue.Create(0)), ("lots", JsonValue.Create(-1)),
            ("lots", JsonValue.Create(int.MaxValue)), ("lots", JsonValue.Create(1.5m)),
            ("lots", JsonValue.Create(2147483648L)),
            ("lots", JsonValue.Create("2")),
            ("note", JsonValue.Create(new string('x', 4001))),
            ("thesis", null), ("scenarioId", null)
        ];
        foreach (var (field, value) in invalidValues)
        {
            var request = (JsonObject)valid.DeepClone();
            request[field] = value?.DeepClone();
            using var response = await client.PostAsJsonAsync("/api/journal/plans", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("detail").GetString()));
        }
        var incomplete = (JsonObject)valid.DeepClone();
        incomplete.Remove("lots");
        using var missing = await client.PostAsJsonAsync("/api/journal/plans", incomplete);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/journal/plans"));
        Assert.Equal(0, list.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Saved_plan_preserves_server_evidence_and_survives_a_new_backend_host()
    {
        using var database = new TemporaryJournal();
        string savedJson;
        string location;
        string evidence;
        await using (var application = database.CreateHost())
        {
            using var client = application.CreateClient();
            evidence = await client.GetStringAsync("/api/dashboard/snapshot?scenarioId=missing-inputs");
            var request = RequestFor(evidence);
            // Client-supplied evidence is never used to replace the server-resolved snapshot.
            request["snapshot"] = new JsonObject { ["mode"] = "FAKE LIVE", ["asOfUtc"] = "2099-01-01T00:00:00Z" };
            using var response = await client.PostAsJsonAsync("/api/journal/plans", request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            location = response.Headers.Location!.ToString();
            savedJson = await response.Content.ReadAsStringAsync();
            using var saved = JsonDocument.Parse(savedJson);
            Assert.Equal(50, saved.RootElement.GetProperty("units").GetInt32());
            Assert.Equal("SYNTHETIC TEST", saved.RootElement.GetProperty("mode").GetString());
            Assert.Equal("<b>plain text</b>", saved.RootElement.GetProperty("note").GetString());
            Assert.Equal(JsonNode.Parse(evidence)!.ToJsonString(), JsonNode.Parse(saved.RootElement.GetProperty("snapshot").GetRawText())!.ToJsonString());
            Assert.Equal(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
                saved.RootElement.GetProperty("savedAtUtc").GetDateTimeOffset());
            Assert.Equal(savedJson, await client.GetStringAsync(location));
            // Save another scenario; the first entry must retain its original evidence.
            var second = RequestFor(await client.GetStringAsync("/api/dashboard/snapshot?scenarioId=open-position"));
            using var secondResponse = await client.PostAsJsonAsync("/api/journal/plans", second);
            Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
            using var secondJson = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
            using var list = JsonDocument.Parse(await client.GetStringAsync("/api/journal/plans"));
            Assert.Equal(2, list.RootElement.GetArrayLength());
            Assert.Equal(secondJson.RootElement.GetProperty("id").GetString(), list.RootElement[0].GetProperty("id").GetString());
        }
        await using (var recovered = database.CreateHost())
        {
            using var client = recovered.CreateClient();
            Assert.Equal(savedJson, await client.GetStringAsync(location));
            using var list = JsonDocument.Parse(await client.GetStringAsync("/api/journal/plans"));
            Assert.Equal(2, list.RootElement.GetArrayLength());
            Assert.Equal(JsonNode.Parse(evidence)!.ToJsonString(), JsonNode.Parse(list.RootElement[1].GetProperty("snapshot").GetRawText())!.ToJsonString());
            using var missing = await client.GetAsync("/api/journal/plans/unknown-entry");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    private static JsonObject RequestFor(string snapshotJson)
    {
        var snapshot = JsonNode.Parse(snapshotJson)!;
        return new JsonObject
        {
            ["indexId"] = snapshot["indexId"]!.GetValue<string>(),
            ["scenarioId"] = snapshot["scenarioId"]!.GetValue<string>(),
            ["snapshotId"] = snapshot["snapshotId"]!.GetValue<string>(),
            ["contractId"] = snapshot["options"]![0]!["contract"]!["id"]!.GetValue<string>(),
            ["lots"] = 2, ["status"] = "planned", ["thesis"] = "Fixture thesis",
            ["stopPlan"] = "Observe only", ["invalidation"] = "Insufficient evidence", ["note"] = "<b>plain text</b>"
        };
    }

    private sealed class TemporaryJournal : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "opencode", $"autotrader-{Guid.NewGuid():N}.db");

        public async Task InsertLegacyAsync(string json)
        {
            Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE journal_plans (id TEXT PRIMARY KEY NOT NULL, saved_at_utc TEXT NOT NULL, entry_json TEXT NOT NULL); INSERT INTO journal_plans VALUES ('legacy-bank-plan', '2026-10-10T12:00:00Z', $json);";
            command.Parameters.AddWithValue("$json", json);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<string> ReadRawAsync()
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT entry_json FROM journal_plans WHERE id = 'legacy-bank-plan'";
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public WebApplicationFactory<Program> CreateHost() => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.PostConfigure<Trading.Data.JournalStorageOptions>(options => options.SqliteJournalPath = path);
                services.AddSingleton<TimeProvider>(new FixedTimeProvider());
            }));

        public void Dispose()
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                File.Delete(path + suffix);
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    }
}
