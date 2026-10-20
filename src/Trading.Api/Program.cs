using Trading.Api;
using Trading.Api.Hubs;
using Trading.Domain;
using Trading.Data;
using Trading.Portfolio;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Reapply higher-priority sources after the optional local file.
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true, reloadOnChange: true);
}

builder.Configuration.AddEnvironmentVariables();
if (args.Length > 0)
{
    builder.Configuration.AddCommandLine(args);
}

builder.Services.AddSignalR();
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer<SyntheticApiSchemaTransformer>();
    options.AddOperationTransformer((operation, context, cancellationToken) =>
    {
        foreach (var parameter in operation.Parameters ?? [])
        {
            parameter.Description = parameter.Name switch
            {
                "indexId" => "Case-sensitive identity from GET /api/dashboard/indices (initially BANKNIFTY, NIFTY, SENSEX). Snapshot defaults to BANKNIFTY; omitted journal filter returns all indices.",
                "scenarioId" => "normal (default), missing-inputs, stale-quotes, feed-loss, or open-position; unknown values return 404.",
                "id" => context.Description.RelativePath?.Contains("/replay/", StringComparison.Ordinal) == true
                    ? "Server-managed historical replay job identity, never a filesystem path or broker order ID."
                    : "Immutable saved plan identity returned by POST /api/journal/plans; not an order or intent ID.",
                _ => parameter.Description
            };
        }
        return Task.CompletedTask;
    });
});
builder.Services.Configure<SyntheticDashboardOptions>(builder.Configuration.GetSection("SyntheticDashboard"));
builder.Services.AddSingleton<SyntheticIndexCatalog>();
builder.Services.AddSingleton<IIndexCatalog>(services => services.GetRequiredService<SyntheticIndexCatalog>());
builder.Services.AddSingleton<IDashboardSnapshots, SyntheticDashboardSnapshots>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.Configure<JournalStorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.AddSingleton<IJournalStore>(services =>
{
    var settings = services.GetRequiredService<IOptions<JournalStorageOptions>>().Value;
    var environment = services.GetRequiredService<IHostEnvironment>();
    return new SqliteJournalStore(Path.GetFullPath(settings.SqliteJournalPath, environment.ContentRootPath));
});
builder.Services.AddSingleton<SyntheticPlanJournal>();
builder.Services.Configure<HistoricalReplayOptions>(builder.Configuration.GetSection("HistoricalReplay"));
builder.Services.AddKiteCredentialFile(builder.Environment.ContentRootPath);
builder.Services.AddHttpClient("KiteHistorical", client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate });
builder.Services.AddSingleton<KiteHistoricalClient>(services =>
{
    var options = services.GetRequiredService<IOptions<HistoricalReplayOptions>>().Value;
    if (options.MinimumRequestSpacingMilliseconds < 350 || options.MaximumProviderAttempts is < 1 or > 5)
        throw new InvalidOperationException("Historical request spacing must be >=350ms; attempts 1–5.");
    var credentials = services.GetRequiredService<IOptionsMonitor<KiteCredentialOptions>>();
    return new(services.GetRequiredService<IHttpClientFactory>().CreateClient("KiteHistorical"),
        () => { var current = credentials.CurrentValue; return new(current.ApiKey, current.AccessToken); }, services.GetRequiredService<TimeProvider>(),
        TimeSpan.FromMilliseconds(options.MinimumRequestSpacingMilliseconds), options.MaximumProviderAttempts);
});
builder.Services.AddSingleton<HistoricalReplayService>();
builder.Services.AddHttpClient("NseHistoricalContracts", client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate });
builder.Services.AddSingleton(services => new NseHistoricalContractClient(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("NseHistoricalContracts"), services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(services => new PublishedBankInputsClient(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("NseHistoricalContracts"), services.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService(services => services.GetRequiredService<HistoricalReplayService>());
builder.Services.Configure<LivePaperOptions>(builder.Configuration.GetSection("LivePaper"));
builder.Services.AddSingleton<LivePaperService>();
builder.Services.AddHostedService(services => services.GetRequiredService<LivePaperService>());
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<RequestBodyExceptionHandler>();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});

var app = builder.Build();
app.UseExceptionHandler();

app.MapHub<DashboardHub>("/hubs/dashboard");
app.MapSyntheticRestEndpoints();
app.MapHistoricalReplay();
app.MapLivePaper();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json");
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "AutoTrader synthetic cockpit v1"));
}

app.Run();

public partial class Program { }
