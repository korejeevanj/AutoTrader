using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Trading.Tests;

public sealed class OpenApiTests
{
    [Fact]
    public async Task Response_schemas_publish_fixed_values_without_closing_catalog_identities()
    {
        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(x => x.UseEnvironment("Development"));
        using var client = host.CreateClient();
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"))!;
        var schemas = document["components"]!["schemas"]!;
        string[] scenarios = ["normal", "missing-inputs", "stale-quotes", "feed-loss", "open-position"];
        string[] quoteStates = ["fresh", "missing", "stale", "disconnected"];
        foreach (var (type, field, values) in new (string, string, string[])[]
        {
            ("Quote", "state", quoteStates),
            ("FixtureContract", "optionType", ["CE", "PE"]),
            ("FixtureContract", "exchange", ["SYNTHETIC"]),
            ("IllustrativeDecision", "action", ["WATCH", "SKIP", "INSUFFICIENT_DATA"]),
            ("SnapshotFreshness", "state", ["fresh", "partial", "stale", "disconnected"]),
            ("PresetPosition", "state", ["none", "open", "unpriced"]),
            ("ConstituentRow", "state", quoteStates),
            ("PlanPreview", "quoteState", quoteStates),
            ("JournalEntry", "status", ["considered", "planned", "skipped"]),
            ("JournalEntry", "mode", ["SYNTHETIC TEST"]),
            ("JournalEntry", "scenarioId", scenarios),
            ("ScenarioDescriptor", "id", scenarios),
            ("DashboardSnapshot", "scenarioId", scenarios),
            ("DashboardSnapshot", "source", ["SYNTHETIC_FIXTURE"]),
            ("DashboardSnapshot", "mode", ["SYNTHETIC TEST"]),
            ("HealthResponse", "status", ["ok"]),
            ("HealthResponse", "application", ["AutoTrader"]),
            ("HealthResponse", "mode", ["SYNTHETIC TEST"])
        })
        {
            var property = Resolve(document, schemas[type]!["properties"]![field]!);
            Assert.True(property["enum"] is JsonArray, $"Missing fixed response values for {type}.{field}");
            Assert.Equal(values, property["enum"]!.AsArray().Select(x => (string)x!));
        }
        var auto = schemas["HealthResponse"]!["properties"]!["paperAutoEnabled"]!;
        Assert.Equal("boolean", (string)auto["type"]!);
        Assert.False((bool)Assert.Single(auto["enum"]!.AsArray())!);
        foreach (var (type, field) in new[] { ("IndexDefinition", "id"), ("FixtureContract", "underlying"),
            ("DashboardSnapshot", "indexId"), ("JournalEntry", "indexId"), ("PlanSelection", "indexId"), ("SavePlanRequest", "indexId") })
        {
            var property = schemas[type]!["properties"]![field]!;
            Assert.Contains("string", property["type"]!.ToJsonString());
            Assert.Null(property["enum"]);
        }
    }

