using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Trading.Domain;

namespace Trading.Api;

public sealed class SyntheticApiSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(Quote) || type == typeof(ConstituentRow))
            Property(schema, "state", p => QuoteStates(p));
        if (type == typeof(ScenarioDescriptor) || type == typeof(DashboardSnapshot) || type == typeof(JournalEntry))
            Property(schema, type == typeof(ScenarioDescriptor) ? "id" : "scenarioId", p => Scenarios(p));
        if (type == typeof(DashboardSnapshot) || type == typeof(JournalEntry) || type == typeof(HealthResponse))
            Property(schema, "mode", p => Enum(p, "SYNTHETIC TEST"));
        if (type == typeof(DashboardSnapshot))
            Property(schema, "source", p => Enum(p, "SYNTHETIC_FIXTURE"));
        if (type == typeof(SnapshotFreshness))
            Property(schema, "state", p => Enum(p, "fresh", "partial", "stale", "disconnected"));
        if (type == typeof(IllustrativeDecision))
            Property(schema, "action", p => Enum(p, "WATCH", "SKIP", "INSUFFICIENT_DATA"));
        if (type == typeof(IndexDefinition))
            schema.Description = "Configured synthetic index identity. Dates, exchange venue, capabilities and weights are illustrative metadata, not verified live or historical facts. Contract exchange is always SYNTHETIC.";
        if (type == typeof(IndexCapabilities))
        {
            schema.Description = "Independent configured synthetic feature flags. False flags suppress corresponding snapshot evidence; all initial fixture definitions enable every flag.";
            Property(schema, "futuresChart", p => p.Description = "False returns empty bars/VWAP, null opening range/futuresValue, and a chart-unavailable explanation.");
            Property(schema, "optionComparison", p => p.Description = "False returns empty options and no preset position; preview/save cannot resolve an option contract.");
            Property(schema, "weightedConstituents", p => p.Description = "False retains illustrative constituent observations with null weights; aggregate weights are unavailable with 100% missing weight.");
        }
        if (type == typeof(FixtureContract))
        {
            schema.Description = "Explicitly SYNTHETIC contract evidence. Expiry, validity, lot and tick sizes belong to this contract; fixture quantities are not current exchange facts.";
            Property(schema, "optionType", p => Enum(p, "CE", "PE"));
            Property(schema, "exchange", p => Enum(p, "SYNTHETIC"));
        }
        if (type == typeof(ConstituentRow))
            schema.Description = "Illustrative constituent evidence; unavailable values remain null. Missing weights are disclosed against 100%, never silently renormalized.";
        if (type == typeof(DashboardSnapshot))
        {
            schema.Description = "Current schema 2 synthetic snapshot with explicit indexId and constituents. Saved legacy schema-1 evidence retains original versions/banks and nullable new fields. Quotes use supplied as-of; completed bars cannot contribute before end/availability.";
            Property(schema, "banks", p => { p.Deprecated = true; p.Description = "Deprecated Bank Nifty constituent alias; empty for other current indices. Legacy saved evidence uses banks when constituents is null."; });
            Property(schema, "indexId", p => p.Description = "Required non-null on current snapshots; null only on preserved known legacy evidence.");
            Property(schema, "constituents", p => p.Description = "Generic constituent rows on current snapshots; null on preserved legacy evidence.");
        }
        if (type == typeof(ChartContext))
            schema.Description = "Completed, available one-minute synthetic futures bars and approximate candle-derived VWAP. Candle/VWAP coordinates label period start; cash indices have no traded-volume VWAP. Disabled futuresChart returns empty bars/VWAP, null range, and an unavailable vwapLabel.";
        if (type == typeof(PlanningAvailability))
            schema.Description = "Unimplemented fee/cash/net-risk estimates are explicitly null with an explanation; no portfolio or execution approval is implied.";
        if (type == typeof(PresetPosition))
        {
            schema.Description = "Preset synthetic position demonstration, not an executed paper position. Stale/disconnected liquidation and net estimates remain null. Disabled optionComparison always returns none with null position evidence and an explanation.";
            Property(schema, "state", p => Enum(p, "none", "open", "unpriced"));
        }
        if (type == typeof(JournalEntry))
        {
            schema.Description = "Immutable saved synthetic plan with server-resolved evidence; not an order, intent or fill. Entry indexId identifies current or known schema-1 Bank Nifty evidence without rewriting stored JSON.";
            schema.Required ??= new HashSet<string>();
            schema.Required.Add("indexId");
            Property(schema, "indexId", p => { p.Type = JsonSchemaType.String; p.Description = "Explicit current or known legacy index identity; always non-null on public journal reads."; });
            Property(schema, "status", p => Enum(p, "considered", "planned", "skipped"));
        }
        if (type == typeof(PlanPreview))
        {
            schema.Description = "Whole-lot server-derived units and fresh-only reference ask. Does not save or execute; unavailable planning estimates remain null.";
            Property(schema, "quoteState", p => QuoteStates(p));
        }
        if (type == typeof(HealthResponse))
        {
            schema.Description = "Application health only; SYNTHETIC TEST and paused paper-auto do not establish live-feed connectivity or quote freshness.";
            Property(schema, "status", p => Enum(p, "ok"));
            Property(schema, "application", p => Enum(p, "AutoTrader"));
            Property(schema, "paperAutoEnabled", p => p.Enum = [JsonValue.Create(false)!]);
        }
        if (type == typeof(PlanSelection) || type == typeof(SavePlanRequest))
        {
            schema.Description = "Required selection from one synthetic index/scenario/snapshot/contract. Whole lots are positive JSON integers; strings, fractions and derived-unit overflow return 400. No client market evidence is accepted.";
            // Explicitly mirror the required constructor/body boundary, including empty-but-present text fields.
            schema.Required = new HashSet<string>(schema.Properties!.Keys);
            Property(schema, "indexId", p => p.Description = "Required case-sensitive identity from the configured /api/dashboard/indices catalog (initially BANKNIFTY, NIFTY, SENSEX). Unknown/cross-index selections return 400; this is an extensible string, not a closed enum.");
            Property(schema, "scenarioId", p => Scenarios(p));
            Property(schema, "snapshotId", p => p.Description = "Must match the server snapshot for this exact index and scenario; frozen evidence from another workspace returns 400.");
            Property(schema, "contractId", p => p.Description = "Must identify a contract in the resolved index/scenario snapshot; units use that contract's lotSize.");
            Property(schema, "lots", p => { p.Minimum = "1"; p.Description = "Positive whole lots, JSON integer only. Derived lots × contract.lotSize must fit a signed 32-bit integer."; });
            Property(schema, "status", p => { Enum(p, "considered", "planned", "skipped"); p.Description = "Plan status only; no executed/order status is accepted."; });
            foreach (var field in new[] { "thesis", "stopPlan", "invalidation", "note" })
                Property(schema, field, p => { p.MaxLength = field == "note" ? 4000 : 2000; p.Description = "Required immutable plain-text plan field; empty text is allowed, null is rejected. Not an execution instruction."; });
        }
        return Task.CompletedTask;
    }

    private static void Property(OpenApiSchema schema, string name, Action<OpenApiSchema> transform)
    {
        if (schema.Properties?.TryGetValue(name, out var property) == true && property is OpenApiSchema concrete) transform(concrete);
    }

    private static void Enum(OpenApiSchema schema, params string[] values) =>
        schema.Enum = values.Select(x => (JsonNode)JsonValue.Create(x)!).ToList();

    private static void Scenarios(OpenApiSchema schema) =>
        Enum(schema, "normal", "missing-inputs", "stale-quotes", "feed-loss", "open-position");

    private static void QuoteStates(OpenApiSchema schema) =>
        Enum(schema, "fresh", "missing", "stale", "disconnected");
}
