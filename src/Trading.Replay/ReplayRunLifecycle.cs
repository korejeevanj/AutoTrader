using System.Text.Json;
using Trading.Data;
using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

public sealed record ReplayCodeVersion(string Name, Guid Mvid);
public sealed record ReplayRunInputs(HistoricalDataset Dataset, ReplayConfiguration Replay, EngineConfiguration Engine,
    string InputHash, IReadOnlyList<ReplayCodeVersion> CodeVersions, IReadOnlyList<string> Warnings);
public sealed record ReplayRunRecovery(int Number, string? ActivePresetId, bool AllPresets, bool NewRunRequired = false);
public enum ReplayPlaybackRequest { Step, Resume, CurrentPreset, AllPresets }

/// <summary>
/// Owns one coherent run across opening, reconstruction, presets and publication
/// eligibility. The host serializes access and supplies pacing and frozen inputs.
/// A newly attached session cannot advance before its manifest is durable.
/// </summary>
public sealed class ReplayRunLifecycle(string jobId, DateOnly date, ReplayEvidenceJournal journal, TimeProvider clock) : IHistoricalReplaySession
{
    private IHistoricalReplaySession? session;
    private ReplayRunner? runner;
    private readonly List<ReplaySessionResult> completed = [];
    private string? pendingManifest;
    private bool openingStaged;
    private int previewStepNumber;
    public ReplayRunInputs? Inputs { get; private set; }
    public int Number { get; private set; }
    public int PresetIndex { get; private set; }
    public bool AllPresets { get; private set; }
    public bool Recovered { get; private set; }
    public bool HasSession => session is not null;
    public bool PriceOnly => Inputs is not null && !Inputs.Dataset.Manifest.Instruments.Any(item => item.Role == HistoricalInstrumentRole.Futures);
    public bool ManifestPending => pendingManifest is not null;
    public string? ManifestError { get; private set; }
    private DurableReplaySession? Evidence => session as DurableReplaySession;
    public string? JournalRunId => Evidence?.Run.Id;
    public int StepNumber => Evidence?.StepNumber ?? previewStepNumber;
    public bool HasPendingCommit => Evidence?.HasPendingCommit == true;
    public bool CanPublish => IsComplete && !HasPendingCommit && !ManifestPending && (!AllPresets || !HasRemainingPresets);
    public ReplayStep? LastStep { get; private set; }

    public void Open(ReplayRunInputs inputs, int previousRun, ReplayRunRecovery? recovery = null,
        Action<ReplayStep>? recoveredStep = null)
    {
        if (HasPendingCommit) throw new InvalidOperationException("Commit the pending step before opening another replay run.");
        session?.Pause();
        var priceOnly = !inputs.Dataset.Manifest.Instruments.Any(item => item.Role == HistoricalInstrumentRole.Futures);
        var nextRunner = priceOnly ? null : new ReplayRunner(inputs.Dataset, inputs.Replay, inputs.Engine);
        if (!priceOnly && recovery is not null) recovery = RecoverOpening(inputs, previousRun, recovery);
        var preset = 0; var recover = false;
        if (!priceOnly && recovery is { Number: > 0 })
        {
            preset = inputs.Replay.Presets.Select((value, i) => (value.Id, i)).SingleOrDefault(value => value.Id == recovery.ActivePresetId).i;
            var checkpoint = journal.Checkpoint(ReplayEvidenceRun.IdFor(jobId, recovery.Number, inputs.Replay.Presets[preset].Id));
            recover = !recovery.NewRunRequired && checkpoint?.InputHash == inputs.InputHash;
        }
        if (!recover) preset = 0;
        var number = recover ? recovery!.Number : journal.NextRunNumber(jobId, previousRun);
        var priorResults = new List<ReplaySessionResult>();
        if (recover)
            for (var i = 0; i < preset; i++)
            {
                var prior = journal.Checkpoint(ReplayEvidenceRun.IdFor(jobId, number, inputs.Replay.Presets[i].Id));
                if (prior?.TimeUtc is null) continue;
                var restored = CreateSession(inputs, nextRunner, number, i, recovery!.AllPresets);
                if (!restored.IsComplete) continue;
                PersistManifest(inputs, number, i, Manifest(inputs, number, i), recovery!.AllPresets);
                priorResults.Add(restored.Result);
            }
        var next = CreateSession(inputs, nextRunner, number, preset, recover && recovery!.AllPresets, recover ? recoveredStep : null);
        Install(inputs, nextRunner, next, number, preset, recover && recovery!.AllPresets, priorResults, recover);
    }

    public void Restart() => Open(Inputs ?? throw new InvalidOperationException("Open a replay dataset first."), Number);

