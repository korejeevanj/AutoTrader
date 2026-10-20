using Microsoft.AspNetCore.Http.HttpResults;
using Trading.Domain;
using Trading.Portfolio;

namespace Trading.Api;

public sealed record HealthResponse(string Status, string Application, string Mode, bool PaperAutoEnabled);

public static class SyntheticRestEndpoints
{
    public static void MapSyntheticRestEndpoints(this WebApplication app)
    {
        Describe(app.MapGet("/api/health", () => TypedResults.Ok(new HealthResponse("ok", "AutoTrader", "SYNTHETIC TEST", false))),
            "GetHealth", "Health", "Read application health", "Application connectivity only; does not establish quote freshness or live feed readiness. Paper auto remains paused.");
        Describe(app.MapGet("/api/dashboard/indices", (IIndexCatalog catalog) => TypedResults.Ok(catalog.Indices)),
            "GetDashboardIndices", "Dashboard", "Read the configured index catalog", "Illustrative synthetic definitions and capabilities, not verified instrument-master or historical weight evidence. Additional identities are configuration-driven.");
        Describe(app.MapGet("/api/dashboard/scenarios", (IDashboardSnapshots snapshots) => TypedResults.Ok(snapshots.Scenarios)),
            "GetDashboardScenarios", "Dashboard", "Read synthetic scenarios", "All five fixture scenarios are available for every configured index; no live market data is implied.");
        Describe(app.MapGet("/api/dashboard/snapshot", Snapshot).ProducesProblem(404),
            "GetDashboardSnapshot", "Dashboard", "Read an index-scoped synthetic snapshot", "Defaults to BANKNIFTY and normal. Unknown index/scenario returns 404. Schema 2 observations use a fixed as-of clock; missing estimates remain null.");
        Describe(app.MapPost("/api/journal/plans/preview", Preview).ProducesProblem(400).ProducesProblem(415),
            "PreviewJournalPlan", "Journal", "Preview whole-lot plan quantities", "Requires index, scenario, snapshot and contract evidence from one workspace. Returns fresh-only indicative ask; creates no plan, intent, order or fill. Invalid selection/body/whole lots returns 400.");
        Describe(app.MapPost("/api/journal/plans", Save).ProducesProblem(400).ProducesProblem(415),
            "SaveJournalPlan", "Journal", "Save an immutable synthetic plan", "Returns 201 and a Location only after durable save. Market evidence and units are server-resolved; client evidence is ignored. Plain-text notes are immutable plans, not orders/intents/fills. Invalid fields or cross-index evidence returns 400; storage failure returns 500.");
        Describe(app.MapGet("/api/journal/plans", List).ProducesProblem(400),
            "ListJournalPlans", "Journal", "List saved plans, optionally by index", "Newest first. Omitted indexId returns all indices; unknown filter returns 400. Known schema-1 Bank Nifty identity is projected without rewriting original evidence. Storage/unsupported evidence failures return 500.");
        Describe(app.MapGet("/api/journal/plans/{id}", Get).ProducesProblem(404),
            "GetJournalPlan", "Journal", "Read immutable saved evidence", "Returns the preserved plan, including original schema/fixture/as-of and quote evidence. Known legacy Bank Nifty identity is projected only at entry level. Unknown saved ID returns 404; storage/unsupported evidence failures return 500.");
    }

    private static void Describe(RouteHandlerBuilder route, string id, string tag, string summary, string description) =>
        route.WithName(id).WithTags(tag).WithSummary(summary).WithDescription(description).ProducesProblem(500);

    private static Results<Ok<DashboardSnapshot>, ProblemHttpResult> Snapshot(string? indexId, string? scenarioId, IDashboardSnapshots snapshots) =>
        snapshots.Get(scenarioId ?? "normal", indexId ?? "BANKNIFTY") is { } snapshot
            ? TypedResults.Ok(snapshot)
            : TypedResults.Problem(statusCode: 404, title: "Unknown index or scenario", detail: "Select a catalog indexId and supported synthetic scenarioId.");

    private static Results<Ok<PlanPreview>, ProblemHttpResult> Preview(PlanSelection selection, SyntheticPlanJournal journal)
    {
        try { return TypedResults.Ok(journal.Preview(selection)); }
        catch (PlanValidationException exception) { return Invalid("Invalid plan selection", exception); }
    }

    private static async Task<Results<Created<JournalEntry>, ProblemHttpResult>> Save(SavePlanRequest request,
        SyntheticPlanJournal journal, TimeProvider clock, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await journal.SaveAsync(request, clock.GetUtcNow(), cancellationToken);
            return TypedResults.Created($"/api/journal/plans/{entry.Id}", entry);
        }
        catch (PlanValidationException exception) { return Invalid("Invalid plan", exception); }
    }

    private static async Task<Results<Ok<IReadOnlyList<JournalEntry>>, ProblemHttpResult>> List(string? indexId,
        SyntheticPlanJournal journal, CancellationToken cancellationToken)
    {
        try { return TypedResults.Ok(await journal.ListAsync(indexId, cancellationToken)); }
        catch (PlanValidationException exception) { return Invalid("Invalid index filter", exception); }
    }

    private static async Task<Results<Ok<JournalEntry>, ProblemHttpResult>> Get(string id, SyntheticPlanJournal journal,
        CancellationToken cancellationToken) => await journal.GetAsync(id, cancellationToken) is { } entry
            ? TypedResults.Ok(entry)
            : TypedResults.Problem(statusCode: 404, title: "Plan not found", detail: "No saved journal plan has this id.");

    private static ProblemHttpResult Invalid(string title, PlanValidationException exception) =>
        TypedResults.Problem(statusCode: 400, title: title, detail: exception.Message);
}
