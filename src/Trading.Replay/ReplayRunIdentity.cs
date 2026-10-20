using Trading.Data;
using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

/// <summary>
/// Defines which frozen inputs and which code identify a replay run. A saved run is
/// recoverable only when the derived input hash still matches, so every assembly that
/// can change replay behaviour is listed here and nowhere else.
/// </summary>
public static class ReplayRunIdentity
{
    private static readonly System.Reflection.Assembly[] Assemblies =
    [
        typeof(ReplayRunner).Assembly, typeof(TradingDecisionEngine).Assembly,
        typeof(Trading.Risk.HistoricalResearchCalculations).Assembly, typeof(Trading.Analytics.HistoricalSessionAnalytics).Assembly,
        typeof(KiteCandleImporter).Assembly, typeof(HistoricalCandle).Assembly
    ];

    public static IReadOnlyList<ReplayCodeVersion> CodeVersions { get; } = Assemblies
        .Select(assembly => new ReplayCodeVersion(assembly.GetName().Name!, assembly.ManifestModule.ModuleVersionId)).ToArray();

    public static string InputHash(HistoricalDataset dataset, ReplayConfiguration replay, EngineConfiguration engine) =>
        HistoricalAcquisitionStore.SafeName(dataset.ManifestSha256
            + string.Join("|", dataset.Files.OrderBy(file => file.InstrumentId, StringComparer.Ordinal).Select(file => file.Sha256))
            + replay.ToJson() + engine.ToJson()
            + string.Concat(Assemblies.Select(assembly => assembly.ManifestModule.ModuleVersionId)));
}
