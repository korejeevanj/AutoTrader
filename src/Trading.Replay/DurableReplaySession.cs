using System.Text.Json;
using Trading.Data;

namespace Trading.Replay;

public sealed record ReplayEvidenceRun(string JobId, int Number, string PresetId, string InputHash,
    DateTimeOffset SessionOpenUtc, string IndexId)
{
    public string Id => IdFor(JobId, Number, PresetId);
    public static string IdFor(string jobId, int number, string presetId) => $"{jobId}:run-{number}:{presetId}";
}

/// <summary>
/// Adapts a replay session to durable evidence: a failed step remains pending until
/// the exact envelope commits, before another step or history release is allowed.
/// The host serializes access and retains pacing and presentation ownership.
/// </summary>
public sealed class DurableReplaySession : IHistoricalReplaySession
{
    private sealed record PaperEntryControl(DateTimeOffset? TimeUtc, bool Paused);
    private sealed record PendingStep(ReplayStep Step, string Id, string PayloadJson, string SnapshotJson,
        string ControlsJson, IReadOnlyList<PaperJournalEvent> Events);

    private readonly IHistoricalReplaySession session;
    private readonly ReplayEvidenceJournal journal;
    private readonly List<PaperEntryControl> controls = [];
    private PendingStep? pending;
    public ReplayEvidenceRun Run { get; }
    public int StepNumber { get; private set; }
    public ReplayStep? LastStep { get; private set; }
    public bool HasPendingCommit => pending is not null;

    public DurableReplaySession(IHistoricalReplaySession session, ReplayEvidenceJournal journal, ReplayEvidenceRun run,
        Action<ReplayStep>? recoveredStep = null)
    {
        if (session.CurrentTimeUtc is not null || session.PaperState is null)
            throw new ArgumentException("Supply a fresh engine-backed replay session.", nameof(session));
        if (run.Number < 1 || string.IsNullOrWhiteSpace(run.JobId) || string.IsNullOrWhiteSpace(run.PresetId)
            || string.IsNullOrWhiteSpace(run.InputHash) || string.IsNullOrWhiteSpace(run.IndexId))
            throw new ArgumentException("Supply a complete replay evidence identity.", nameof(run));
        this.session = session; this.journal = journal; Run = run;
        journal.Begin(run.Id, run.InputHash);
        var checkpoint = journal.Checkpoint(run.Id)!;
        controls.AddRange(JsonSerializer.Deserialize<PaperEntryControl[]>(checkpoint.ControlsJson, KiteCandleImporter.JsonOptions) ?? []);
        Restore(checkpoint, recoveredStep);
        session.Pause();
    }

    public bool IsPaused => session.IsPaused;
    public bool IsComplete => session.IsComplete;
    public DateTimeOffset? CurrentTimeUtc => session.CurrentTimeUtc;
    public DateTimeOffset? NextTimeUtc => session.NextTimeUtc;
    public IReadOnlyList<ResearchTrade> VisibleTrades => session.VisibleTrades;
    public IReadOnlyList<ReplayConstituentView> VisibleConstituents(string indexId) => session.VisibleConstituents(indexId);
    public ReplaySessionResult Result => session.Result;
    public bool PaperEntriesPaused => session.PaperEntriesPaused;
    public PaperStateView? PaperState => session.PaperState;
    public void Pause() => session.Pause();
    public void Resume() => session.Resume();

    public ReplayStep? Step()
    {
        if (pending is null)
        {
            var step = session.Step();
            if (step is null) return null;
            LastStep = step; StepNumber++;
            var id = "step-" + StepNumber;
            var events = step.Decisions.Select((decision, i) => new PaperJournalEvent(HistoricalAcquisitionStore.SafeName(id + ":" + i),
                decision.TimeUtc, decision.IndexId, decision.ContractId, decision.Class, decision.Action, decision.SetupId,
                JsonSerializer.Serialize(decision.Reasons, KiteCandleImporter.JsonOptions))).ToArray();
            pending = new(step, id, JsonSerializer.Serialize(step, KiteCandleImporter.JsonOptions), Snapshot(),
                JsonSerializer.Serialize(controls, KiteCandleImporter.JsonOptions), events);
        }
        try
        {
            journal.Append(Run.Id, pending.Id, pending.Step.TimeUtc, pending.PayloadJson,
                pending.SnapshotJson, pending.ControlsJson, pending.Events);
        }
        catch { session.Pause(); throw; }
        var committed = pending.Step;
        pending = null;
        session.ReleaseCommittedDecisionHistory();
        return committed;
    }

