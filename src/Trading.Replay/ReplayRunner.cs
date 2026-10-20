using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trading.Data;
using Trading.Domain;
using Trading.Engine;

namespace Trading.Replay;

public sealed class ReplayRunner
{
    private readonly HistoricalDataset dataset;
    private readonly ReplayConfiguration configuration;
    private readonly EngineConfiguration engineConfiguration;
    public ReplayRunner(HistoricalDataset dataset, ReplayConfiguration configuration, EngineConfiguration engineConfiguration)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        configuration.Validate();
        this.configuration = ReplayConfiguration.FromJson(configuration.ToJson());
        this.engineConfiguration = EngineConfiguration.FromJson(engineConfiguration.ToJson());
        var manifest = JsonSerializer.Deserialize<HistoricalDatasetManifest>(JsonSerializer.Serialize(dataset.Manifest, KiteCandleImporter.JsonOptions), KiteCandleImporter.JsonOptions)!;
        this.dataset = dataset with
        {
            Manifest = manifest with
            {
                TradingDates = Array.AsReadOnly(manifest.TradingDates.ToArray()), Indices = Array.AsReadOnly(manifest.Indices.ToArray()),
                Instruments = Array.AsReadOnly(manifest.Instruments.ToArray()),
                Weights = Array.AsReadOnly(manifest.Weights.Select(weights => weights with
                    { Members = Array.AsReadOnly(weights.Members.ToArray()) }).ToArray())
            },
            Candles = Array.AsReadOnly(dataset.Candles.ToArray()), Issues = Array.AsReadOnly(dataset.Issues.ToArray()),
            Files = Array.AsReadOnly(dataset.Files.ToArray()), Coverage = dataset.Coverage is null ? null : Array.AsReadOnly(dataset.Coverage.ToArray())
        };
        dataset = this.dataset;
        KiteCandleImporter.ValidateManifest(dataset.Manifest);
        if (dataset.Manifest.Indices.Select(index => index.TimeZone).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new ArgumentException("One replay experiment currently requires indices sharing a session timezone.");
        foreach (var profile in this.engineConfiguration.Strategies.Where(profile => profile.Enabled
            && dataset.Manifest.Indices.Any(index => index.Id == profile.IndexId)))
            if (profile.OpeningStart != dataset.Manifest.MarketOpen || profile.SessionExit >= dataset.Manifest.MarketClose)
                throw new ArgumentException("ORB session start must match market open, and session exit precede market close.");
        if (dataset.Candles.Any(bar => bar.AvailableAtUtc != bar.EndUtc.Add(configuration.PublicationLag)))
            throw new ArgumentException("Dataset must be normalized under this replay's publication-lag assumption.");
    }

    public IReadOnlyList<DateOnly> Dates => Array.AsReadOnly(dataset.Manifest.TradingDates
        .Where(date => (configuration.FromDate is null || date >= configuration.FromDate)
            && (configuration.ToDate is null || date <= configuration.ToDate)).Order().ToArray());

    public CandleReplaySession CreateSession(DateOnly date, string presetId)
    {
        if (!Dates.Contains(date)) throw new ArgumentException("Date is outside the declared/requested dataset.", nameof(date));
        var preset = configuration.Presets.SingleOrDefault(preset => preset.Id == presetId)
            ?? throw new ArgumentException("Unknown execution preset.", nameof(presetId));
        return new(dataset, configuration, engineConfiguration, preset, date);
    }

    public ReplayRunResult RunToEnd()
    {
        var results = new List<ReplaySessionResult>();
        foreach (var date in Dates)
            foreach (var preset in configuration.Presets)
            {
                var session = CreateSession(date, preset.Id);
                session.RunToEnd();
                results.Add(session.Result);
            }
        return CaptureResults(results);
    }

    public ReplayRunResult CaptureResults(IEnumerable<ReplaySessionResult> results) => new(Array.AsReadOnly(results.ToArray()),
        dataset.Issues, dataset, configuration.ToJson(), engineConfiguration.ToJson());

    internal static string Key(params string[] values) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n", values.Select(value => $"{value.Length}:{value}")))));
}

internal static class ReplaySchedule
{
    public static IReadOnlyList<ReplayBatch> Build(HistoricalDataset dataset, EngineSession session,
        TimeSpan lag)
    {
        var start = session.At(dataset.Manifest.MarketOpen);
        var end = session.At(dataset.Manifest.MarketClose);
        var bars = dataset.Candles.Where(bar => bar.StartUtc >= start && bar.StartUtc < end).ToArray();
        var completed = bars.GroupBy(bar => bar.AvailableAtUtc).ToDictionary(group => group.Key,
            group => (IReadOnlyList<HistoricalCandle>)Array.AsReadOnly(group.OrderBy(bar => bar.InstrumentId, StringComparer.Ordinal).ToArray()));
        var openings = bars.GroupBy(bar => bar.StartUtc).ToDictionary(group => group.Key,
            group => (IReadOnlyList<CandleOpening>)Array.AsReadOnly(group.OrderBy(bar => bar.InstrumentId, StringComparer.Ordinal)
                .Select(bar => new CandleOpening(bar.InstrumentId, bar.Id, bar.StartUtc, bar.Open)).ToArray()));
        var times = completed.Keys.Concat(openings.Keys).ToHashSet();
        for (var tick = start; tick <= end; tick = tick.AddMinutes(1)) times.Add(tick);
        times.Add(end.Add(lag));
        return Array.AsReadOnly(times.Order().Select(time => new ReplayBatch(time,
            completed.GetValueOrDefault(time) ?? [], openings.GetValueOrDefault(time) ?? [])).ToArray());
    }
}
