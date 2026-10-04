using System.Globalization;
using Workshop.Agent.Engines;
using Workshop.Agent.Runner;
using Workshop.Agent.Scenarios;

namespace Workshop.Agent;

/// <summary>
/// The command line: <c>run</c> runs scenarios through the runner and writes their transcripts;
/// <c>scenarios check</c> reads a scenario set and reports its first problem. Exit codes: 0 done,
/// 1 a run met an infrastructure error or the set is invalid, 2 a bad command line.
/// </summary>
internal static class Program
{
    public const string Usage = """
        Usage:
          Workshop.Agent run --engine fake|gpt|claude --scenarios <dir> [--only <id>] [--passes <n>] [--out <dir>] [--script-dir <dir>] [--app <exe>]
          Workshop.Agent scenarios check <dir>

          --engine      fake runs each scenario's scripted model from <script-dir>/<id>.correct.json.
          --scenarios   the directory of scenario files, <id>.json.
          --only        run this scenario only.
          --passes      passes per scenario (default 1).
          --out         where transcripts go (default %LOCALAPPDATA%\FoundryWorkshopAgent\transcripts\<timestamp>).
          --script-dir  the fake engine's scripts.
          --app         Workshop.App.exe (default: the one built beside this solution).
        """;

    private const int Done = 0;
    private const int Failed = 1;
    private const int BadCommandLine = 2;

    private static readonly string[] RunOptions = ["--engine", "--scenarios", "--only", "--passes", "--out", "--script-dir", "--app"];

    public static async Task<int> Main(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        // Ctrl+C cancels the run, so the runner closes its app; a second Ctrl+C ends the process,
        // and the app's kill-on-close job ends the app with it.
        Console.CancelKeyPress += (_, e) =>
        {
            if (!cancel.IsCancellationRequested)
            {
                e.Cancel = true;
                cancel.Cancel();
            }
        };

        try
        {
            return await RunAsync(args, Console.Out, Console.Error, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return Failed;
        }
    }

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        switch (args)
        {
            case ["run", .. var options]:
                return await RunScenariosAsync(options, output, error, ct).ConfigureAwait(false);
            case ["scenarios", "check", var dir]:
                return await CheckScenariosAsync(dir, output, error).ConfigureAwait(false);
            default:
                await error.WriteLineAsync(Usage).ConfigureAwait(false);
                return BadCommandLine;
        }
    }

    private static async Task<int> CheckScenariosAsync(string dir, TextWriter output, TextWriter error)
    {
        try
        {
            var scenarios = ScenarioLoader.LoadAll(dir);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{scenarios.Length} scenarios are valid.")).ConfigureAwait(false);
            return Done;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return Failed;
        }
    }

    private static async Task<int> RunScenariosAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (ReadOptions(args) is not { } options
            || !options.TryGetValue("--engine", out var engineName)
            || !options.TryGetValue("--scenarios", out var scenarioDir)
            || !TryPasses(options, out var passes))
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return BadCommandLine;
        }

        if (engineName is "gpt" or "claude")
        {
            await error.WriteLineAsync($"The {engineName} engine arrives in plan 3.").ConfigureAwait(false);
            return BadCommandLine;
        }

        if (engineName != FakeEngine.Name || !options.TryGetValue("--script-dir", out var scriptDir))
        {
            await error.WriteLineAsync(engineName == FakeEngine.Name ? "The fake engine needs --script-dir." : Usage).ConfigureAwait(false);
            return BadCommandLine;
        }

        Scenario[] scenarios;
        string appExe;
        try
        {
            scenarios = ScenarioLoader.LoadAll(scenarioDir);
            appExe = options.TryGetValue("--app", out var app) ? app : AppProcess.FindAppExe();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return BadCommandLine;
        }

        if (options.TryGetValue("--only", out var only))
        {
            scenarios = [.. scenarios.Where(s => s.Id == only)];
            if (scenarios.Length == 0)
            {
                await error.WriteLineAsync($"There is no scenario '{only}' in '{scenarioDir}'.").ConfigureAwait(false);
                return BadCommandLine;
            }
        }

        // Every script is read before the first app starts, so a missing or broken one stops nothing half-way.
        var scripts = new Dictionary<string, Script>(StringComparer.Ordinal);
        foreach (var s in scenarios)
        {
            var path = Path.Combine(scriptDir, $"{s.Id}.correct.json");
            try
            {
                scripts[s.Id] = Script.Load(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
            {
                await error.WriteLineAsync($"The script '{path}' cannot be read: {e.Message}").ConfigureAwait(false);
                return BadCommandLine;
            }
        }

        var outDir = options.TryGetValue("--out", out var given) ? given : DefaultOutDir();
        var runner = new ScenarioRunner(appExe, (s, run) => FakeEngine.Create(scripts[s.Id], run), ScenarioRunner.GateFor);
        await output.WriteLineAsync($"Transcripts go to {Path.GetFullPath(outDir)}").ConfigureAwait(false);

        int succeeded = 0, infraErrors = 0, runs = 0;
        foreach (var s in scenarios)
        {
            for (var pass = 1; pass <= passes; pass++)
            {
                var t = await runner.RunAsync(s, pass, outDir, ct).ConfigureAwait(false);
                runs++;
                succeeded += t.Success ? 1 : 0;
                infraErrors += t.InfraError ? 1 : 0;
                await output.WriteLineAsync(Line(t)).ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{succeeded} of {runs} runs succeeded; {infraErrors} infrastructure errors.")).ConfigureAwait(false);
        // A task failure is a result, not an error; a run that never got to try is.
        return infraErrors == 0 ? Done : Failed;
    }

    private static string Line(Transcript t)
    {
        var verdict = t.InfraError ? $"infrastructure error: {t.InfraMessage}"
            : t.Success ? "success"
            : "failure";
        return string.Create(CultureInfo.InvariantCulture, $"{t.ScenarioId} p{t.Pass}: {verdict} ({t.Outcome}, {t.Tools.Count} tool calls, {t.GateViolations} gate violations, {t.Ms / 1000:0.0} s)");
    }

    /// <summary>The options as name and value, or null when one is unknown, repeated or has no value.</summary>
    private static Dictionary<string, string>? ReadOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!RunOptions.Contains(args[i], StringComparer.Ordinal) || i + 1 >= args.Length || !options.TryAdd(args[i], args[i + 1]))
            {
                return null;
            }
        }

        return options;
    }

    private static bool TryPasses(Dictionary<string, string> options, out int passes)
    {
        passes = 1;
        return !options.TryGetValue("--passes", out var text)
            || (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out passes) && passes >= 1);
    }

    private static string DefaultOutDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FoundryWorkshopAgent",
        "transcripts",
        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
}
