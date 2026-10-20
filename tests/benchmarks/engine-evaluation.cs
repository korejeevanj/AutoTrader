#:project ../../src/Trading.Engine/Trading.Engine.csproj
#:property PublishAot=false

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trading.Domain;
using Trading.Engine;

// .NET 10 file-based benchmark. All prices, contracts, weights and policies are synthetic.
// No providers, files, database, network or wall clock are used by Engine evaluation.
var ids = new[] { "BANKNIFTY", "NIFTY", "SENSEX" };
var configuration = new EngineConfiguration
{
    Strategies = ids.Select(id => new IndexStrategyConfiguration { IndexId = id }).ToArray(),
    Portfolio = new() { IndexPriority = ids }
};
var engine = new TradingDecisionEngine(configuration);
var inputs = ids.Select(BuildInput).ToArray();
var states = inputs.Select(input => engine.EvaluateIndex(input).State).ToArray();
if (args.Contains("--trace", StringComparer.Ordinal))
{
    var input = inputs[0];
    var scan = engine.EvaluateIndex(input, states[0]);
    var recommendation = engine.Allocate(input.Identity, input.AsOfUtc, input.Session, input.Portfolio, [scan])
        .Decisions.Single(decision => decision.Action == EntryAction.ENTER);
    var candidate = input.Candidates.Single(candidate => candidate.Contract.Id == recommendation.ContractId);
    var strategy = configuration.Strategies[0] with { Position = new() { ProfitExit = new() { Mode = ProfitExitMode.FixedNetTarget } } };
    var position = new PaperPositionEvidence("synthetic-position", input.Index.Id, input.Session.Id, "synthetic-fill-v1",
        candidate.Contract, TradeDirection.Bullish, input.AsOfUtc, 200m, 25, 20m, configuration.Version,
        strategy, input.FuturesInstrumentId, 96.4m, 8.6m, 105m);
    var quote = candidate.Quote! with { Bid = 200m, Ask = 201m };
    var mark = new NetLiquidationEvidence("synthetic-net", position.Id, position.EntryFillVersion, quote.Id,
        input.AsOfUtc, input.AsOfUtc.AddSeconds(1), "synthetic-risk-v1", "synthetic-fees-v1", 0m, 20m, 20m, -40m);
    var positionInput = new PositionEvaluationInput(input.Identity, input.AsOfUtc, input.Session, position, quote, mark,
        input.FuturesPrice, new("synthetic-progress", position.Id, input.FuturesPrice!.Id, input.AsOfUtc,
            "synthetic-risk-v1", 8.6m, 105m, 0m), input.FuturesBars, input.FuturesFeatures,
        input.ConstituentDefinitions, input.Constituents, input.Portfolio);
    Console.WriteLine(JsonSerializer.Serialize(new { evidenceClass = "SYNTHETIC_POLICY_TRACE_NOT_BROKER_FILLS",
        recommendation, reconciledPosition = engine.EvaluatePosition(positionInput),
        inconsistentNetPosition = engine.EvaluatePosition(positionInput with
            { Identity = input.Identity with { EvidenceVersion = "synthetic-inconsistent-net-evidence-v1" },
                NetLiquidation = mark with { NetPnl = 500m } }) },
        new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
    return;
}
var results = new List<object>();
var monitoringInput = BuildPosition(inputs[0]);
foreach (var (indexCount, monitorPosition) in new[] { (1, false), (3, false), (1, true), (3, true) })
{
    const int warmup = 200, samples = 1000;
    int Evaluate()
    {
        // Risk runs first. This measures one active position under the default portfolio cap.
        if (monitorPosition && engine.EvaluatePosition(monitoringInput).Action != PositionAction.HOLD)
            throw new InvalidOperationException("Expected a qualified synthetic HOLD before scanning entries.");
        var scans = Enumerable.Range(0, indexCount).Select(i => engine.EvaluateIndex(inputs[i], states[i])).ToArray();
        if (scans.Any(scan => scan.Candidates.Count != 35 || scan.Candidates.Any(candidate => !candidate.Eligible)))
            throw new InvalidOperationException("Benchmark must exercise 35 eligible synthetic candidates per index.");
        return engine.Allocate(inputs[0].Identity, inputs[0].AsOfUtc, inputs[0].Session, inputs[0].Portfolio, scans)
            .Decisions.Count(decision => decision.Action == EntryAction.ENTER);
    }
    for (var i = 0; i < warmup; i++) Evaluate();
    var elapsed = new double[samples];
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < samples; i++)
    {
        var start = Stopwatch.GetTimestamp();
        if (Evaluate() != 1) throw new InvalidOperationException("Expected one recommendation under the frozen portfolio cap.");
        elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    var bytesPerSample = (GC.GetAllocatedBytesForCurrentThread() - allocated) / samples;
    Array.Sort(elapsed);
    results.Add(new { indexCount, candidatesPerIndex = 35, positionChecksPerSample = monitorPosition ? 1 : 0, warmup, samples,
        p50Ms = elapsed[499], p95Ms = elapsed[949], maximumMs = elapsed[^1], bytesPerSample,
        targetP95Ms = 50, targetMet = elapsed[949] < 50 });
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    evidenceClass = "SYNTHETIC_INTERNAL_ENGINE_BENCHMARK",
    scope = "EvaluatePosition first where specified, then steady-state EvaluateIndex plus Allocate; excludes feed/features/IO/UI.",
    framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
    configurationFingerprint = engine.ConfigurationFingerprint, results
}, new JsonSerializerOptions { WriteIndented = true }));