    [Fact]
    public async Task Development_document_describes_every_rest_operation_with_real_schemas_and_validation()
    {
        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(x => x.UseEnvironment("Development"));
        using var client = host.CreateClient();
        using var ui = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("Swagger UI", await ui.Content.ReadAsStringAsync());
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"))!;
        var paths = document["paths"]!.AsObject();
        Assert.Equal(28, paths.Count);
        Assert.DoesNotContain(paths, x => x.Key.StartsWith("/hubs"));
        var ids = new HashSet<string>();
        foreach (var (path, method, status, expected) in new[]
        {
            ("/api/health", "get", "200", "HealthResponse"),
            ("/api/dashboard/indices", "get", "200", "IndexDefinition"),
            ("/api/dashboard/scenarios", "get", "200", "ScenarioDescriptor"),
            ("/api/dashboard/snapshot", "get", "200", "DashboardSnapshot"),
            ("/api/journal/plans", "post", "201", "JournalEntry"),
            ("/api/journal/plans", "get", "200", "JournalEntry"),
            ("/api/journal/plans/preview", "post", "200", "PlanPreview"),
            ("/api/journal/plans/{id}", "get", "200", "JournalEntry"),
            ("/api/replay/jobs/{id}/data-health", "get", "200", "ReplayDataHealth"),
            ("/api/replay/jobs/{id}/diagnostics", "get", "200", "ReplayRunDiagnostics"),
            ("/api/replay/runs", "get", "200", "ReplayRunInfo"),
            ("/api/replay/jobs/{id}/events", "get", "200", "ReplayEventPage"),
            ("/api/replay/jobs/{id}/events/{sequence}", "get", "200", "ReplayDecisionTrace"),
            ("/api/replay/jobs/{id}/compare", "get", "200", "ReplayRunComparison")
        })
        {
            var operation = paths[path]![method]!;
            Assert.True(ids.Add((string)operation["operationId"]!));
            Assert.NotEmpty(operation["tags"]!.AsArray());
            Assert.False(string.IsNullOrWhiteSpace((string?)operation["summary"]));
            Assert.False(string.IsNullOrWhiteSpace((string?)operation["description"]));
            var schema = operation["responses"]![status]!["content"]!["application/json"]!["schema"]!;
            if (schema["type"]?.ToString() == "array") schema = schema["items"]!;
            Assert.EndsWith("/" + expected, (string)schema["$ref"]!);
            Assert.NotEmpty(Resolve(document, schema)["properties"]!.AsObject());
            var errors = operation["responses"]!.AsObject().Where(x => x.Key.StartsWith('4') || x.Key.StartsWith('5')).ToArray();
            Assert.NotEmpty(errors);
            foreach (var (_, response) in errors)
            {
                var problem = Resolve(document, response!["content"]!["application/problem+json"]!["schema"]!);
                Assert.Contains("detail", problem["properties"]!.AsObject().Select(x => x.Key));
            }
            if (method == "post")
            {
                Assert.True((bool)operation["requestBody"]!["required"]!);
                var request = Resolve(document, operation["requestBody"]!["content"]!["application/json"]!["schema"]!);
                var required = request["required"]!.AsArray().Select(x => (string)x!).ToArray();
                foreach (var field in new[] { "indexId", "scenarioId", "snapshotId", "contractId", "lots" }) Assert.Contains(field, required);
                Assert.Equal(1, (int)request["properties"]!["lots"]!["minimum"]!);
                Assert.Contains("catalog", (string)request["properties"]!["indexId"]!["description"]!, StringComparison.OrdinalIgnoreCase);
                if (path == "/api/journal/plans")
                {
                    foreach (var field in new[] { "status", "thesis", "stopPlan", "invalidation", "note" }) Assert.Contains(field, required);
                    Assert.Equal(new[] { "considered", "planned", "skipped" }, request["properties"]!["status"]!["enum"]!.AsArray().Select(x => (string)x!));
                }
            }
        }
        var schemas = document["components"]!["schemas"]!;
        Assert.False(schemas["HistoricalReplayView"]!["properties"]!.AsObject().ContainsKey("series"));
        Assert.True((bool)schemas["DashboardSnapshot"]!["properties"]!["banks"]!["deprecated"]!);
        Assert.Contains("legacy", (string)schemas["DashboardSnapshot"]!["description"]!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not", (string)schemas["JournalEntry"]!["description"]!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("null", (string)schemas["PlanningAvailability"]!["description"]!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "status", "application", "mode", "paperAutoEnabled" }, schemas["HealthResponse"]!["properties"]!.AsObject().Select(x => x.Key));
    }

    [Fact]
    public async Task Production_has_no_document_or_ui_and_keeps_health_available()
    {
        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(x => x.UseEnvironment("Production"));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var path in new[] { "/openapi/v1.json", "/swagger", "/swagger/index.html" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static JsonNode Resolve(JsonNode document, JsonNode schema) => schema["$ref"] is { } reference
        ? document["components"]!["schemas"]![reference.ToString().Split('/')[^1]]! : schema;
}
