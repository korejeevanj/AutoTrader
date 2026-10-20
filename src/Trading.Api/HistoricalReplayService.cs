using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Trading.Data;
using Trading.Replay;

namespace Trading.Api;

/// <summary>
/// Hosts the replay jobs module and presents it over HTTP: it owns transport, request mapping and
/// the display projection, while preparation, commands, playback and persistence live in ReplayJobs.
/// </summary>
public sealed partial class HistoricalReplayService : BackgroundService
{
    private readonly string serverInstanceId = Guid.NewGuid().ToString("N");
    private readonly KiteHistoricalClient provider;
    private readonly ReplayEvidenceJournal paperJournal;
    private readonly ReplayOperationLog operationLog;
    private readonly ReplayJobs jobs;
    private readonly ConcurrentDictionary<string, (ReplayJobSnapshot Snapshot, HistoricalReplayView View)> views = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ((AcquisitionAttemptEvidence, bool, bool, HistoricalReadinessSummary?, IReadOnlyList<string>) Inputs, ReplayDataHealth Health)> health = new(StringComparer.Ordinal);

    public HistoricalReplayService(IOptions<HistoricalReplayOptions> options, IWebHostEnvironment environment,
        KiteHistoricalClient provider, TimeProvider clock, NseHistoricalContractClient exchange, PublishedBankInputsClient bankInputs)
    {
        this.provider = provider;
        var store = new HistoricalAcquisitionStore(Path.GetFullPath(options.Value.RootDirectory, environment.ContentRootPath));
        paperJournal = new(Path.Combine(store.Root, "paper-replay.sqlite"));
        operationLog = new(paperJournal);
        var configurationPath = Path.GetFullPath(options.Value.ReplayConfigurationPath, environment.ContentRootPath);
        var sources = new ProviderHistoricalSources(store, provider, exchange, bankInputs);
        jobs = new(store, new HistoricalAcquisition(store, sources, clock, configurationPath, options.Value.AcquirePublishedBankInputs),
            sources, paperJournal, operationLog, clock, new() { ArchiveCurrentMaster = options.Value.ArchiveCurrentMaster });
    }

    public ReplayConnectionStatus Status() => new(provider.CredentialStatus, jobs.LatestCompletedDate(), "/api/replay/setup",
        "Historical metadata is date-specific. Current dumps never substitute for missing historical catalogues/weights.");

    public async Task<HistoricalReplayJob> PrepareAsync(PrepareHistoricalReplayRequest request)
    {
        var snapshot = await jobs.PrepareAsync(request.Date, request.Refresh);
        return HistoricalReplayJob.From(snapshot.Job, snapshot.Job.PreparingData);
    }

    /// <summary>Maps the wire command to the module's typed command and returns its typed result.</summary>
    public async Task<ReplayCommandResult> CommandAsync(string id, HistoricalReplayCommand request)
    {
        if (!jobs.Exists(id)) return new ReplayCommandResult.UnknownJob();
        var command = ToCommand(request);
        return command is null ? new ReplayCommandResult.Rejected("Unknown replay command.") : await jobs.SendAsync(id, command);
    }

    private static ReplayCommand? ToCommand(HistoricalReplayCommand request) => request.Action switch
    {
        "retry" => new ReplayCommand.Retry { Speed = request.Speed },
        "retryFailed" => new ReplayCommand.RetryFailed { Speed = request.Speed },
        "refreshGaps" => new ReplayCommand.RefreshGaps { Speed = request.Speed },
        "cancel" => new ReplayCommand.Cancel { Speed = request.Speed },
        "cancelPreparation" => new ReplayCommand.CancelPreparation { Speed = request.Speed },
        "pause" => new ReplayCommand.Pause { Speed = request.Speed },
        "step" => new ReplayCommand.Step { Speed = request.Speed },
        "play" => new ReplayCommand.Play { Speed = request.Speed },
        "run" => new ReplayCommand.Run { Speed = request.Speed },
        "runAllPresets" => new ReplayCommand.RunAllPresets { Speed = request.Speed },
        "setSpeed" => new ReplayCommand.SetSpeed { Speed = request.Speed },
        "setProfitExit" => new ReplayCommand.SetProfitExit(request.ProfitExit) { Speed = request.Speed },
        "publish" => new ReplayCommand.Publish { Speed = request.Speed },
        "restart" => new ReplayCommand.Restart { Speed = request.Speed },
        "pauseEntries" => new ReplayCommand.PauseEntries { Speed = request.Speed },
        "enableEntries" => new ReplayCommand.EnableEntries { Speed = request.Speed },
        _ => null
    };

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => jobs.RunAsync(stoppingToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await jobs.DrainAsync(cancellationToken);
    }

    public string? ReportPath(string id, int run, string file) => jobs.ReportPath(id, run, file);
}