    public void SetPaperEntriesPaused(bool paused)
    {
        RequireCommittedStep();
        var previous = session.PaperEntriesPaused;
        session.SetPaperEntriesPaused(paused);
        var control = new PaperEntryControl(session.CurrentTimeUtc, paused);
        controls.Add(control);
        var id = "control-" + controls.Count;
        try
        {
            journal.Append(Run.Id, id, session.CurrentTimeUtc, JsonSerializer.Serialize(control, KiteCandleImporter.JsonOptions),
                Snapshot(), JsonSerializer.Serialize(controls, KiteCandleImporter.JsonOptions),
                [new(id, control.TimeUtc ?? Run.SessionOpenUtc, Run.IndexId, "", "PAPER_CONTROL",
                    paused ? "PAUSE_NEW_ENTRIES" : "ENABLE_NEW_ENTRIES", null, "[]")]);
        }
        catch
        {
            session.SetPaperEntriesPaused(previous);
            controls.RemoveAt(controls.Count - 1);
            session.Pause();
            throw;
        }
    }

    public void ReleaseCommittedDecisionHistory()
    {
        RequireCommittedStep();
        session.ReleaseCommittedDecisionHistory();
    }

    public static IEnumerable<ReplayDecisionTrace> ReadDecisions(ReplayEvidenceJournal journal, string runId)
    {
        foreach (var payload in journal.BatchPayloads(runId))
        {
            var step = JsonSerializer.Deserialize<ReplayStep>(payload, KiteCandleImporter.JsonOptions)!;
            foreach (var decision in step.Decisions) yield return decision;
        }
    }

    private void RequireCommittedStep()
    {
        if (pending is not null) throw new InvalidOperationException("Commit the pending replay step before changing controls or releasing history.");
    }

    // Preserve the checkpoint JSON contract consumed by existing evidence readers.
    private string Snapshot() => JsonSerializer.Serialize(new { Paper = session.PaperState,
        Trades = session.VisibleTrades, Complete = session.IsComplete,
        Qualified = session.IsComplete ? (bool?)session.Result.Complete : null }, KiteCandleImporter.JsonOptions);

    private void Restore(PaperJournalCheckpoint checkpoint, Action<ReplayStep>? recoveredStep)
    {
        var applied = 0;
        while (checkpoint.TimeUtc is not null && !session.IsComplete && (session.CurrentTimeUtc is null || session.CurrentTimeUtc < checkpoint.TimeUtc))
        {
            while (applied < controls.Count && controls[applied].TimeUtc == session.CurrentTimeUtc)
                session.SetPaperEntriesPaused(controls[applied++].Paused);
            var step = session.Step();
            if (step is not null)
            {
                StepNumber++; LastStep = step;
                recoveredStep?.Invoke(step);
            }
            session.ReleaseCommittedDecisionHistory();
        }
        while (applied < controls.Count && controls[applied].TimeUtc == session.CurrentTimeUtc)
            session.SetPaperEntriesPaused(controls[applied++].Paused);
        if (session.CurrentTimeUtc != checkpoint.TimeUtc) throw new InvalidDataException("Frozen replay cannot reproduce its saved clock.");
        if (checkpoint.SnapshotJson != "{}" && Snapshot() != checkpoint.SnapshotJson)
            throw new InvalidDataException("Reconstructed paper state differs from its durable checkpoint; start a new run.");
    }
}