static PositionEvaluationInput BuildPosition(IndexEvaluationInput input)
{
    var option = input.Candidates[5];
    var position = new PaperPositionEvidence("benchmark-position", input.Index.Id, input.Session.Id, "benchmark-fill-v1",
        option.Contract, TradeDirection.Bullish, input.AsOfUtc, 200m, 25, 20m, "paper-policy-v1",
        new() { IndexId = input.Index.Id }, input.FuturesInstrumentId, 96.4m, 8.6m, 105m);
    var quote = option.Quote! with { Bid = 200m, Ask = 201m };
    return new(input.Identity, input.AsOfUtc, input.Session, position, quote,
        new("benchmark-net", position.Id, position.EntryFillVersion, quote.Id, input.AsOfUtc, input.AsOfUtc.AddSeconds(1),
            "synthetic-risk-v1", "synthetic-fees-v1", 0m, 20m, 20m, -40m), input.FuturesPrice,
        new("benchmark-progress", position.Id, input.FuturesPrice!.Id, input.AsOfUtc, "synthetic-risk-v1", 8.6m, 105m, 0m),
        input.FuturesBars, input.FuturesFeatures, input.ConstituentDefinitions, input.Constituents, input.Portfolio);
}

static IndexEvaluationInput BuildInput(string indexId)
{
    var session = new EngineSession("2026-10-12", new(2026, 10, 12), new(2026, 10, 11, 18, 30, 0, TimeSpan.Zero));
    var start = session.At(new(9, 15));
    var futureId = $"{indexId}:synthetic-futures";
    var bars = Enumerable.Range(0, 16).Select(i => new CompletedBarEvidence($"{indexId}:bar-{i}", futureId, session.Id,
        start.AddMinutes(i), start.AddMinutes(i + 1), start.AddMinutes(i + 1).AddSeconds(1),
        100m, i == 15 ? 106m : 103m, 97m, i == 15 ? 105m : 100m, 100m)).ToArray();
    var asOf = bars[^1].AvailableAtUtc.AddSeconds(1);
    var features = bars.Select((bar, i) => new FuturesFeatureEvidence($"feature:{bar.Id}", bar.Id, session.Id,
        "paper-policy-v1", bar.AvailableAtUtc, bars.Take(i + 1).Select(item => item.Id).ToArray(),
        100m, 14, 0.1m, 96.4m, 8.6m, 106.6m, 1.6m, "synthetic-feature-v1", true)).ToArray();
    var underlying = new UnderlyingPriceEvidence($"{indexId}:underlying", futureId, 105m,
        asOf.AddMilliseconds(-500), asOf.AddMilliseconds(-100), asOf.AddMilliseconds(-100), "synthetic-v1");
    var candidates = Enumerable.Range(0, 35).Select(i =>
    {
        var id = $"{indexId}:synthetic-option-{i:D2}";
        var contract = new OptionContractEvidence(id, indexId, "SYNTHETIC", OptionRight.Call, new(2026, 10, 29),
            100m + i, 25, 0.05m, start.AddDays(-1), start.AddDays(17), start.AddDays(-1), "synthetic-contracts-v1", true);
        var quote = new QuoteEvidence($"quote:{id}", id, 199m, 200m, 100, 100,
            asOf.AddMilliseconds(-500), asOf.AddMilliseconds(-100), asOf.AddMilliseconds(-500),
            asOf.AddMilliseconds(-100), "synthetic-v1", 1);
        var risk = new EntryRiskEvidence($"risk:{id}", id, contract.DefinitionVersion, quote.Id,
            bars[^1].Id, "paper-policy-v1", 25, asOf, asOf.AddMinutes(1), "paper-fees", "synthetic-fees-v1",
            start.AddDays(-1), null, "synthetic-risk-v1", "paper-fill-v1", 0m, 0m, 200m,
            true, true, true, 200m, 40m, 5040m, 350m, 500m, 0.5m, 65m, Math.Abs(contract.Strike - 105m),
            underlying.Id, features[^1].Id);
        return new OptionCandidateEvidence(contract, quote, risk);
    }).ToArray();
    // Each index has separate fictional members; these are not Bank Nifty data reused for other indices.
    var members = new[] { new ConstituentMember($"{indexId}:member-a", 65m), new ConstituentMember($"{indexId}:member-b", 35m) };
    var constituents = members.Select((member, i) => new ConstituentBarEvidence(member.InstrumentId, session.Id,
        bars[^1].StartUtc, bars[^1].EndUtc, bars[^1].AvailableAtUtc, i == 0 ? 110m : 90m, 100m, true, "synthetic-v1")).ToArray();
    return new(new("benchmark", "synthetic-evidence-v1", "benchmark-code"), asOf,
        new(indexId, indexId, "SYNTHETIC", "Asia/Kolkata", "Fictional members", "synthetic-index-v1",
            start.AddDays(-1), null, true, new(true, true, true)), session, futureId, bars, features,
        new("synthetic-weights-v1", start.AddDays(-1), null, start.AddDays(-1), true, members), constituents,
        underlying, $"{indexId}:synthetic-universe-v1", start, candidates,
        new("portfolio-v1", session.Id, asOf, 500000m, 0m, false, false, true, true, [], []),
        FuturesContract: new(futureId, indexId, start.AddDays(-1), start.AddDays(17), start.AddDays(-1), "synthetic-futures-v1"));
}