    public void PreparePlayback(ReplayPlaybackRequest request)
    {
        if (session is null || Inputs is null) throw new InvalidOperationException("Open a replay dataset first.");
        session.Pause();
        if (request == ReplayPlaybackRequest.AllPresets && PriceOnly) throw new ArgumentException("Engine-backed replay is required for preset comparison.");
        var continuing = request == ReplayPlaybackRequest.Resume && AllPresets && HasRemainingPresets;
        if (IsComplete && !HasPendingCommit && !continuing
            || request == ReplayPlaybackRequest.AllPresets && (CurrentTimeUtc is not null || PresetIndex != 0)) Restart();
        var previousScope = AllPresets;
        if (request is ReplayPlaybackRequest.CurrentPreset or ReplayPlaybackRequest.AllPresets)
            AllPresets = request == ReplayPlaybackRequest.AllPresets;
        if (openingStaged && previousScope != AllPresets && JournalRunId is { } id)
            try { journal.UpdateOpeningScope(jobId, id, AllPresets); }
            catch { AllPresets = previousScope; throw; }
        EnsureManifest();
    }

    // Returns true when the same comparison run has another preset to execute.
    public bool CompletePreset()
    {
        EnsureManifest();
        if (!IsComplete || HasPendingCommit) throw new InvalidOperationException("Commit and finish the current preset before completing it.");
        RememberCurrent();
        if (!AllPresets || !HasRemainingPresets) return false;
        var next = CreateSession(Inputs!, runner, Number, PresetIndex + 1, true);
        Install(Inputs!, runner, next, Number, PresetIndex + 1, true, completed.ToArray(), false);
        return true;
    }

    public ReplayRunResult CaptureResults()
    {
        EnsureManifest();
        if (!CanPublish)
            throw new InvalidOperationException("Finish the requested replay scope before publishing reports.");
        RememberCurrent();
        return PriceOnly ? new(Array.AsReadOnly(completed.ToArray()), Inputs!.Dataset.Issues, Inputs.Dataset,
            Inputs.Replay.ToJson(), Inputs.Engine.ToJson()) : runner!.CaptureResults(completed);
    }

    private bool HasRemainingPresets => !PriceOnly && PresetIndex + 1 < Inputs!.Replay.Presets.Count;
    public bool IsPaused => session?.IsPaused ?? true;
    public bool IsComplete => session?.IsComplete ?? false;
    public DateTimeOffset? CurrentTimeUtc => session?.CurrentTimeUtc;
    public DateTimeOffset? NextTimeUtc => session?.NextTimeUtc;
    public IReadOnlyList<ResearchTrade> VisibleTrades => session?.VisibleTrades ?? [];
    public IReadOnlyList<ReplayConstituentView> VisibleConstituents(string indexId) => session?.VisibleConstituents(indexId) ?? [];
    public ReplaySessionResult Result => session?.Result ?? throw new InvalidOperationException("Open a replay dataset first.");
    public bool PaperEntriesPaused => session?.PaperEntriesPaused ?? true;
    public PaperStateView? PaperState => session?.PaperState;
    public void Pause() => session?.Pause();
    public void Resume() { EnsureManifest(); session!.Resume(); }
    public ReplayStep? Step()
    {
        EnsureManifest();
        try
        {
            var step = session!.Step();
            if (step is not null) { LastStep = step; if (PriceOnly) previewStepNumber++; }
            return step;
        }
        catch { LastStep = Evidence?.LastStep ?? LastStep; throw; }
    }
    public void SetPaperEntriesPaused(bool paused) { EnsureManifest(); session!.SetPaperEntriesPaused(paused); }
    public void ReleaseCommittedDecisionHistory() { EnsureManifest(); session!.ReleaseCommittedDecisionHistory(); }

