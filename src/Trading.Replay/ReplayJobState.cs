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

public static class ReplayJobStates
{
    /// <summary>Preparation is queued or running; replay is not yet playable from this attempt.</summary>
    public static bool IsPreparing(this ReplayJobState state) =>
        state is ReplayJobState.Queued or ReplayJobState.Resolving or ReplayJobState.Downloading or ReplayJobState.Validating;
}
