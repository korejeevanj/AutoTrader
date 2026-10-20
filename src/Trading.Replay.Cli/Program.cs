using Trading.Data;
using Trading.Engine;
using Trading.Replay;

if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("Usage: replay run|step --dataset <dataset.json> --config <replay.json> --out <new-directory> [--speed <positive multiplier>]");
    Console.WriteLine("       replay compare-rules --dataset <dataset.json> --config <replay.json> --out <new-directory> --minimum-confirming-constituents <positive count>");
    Console.WriteLine("run executes all configured dates/presets. step interactively advances the first date/preset: Enter=one batch, r=resume, p=pause, q=stop.");
    return 0;
}
try
{
    var command = args[0];
    if (command is not ("run" or "step" or "compare-rules")) throw new ArgumentException("Command must be run, step or compare-rules.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || args[i] is not ("--dataset" or "--config" or "--out" or "--speed" or "--minimum-confirming-constituents")
            || !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown, duplicate or incomplete command option.");
    }
    if (!options.ContainsKey("--dataset") || !options.ContainsKey("--config") || !options.ContainsKey("--out"))
        throw new ArgumentException("--dataset, --config and --out are required.");
    if (command == "compare-rules" && (!options.ContainsKey("--minimum-confirming-constituents") || options.ContainsKey("--speed"))
        || command != "compare-rules" && options.ContainsKey("--minimum-confirming-constituents"))
        throw new ArgumentException("compare-rules requires --minimum-confirming-constituents and does not accept --speed.");
    var speed = options.TryGetValue("--speed", out var text)
        ? double.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : 0d;
    if (options.ContainsKey("--speed") && (!double.IsFinite(speed) || speed <= 0d)) throw new ArgumentException("Speed must be finite and positive.");
    var configPath = Path.GetFullPath(options["--config"]);
    var configuration = ReplayConfiguration.FromJson(File.ReadAllText(configPath));
    var enginePath = Path.GetFullPath(configuration.EngineProfile, Path.GetDirectoryName(configPath)!);
    var engine = EngineConfiguration.FromJson(File.ReadAllText(enginePath));
    var dataset = KiteCandleImporter.Load(options["--dataset"], configuration.PublicationLag);
    if (command == "compare-rules")
    {
        var minimum = int.Parse(options["--minimum-confirming-constituents"], System.Globalization.CultureInfo.InvariantCulture);
        var comparison = ReplayRuleComparison.Run(dataset, configuration, engine, minimum);
        if (comparison.Sessions.Count == 0) throw new ArgumentException("No declared dates match the requested range.");
        ReplayReportWriter.WriteComparison(options["--out"], comparison);
        Console.WriteLine($"Saved {comparison.Sessions.Count} paired rule experiments to {Path.GetFullPath(options["--out"])}");
        return 0;
    }
    var runner = new ReplayRunner(dataset, configuration, engine);
    if (runner.Dates.Count == 0) throw new ArgumentException("No declared dates match the requested range.");
    ReplayRunResult result;
    if (command == "run" && speed == 0d) result = runner.RunToEnd();
    else
    {
        var results = new List<ReplaySessionResult>();
        var dates = command == "step" ? runner.Dates.Take(1) : runner.Dates;
        var presets = command == "step" ? configuration.Presets.Take(1) : configuration.Presets;
        foreach (var date in dates)
            foreach (var preset in presets)
            {
                var session = runner.CreateSession(date, preset.Id);
                if (command == "step") session.Pause();
                while (!session.IsComplete)
                {
                    if (session.IsPaused)
                    {
                        Console.Write("Enter=step, r=resume, q=stop > ");
                        var input = Console.ReadLine();
                        if (input is null or "q") break;
                        if (input == "r") session.Resume();
                        else if (input.Length != 0) continue;
                    }
                    else if (command == "step" && !Console.IsInputRedirected && Console.KeyAvailable)
                    {
                        if (Console.ReadKey(intercept: true).Key == ConsoleKey.P) { session.Pause(); continue; }
                    }
                    var previous = session.CurrentTimeUtc;
                    var step = session.Step()!;
                    Console.WriteLine($"{step.TimeUtc:O} available={step.AvailableCandleCount} positions={step.OpenPositions} pending={step.PendingEntries} net={step.NetSessionPnl?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}");
                    if (speed > 0d && previous is { } prior)
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(60000d, (step.TimeUtc - prior).TotalMilliseconds / speed)));
                }
                results.Add(session.Result);
            }
        result = runner.CaptureResults(results);
    }
    ReplayReportWriter.Write(options["--out"], result);
    Console.WriteLine($"Saved {result.Sessions.Count} session/preset experiments to {Path.GetFullPath(options["--out"])}");
    Console.WriteLine($"Dataset issues: {dataset.Issues.Count}; incomplete experiments: {result.Sessions.Count(session => !session.Complete)}");
    return 0;
}
catch (Exception error) when (error is ArgumentException or IOException or System.Text.Json.JsonException or FormatException
    or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException)
{
    Console.Error.WriteLine($"Replay failed: {error.Message}");
    return 1;
}
