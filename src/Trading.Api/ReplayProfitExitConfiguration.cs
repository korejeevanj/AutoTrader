using Trading.Data;
using Trading.Engine;
using Trading.Replay;

namespace Trading.Api;

public sealed partial class HistoricalReplayService
{
    private static EngineConfiguration ApplyProfitExit(EngineConfiguration baseline, ProfitExitConfiguration? policy)
    {
        if (policy is null) return baseline;
        var errors = policy.Validate();
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors));
        return EngineConfiguration.FromJson((baseline with
        {
            Version = $"{baseline.Version}|{policy.Version}:{policy.Mode}",
            Strategies = baseline.Strategies.Select(profile => profile with
                { Position = profile.Position with { ProfitExit = policy } }).ToArray()
        }).ToJson());
    }

    // Called under the job gate. Selection is run configuration, never a mutation of
    // an existing position's frozen strategy or of acquisition/source evidence.
    private async Task SetProfitExit(RuntimeJob runtime, ProfitExitConfiguration? policy)
    {
        if (policy is null) throw new ArgumentException("Supply the profit-exit policy and net rupee settings.");
        if (!runtime.Job.CanReplay || runtime.PriceOnly || runtime.Session is null || runtime.Acquiring || runtime.Job.State == "Playing")
            throw new ArgumentException("Pause a fully prepared strategy replay before changing its profit-exit policy.");
        var paper = runtime.Session.PaperState ?? throw new ArgumentException("Engine-backed paper state is required.");
        if (paper.Positions.Count != 0 || paper.PendingIntents.Count != 0)
            throw new ArgumentException("Close open/pending paper positions before changing policy, or explicitly restart the replay first.");
        var frozen = HistoricalAcquisitionStore.Read<FrozenAcquisition>(Path.Combine(store.JobsRoot, runtime.Job.Id, "acquisition.json"))
            ?? throw new ArgumentException("Recover the frozen acquisition before selecting a profit policy.");
        var effective = runtime.Engine ?? throw new ArgumentException("Recover effective Engine settings before selecting a profit policy.");
        // Preserve session qualification and all other effective non-profit settings.
        // Only take the original version label from acquisition, avoiding suffix chains.
        var baseline = effective with { Version = EngineConfiguration.FromJson(frozen.EngineJson).Version };
        var engine = ApplyProfitExit(baseline, policy);
        try
        {
            runtime.Session.Pause(); runtime.RecoverSavedRun = false; runtime.Engine = engine;
            runtime.Job = runtime.Job with { ProfitExitOverride = policy };
            await ResetSession(runtime);
            runtime.Qualification = new HistoricalReadiness(runtime.Dataset!, runtime.Replay!, engine).Summary(runtime.Job.Date);
            runtime.Job = runtime.Job with { State = "Paused", CanReplay = true, ReportsAvailable = false,
                Message = $"New {policy.Mode} profit-exit variant opened on the same frozen dataset. Playback is paused; earlier run evidence remains preserved." };
            Log(runtime, "PROFIT_EXIT_POLICY_SELECTED", new { policy, runtime.Job.Run });
            if (runtime.DiagnosticError is not null) throw new IOException("Profit-policy operation could not be journaled.");
            await Save(runtime);
        }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException)
        {
            runtime.Session?.Pause(); runtime.Qualification = null;
            runtime.Job = runtime.Job with { State = "Failed", CanReplay = false,
                Message = "Profit-policy variant could not commit. Restore storage and retry preparation before playback.",
                Warnings = runtime.Job.Warnings.Append("PROFIT_EXIT_POLICY_COMMIT_FAILED").Distinct().ToArray() };
            PublishSnapshot(runtime);
            throw;
        }
    }
}