    public void AcknowledgeCheckpoint()
    {
        if (!openingStaged || ManifestPending || JournalRunId is not { } id) return;
        try { journal.AcknowledgeRunOpening(jobId, id); openingStaged = false; }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException)
        { /* Manifest and host checkpoint are durable. Retain the safe stage and retry cleanup on a later checkpoint. */ }
    }

    private IHistoricalReplaySession CreateSession(ReplayRunInputs inputs, ReplayRunner? source, int number, int preset, bool allPresets,
        Action<ReplayStep>? recoveredStep = null)
    {
        if (source is null) return new PriceOnlyReplaySession(inputs.Dataset, date, inputs.Replay.PublicationLag);
        var id = ReplayEvidenceRun.IdFor(jobId, number, inputs.Replay.Presets[preset].Id);
        if (!journal.HasRun(jobId, id)) journal.StageRunOpening(jobId, id, inputs.InputHash, Manifest(inputs, number, preset), allPresets);
        return new DurableReplaySession(source.CreateSession(date, inputs.Replay.Presets[preset].Id), journal,
            new(jobId, number, inputs.Replay.Presets[preset].Id, inputs.InputHash,
                HistoricalAcquisitionStore.Midnight(date, inputs.Dataset.Manifest.Indices[0].TimeZone).Add(inputs.Dataset.Manifest.MarketOpen.ToTimeSpan()),
                inputs.Dataset.Manifest.Indices[0].Id), recoveredStep);
    }
    private void Install(ReplayRunInputs inputs, ReplayRunner? source, IHistoricalReplaySession next, int number, int preset,
        bool allPresets, IReadOnlyList<ReplaySessionResult> results, bool recovered)
    {
        var manifest = source is null ? null : Manifest(inputs, number, preset);
        Inputs = inputs; runner = source; session = next; Number = number; PresetIndex = preset; AllPresets = allPresets;
        Recovered = recovered; previewStepNumber = 0; LastStep = Evidence?.LastStep;
        completed.Clear(); completed.AddRange(results);
        pendingManifest = manifest; openingStaged = false; ManifestError = null; session.Pause();
        EnsureManifest();
    }
    private void RememberCurrent()
    {
        var current = Result;
        if (!completed.Any(result => result.PresetId == current.PresetId && result.TradingDate == date)) completed.Add(current);
    }
    private void EnsureManifest()
    {
        if (session is null) throw new InvalidOperationException("Open a replay dataset first.");
        if (pendingManifest is null) return;
        try
        {
            PersistManifest(Inputs!, Number, PresetIndex, pendingManifest, AllPresets);
            openingStaged = journal.PendingRunOpenings(jobId).Any(opening => opening.RunId == JournalRunId);
            pendingManifest = null; ManifestError = null;
        }
        catch
        {
            session.Pause();
            ManifestError = "Run manifest publication is pending. Restore storage and retry playback; this run cannot advance before its manifest is durable.";
            throw;
        }
    }
    private void PersistManifest(ReplayRunInputs inputs, int number, int preset, string manifest, bool allPresets)
    {
        var id = ReplayEvidenceRun.IdFor(jobId, number, inputs.Replay.Presets[preset].Id);
        if (journal.HasRun(jobId, id)) return;
        journal.StageRunOpening(jobId, id, inputs.InputHash, manifest, allPresets);
        journal.RecordManifest(id, jobId, manifest);
    }
    private ReplayRunRecovery RecoverOpening(ReplayRunInputs inputs, int previousRun, ReplayRunRecovery recovery)
    {
        var highWater = journal.NextRunNumber(jobId, previousRun) - 1;
        var currentPreset = inputs.Replay.Presets.Select((preset, i) => (preset.Id, i)).SingleOrDefault(item => item.Id == recovery.ActivePresetId).i;
        foreach (var opening in journal.PendingRunOpenings(jobId))
        {
            using var document = JsonDocument.Parse(opening.ManifestJson); var root = document.RootElement;
            var number = root.GetProperty("run").GetInt32();
            if (number != highWater || number < recovery.Number || recovery.NewRunRequired && number <= recovery.Number
                || root.GetProperty("inputHash").GetString() != inputs.InputHash) continue;
            var preset = root.GetProperty("preset").GetProperty("Id").GetString();
            var index = inputs.Replay.Presets.Select((value, i) => (value.Id, i)).Where(item => item.Id == preset).Select(item => (int?)item.i).SingleOrDefault();
            if (index is null || number == recovery.Number && index < currentPreset) continue;
            return new(number, preset, opening.AllPresets);
        }
        return recovery;
    }
    private string Manifest(ReplayRunInputs inputs, int number, int preset)
    {
        var existing = journal.PendingRunOpenings(jobId).FirstOrDefault(opening => opening.RunId == ReplayEvidenceRun.IdFor(jobId, number, inputs.Replay.Presets[preset].Id));
        if (existing is not null) return existing.ManifestJson;
        return JsonSerializer.Serialize(new
    {
        schemaVersion = "replay-evidence-v2", run = number, jobId, runId = ReplayEvidenceRun.IdFor(jobId, number, inputs.Replay.Presets[preset].Id),
        date, preset = inputs.Replay.Presets[preset], createdAtUtc = clock.GetUtcNow(),
        datasetHash = HistoricalAcquisitionStore.SafeName(inputs.Dataset.ManifestSha256 + string.Join("|", inputs.Dataset.Files.OrderBy(file => file.InstrumentId, StringComparer.Ordinal).Select(file => file.Sha256))),
        inputHash = inputs.InputHash, replayConfiguration = inputs.Replay, engineConfiguration = inputs.Engine, dataset = inputs.Dataset.Manifest,
        codeVersions = inputs.CodeVersions.Select(version => new { name = version.Name, mvid = version.Mvid }),
        sources = inputs.Dataset.Files, quality = inputs.Dataset.Coverage, issues = inputs.Dataset.Issues, warnings = inputs.Warnings,
        evidenceClass = "HYPOTHETICAL_CANDLE_RESEARCH"
    }, KiteCandleImporter.JsonOptions);
    }
}
