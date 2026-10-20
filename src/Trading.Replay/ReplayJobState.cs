using System.Text.Json.Serialization;

namespace Trading.Replay;

/// <summary>
/// The lifecycle state of a replay job. Names are the wire and persisted values, so existing
/// job files and clients keep working. NeedsMetadata is never assigned today; it is kept so an
/// old job file or client that names it still reads.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReplayJobState
{
    Queued, Resolving, Downloading, Validating, MarketClosed, NoData, Ready, ReadyWithWarnings,
    Playing, Paused, Reporting, Completed, Failed, Cancelled, Interrupted, AuthenticationRequired, NeedsMetadata
}

/// <summary>
/// What a job is doing beyond its state. A state can lag the work: Failed or Cancelled may be shown while the
/// acquisition worker is still stopping, and a finished preview can be publishing while acquisition continues.
/// </summary>
[Flags]
public enum ReplayJobPhase
{
    Idle = 0,
    /// <summary>The acquisition worker owns the job.</summary>
    Acquiring = 1,
    /// <summary>Acquisition was cancelled or has failed, and the worker has not released the job yet.</summary>
    Stopping = 2,
    /// <summary>A report is being published.</summary>
    Publishing = 4
}

public static class ReplayJobStates
{
    /// <summary>Preparation is queued or running; replay is not yet playable from this attempt.</summary>
    public static bool IsPreparing(this ReplayJobState state) =>
        state is ReplayJobState.Queued or ReplayJobState.Resolving or ReplayJobState.Downloading or ReplayJobState.Validating;
}
