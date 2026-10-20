using Microsoft.AspNetCore.Http.HttpResults;
using Trading.Replay;

namespace Trading.Api;

public static class HistoricalReplayEndpoints
{
    public static void MapHistoricalReplay(this WebApplication app)
    {
        Describe(app.MapGet("/api/replay/status", (HistoricalReplayService service) => TypedResults.Ok(service.Status())),
            "GetReplayStatus", "Read replay setup status", "Credential state only; no secret values. Historical metadata is separate from synthetic catalogues.");
        Describe(app.MapGet("/api/replay/setup", () =>
        {
            using var stream = typeof(HistoricalReplayEndpoints).Assembly.GetManifestResourceStream("Trading.Api.ReplaySetup.md")!;
            using var reader = new StreamReader(stream);
            return TypedResults.Text(reader.ReadToEnd(), "text/plain");
        }), "GetReplaySetupGuide", "Read daily Kite setup instructions", "Gitignored backend JSON credential-file instructions with placeholders; no credentials are returned.");
        Describe(app.MapPost("/api/replay/jobs", Create).ProducesProblem(400), "PrepareHistoricalReplay", "Prepare one completed Bank Nifty day",
            "Date-only asynchronous acquisition. Returns 202 with a recoverable job identity; all date-valid CE/PE strikes/expiries are requested when the historical catalogue is complete.");
        Describe(app.MapGet("/api/replay/jobs/{id}", View).ProducesProblem(404), "GetHistoricalReplayJob", "Read preparation and current replay state",
            "Only candles available at the current simulated time are returned. Polling never advances replay.");
        Describe(app.MapGet("/api/replay/jobs/{id}/paper-log", async (string id, int? limit, string? category, HistoricalReplayService service) =>
            await service.PaperLog(id, limit ?? 200, category) is { } rows ? Results.Ok(rows) : Results.Problem(statusCode: 404, title: "Replay job not found"))
            .Produces<IReadOnlyList<Trading.Data.PaperJournalRow>>().ProducesProblem(404), "GetReplayPaperLog", "Read durable paper events",
            "Read-only, run-scoped events; filters never advance replay. Full evaluation batches and checkpoints are committed separately from synthetic plans.");
        Describe(app.MapPost("/api/replay/jobs/{id}/commands", Command).ProducesProblem(400).ProducesProblem(404).ProducesProblem(503), "ControlHistoricalReplay", "Control acquisition or playback",
            "retry/retryFailed/refreshGaps/cancel/pause/restart/step/play/run/runAllPresets/setSpeed/setProfitExit/publish and entry controls. run finishes the current preset once; runAllPresets explicitly compares execution presets. setProfitExit opens a new paused policy variant on the frozen dataset. No broker orders.");
        Describe(app.MapGet("/api/replay/jobs/{id}/data-health", (string id, HistoricalReplayService service) => service.Health(id) is { } health ? Results.Ok(health) : Results.NotFound()).Produces<ReplayDataHealth>().ProducesProblem(404),
            "GetReplayDataHealth", "Inspect historical coverage and request errors", "Processed, complete, sparse, empty, failed and invalid are distinct. Missing intervals and decision impact are explicit.");
        Describe(app.MapGet("/api/replay/jobs/{id}/diagnostics", (string id, HistoricalReplayService service) => service.Diagnostics(id) is { } diagnostics ? Results.Ok(diagnostics) : Results.NotFound()).Produces<ReplayRunDiagnostics>().ProducesProblem(404),
            "GetReplayDiagnostics", "Inspect operational logs and run summaries", "Latest 100 sanitized operations; full history is downloadable, including after failure/cancellation.");
        Describe(app.MapGet("/api/replay/jobs/{id}/operations", (string id, HttpContext context, HistoricalReplayService service) =>
            service.Health(id) is null ? Results.NotFound() : !service.JournalReady ? Results.Problem(statusCode: 503, title: "Replay storage initializing", detail: "Wait for durable replay storage and indexes to finish initializing.")
                : Results.Stream((Stream stream) => service.ExportOperations(stream, id, context.RequestAborted), "application/gzip", "replay-operations.jsonl.gz")).Produces(200, contentType: "application/gzip").ProducesProblem(503),
            "DownloadReplayOperations", "Download complete runtime/acquisition logs", "Streamed gzip NDJSON with run identities and wall/simulated clocks; credentials and provider bodies are excluded.");
        Describe(app.MapGet("/api/replay/runs", (DateOnly? date, HistoricalReplayService service) => Results.Ok(service.Runs(date))).Produces<IReadOnlyList<Trading.Data.ReplayRunInfo>>(),
            "GetReplayRuns", "List recent research runs", "Latest 200 manifests, optionally filtered by date; legacy runs without frozen dataset identity are not invented.");
        Describe(app.MapGet("/api/replay/jobs/{id}/compare", async (string id, string left, string right, HistoricalReplayService service) =>
        {
            try { return Results.Ok(await Task.Run(() => service.Compare(id, left, right))); }
            catch (ArgumentException error) { return Results.Problem(statusCode: 400, title: "Runs cannot be compared", detail: error.Message); }
        }).Produces<ReplayRunComparison>().ProducesProblem(400), "CompareReplayRuns", "Compare completed same-dataset decisions", "Rejects different frozen datasets; returns the first 100 changed/missing action/reason records, plus version and outcome summaries.");
        Describe(app.MapGet("/api/replay/jobs/{id}/events", async (string id, string runId, int? limit, long? before, string? category, string? search, HttpContext context, HistoricalReplayService service) =>
        {
            if (service.Health(id) is null) return Results.NotFound();
            await service.WaitForJournal(context.RequestAborted);
            return service.Events(id, runId, limit ?? 50, before, category, search) is { } page ? Results.Ok(page) : Results.NotFound();
        }).Produces<ReplayEventPage>().ProducesProblem(404), "GetReplayEventPage", "Browse the complete durable journal", "Descending sequence cursor with category/search filters. Page size 1–100; no simulated time is advanced.");
        Describe(app.MapGet("/api/replay/jobs/{id}/events/{sequence:long}", async (string id, string runId, long sequence, HttpContext context, HistoricalReplayService service) =>
        {
            if (service.Health(id) is null) return Results.NotFound();
            await service.WaitForJournal(context.RequestAborted);
            return service.Evidence(id, runId, sequence) is { } json ? Results.Ok(System.Text.Json.JsonSerializer.Deserialize<Trading.Replay.ReplayDecisionTrace>(json, Trading.Data.KiteCandleImporter.JsonOptions)) : Results.NotFound();
        }).Produces<Trading.Replay.ReplayDecisionTrace>().ProducesProblem(404), "GetReplayEventEvidence", "Inspect full persisted gate and ranking evidence", "Reads the event's committed evaluation batch. Earlier journals without mappings retain their original reports.");
        Describe(app.MapGet("/api/replay/jobs/{id}/evidence", (string id, string runId, HttpContext context, HistoricalReplayService service) =>
            service.HasEvidence(id, runId) ? Results.Stream((Stream stream) => service.ExportEvidence(stream, id, runId, context.RequestAborted), "application/gzip", runId.Replace(':', '-') + "-evidence.jsonl.gz") : Results.NotFound()).Produces(200, contentType: "application/gzip").ProducesProblem(404),
            "DownloadReplayEvidence", "Download complete run-scoped research evidence", "Snapshot-consistent gzip NDJSON manifest, operations, evaluations and checkpoint, available for interrupted/cancelled runs.");
        Describe(app.MapGet("/api/replay/jobs/{id}/reports/{run:int}/{file}", (string id, int run, string file, HistoricalReplayService service) =>
            service.ReportPath(id, run, file) is { } path ? Results.File(path, "application/octet-stream", fileDownloadName: file)
                : Results.Problem(statusCode: 404, title: "Report unavailable", detail: "Only completely published, server-managed reports can be downloaded."))
            .ProducesProblem(404), "GetHistoricalReplayReport", "Download immutable replay evidence", "Allowlisted report names and versioned runs; no browser-supplied filesystem paths.");
    }
    private static void Describe(RouteHandlerBuilder route, string id, string summary, string description) => route
        .WithName(id).WithTags("Historical replay").WithSummary(summary).WithDescription(description).ProducesProblem(500);
    private static async Task<Results<Accepted<HistoricalReplayJob>, ProblemHttpResult>> Create(PrepareHistoricalReplayRequest request, HistoricalReplayService service)
    {
        try { var job = await service.PrepareAsync(request); return TypedResults.Accepted($"/api/replay/jobs/{job.Id}", job); }
        catch (ArgumentException) { return TypedResults.Problem(statusCode: 400, title: "Invalid historical date", detail: "Select a completed historical session."); }
    }
    private static async Task<Results<Ok<HistoricalReplayView>, ProblemHttpResult>> View(string id, string? instrumentId, bool? light, HistoricalReplayService service) =>
        await service.ViewAsync(id, instrumentId) is { } view ? TypedResults.Ok(light == true ? view with { Job = view.Job with { Instruments = [] }, Decisions = view.Decisions.Select(row => row with { Gates = null }).ToArray() } : view) : TypedResults.Problem(statusCode: 404, title: "Replay job not found");
    private static async Task<Results<Ok<HistoricalReplayView>, ProblemHttpResult>> Command(string id, string? instrumentId, HistoricalReplayCommand request, HistoricalReplayService service)
    {
        switch (await service.CommandAsync(id, request))
        {
            case ReplayCommandResult.UnknownJob: return TypedResults.Problem(statusCode: 404, title: "Replay job not found");
            case ReplayCommandResult.Rejected rejected: return TypedResults.Problem(statusCode: 400, title: "Invalid replay command", detail: rejected.Message);
            case ReplayCommandResult.StorageUnavailable:
                return TypedResults.Problem(statusCode: 503, title: "Paper ledger unavailable", detail: request.Action == "setProfitExit"
                    ? "Profit-policy selection could not commit. Restore storage and retry preparation before playback."
                    : "Replay is paused. Restore ledger storage, then retry Step to commit the pending batch.");
            default: return TypedResults.Ok((await service.ViewAsync(id, instrumentId))!);
        }
    }
}
