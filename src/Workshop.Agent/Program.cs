using System.Globalization;
using Azure.Identity;
using Workshop.Agent.Engines;
using Workshop.Agent.Engines.Claude;
using Workshop.Agent.Engines.Foundry;
using Workshop.Agent.Runner;
using Workshop.Agent.Scenarios;
using Workshop.Agent.Tools;

namespace Workshop.Agent;

/// <summary>
/// The command line: <c>run</c> runs scenarios through the runner and writes their transcripts;
/// <c>scenarios check</c> reads a scenario set and reports its first problem. Exit codes: 0 done;
/// 1 a run met an infrastructure error, the scenario set is invalid, the app is missing, or the GPT
/// prompt agent could not be made to hold the code's definition; 2 a bad command line, or a Foundry
/// setting missing.
/// </summary>
internal static class Program
{
    public const string Usage = """
        Usage:
          Workshop.Agent run --engine fake|gpt|claude --scenarios <dir> [--only <id>] [--passes <n>] [--out <dir>] [--script-dir <dir>] [--app <exe>]
                             [--project-endpoint <url>] [--resource-endpoint <url>] [--gpt-deployment <name>] [--claude-deployment <name>] [--agent-name <name>]
          Workshop.Agent scenarios check <dir>

          --engine      fake runs each scenario's scripted model from <script-dir>/<id>.correct.json;
                        gpt runs the Foundry prompt agent, claude runs Claude in Foundry, both signed in with the Azure CLI.
          --scenarios   the directory of scenario files, <id>.json.
          --only        run this scenario only.
          --passes      passes per scenario (default 1).
          --out         where transcripts go (default %LOCALAPPDATA%\FoundryWorkshopAgent\transcripts\<timestamp>).
          --script-dir  the fake engine's scripts.
          --app         Workshop.App.exe (default: the one built beside this solution).

          gpt and claude read Foundry's settings from these options, or else from FWA_PROJECT_ENDPOINT,
          FWA_RESOURCE_ENDPOINT, FWA_GPT_DEPLOYMENT, FWA_CLAUDE_DEPLOYMENT and FWA_AGENT_NAME
          (default fwa-workshop-agent).
        """;

    private const int Done = 0;
    private const int Failed = 1;
    private const int BadCommandLine = 2;

    private static readonly string[] RunOptions =
        ["--engine", "--scenarios", "--only", "--passes", "--out", "--script-dir", "--app", .. FoundryOptions.Settings.Select(s => s.Option)];

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

    internal static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct) =>
        RunAsync(args, output, error, Environment.GetEnvironmentVariable, ct);

    /// <summary>For tests: <paramref name="environment"/> in place of the process's environment variables.</summary>
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Func<string, string?> environment, CancellationToken ct)
    {
        switch (args)
        {
            case ["run", .. var options]:
                return await RunScenariosAsync(options, output, error, environment, ct).ConfigureAwait(false);
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

    private static async Task<int> RunScenariosAsync(string[] args, TextWriter output, TextWriter error, Func<string, string?> environment, CancellationToken ct)
    {
        if (ReadOptions(args) is not { } options
            || !options.TryGetValue("--engine", out var engineName)
            || engineName is not (FakeEngine.Name or GptEngineFactory.Name or ClaudeEngineFactory.Name)
            || !options.TryGetValue("--scenarios", out var scenarioDir)
            || !TryPasses(options, out var passes))
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return BadCommandLine;
        }

        // Before anything is read or started: a run that cannot reach its model does not begin.
        string? scriptDir = null;
        FoundryOptions? foundry = null;
        if (engineName == FakeEngine.Name)
        {
            if (!options.TryGetValue("--script-dir", out scriptDir))
            {
                await error.WriteLineAsync("The fake engine needs --script-dir.").ConfigureAwait(false);
                return BadCommandLine;
            }
        }
        else
        {
            try
            {
                foundry = FoundryOptions.Read(options, environment);
            }
            catch (FoundrySettingsException e)
            {
                await error.WriteLineAsync(e.Message).ConfigureAwait(false);
                return BadCommandLine;
            }
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
            return Failed;
        }

        // A missing app is the setup's fault, not a run's: no run starts, and no transcript is written.
        if (!File.Exists(appExe))
        {
            await error.WriteLineAsync($"There is no app at '{appExe}'.").ConfigureAwait(false);
            return Failed;
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

        Func<Scenario, EngineRun, IAgentEngine> engineFor;
        if (foundry is null)
        {
            if (await LoadScriptsAsync(scenarios, scriptDir!, error).ConfigureAwait(false) is not { } scripts)
            {
                return BadCommandLine;
            }

            engineFor = (s, run) => FakeEngine.Create(scripts[s.Id], run);
        }
        else
        {
            // Keyless: the Azure CLI's sign-in (az login locally, the OIDC login in CI), asked once an hour, not once a call.
            var credential = new CachingTokenCredential(new AzureCliCredential());
            if (engineName == ClaudeEngineFactory.Name)
            {
                engineFor = (_, run) => ClaudeEngineFactory.Create(foundry, run, credential);
            }
            else
            {
                if (await EnsureAgentAsync(foundry, credential, error, ct).ConfigureAwait(false) is not { } agent)
                {
                    return Failed;
                }

                await output.WriteLineAsync($"Prompt agent {agent.Name} version {agent.Version} holds the code's instructions and tools.").ConfigureAwait(false);
                engineFor = (_, run) => GptEngineFactory.Create(foundry, agent, run, credential);
            }
        }

        var outDir = options.TryGetValue("--out", out var given) ? given : DefaultOutDir();
        var runner = new ScenarioRunner(appExe, engineFor, ScenarioRunner.GateFor);
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

    /// <summary>Every scenario's script, read before the first app starts, so a missing or broken one stops nothing half-way; null after reporting one.</summary>
    private static async Task<Dictionary<string, Script>?> LoadScriptsAsync(Scenario[] scenarios, string scriptDir, TextWriter error)
    {
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
                return null;
            }
        }

        return scripts;
    }

    /// <summary>
    /// The GPT prompt agent's version that holds the code's instructions and tools, made or reused
    /// before the first run (Review Focus 2); null after reporting why there is none: it drifted, or
    /// Foundry could not be reached.
    /// </summary>
    /// <remarks><paramref name="transport"/> is for tests: the project client's HTTP transport.</remarks>
    internal static async Task<AgentVersionRef?> EnsureAgentAsync(FoundryOptions foundry, Azure.Core.TokenCredential credential, TextWriter error, CancellationToken ct, System.ClientModel.Primitives.PipelineTransport? transport = null)
    {
        try
        {
            return await PromptAgentProvisioner.EnsureAsync(foundry, WorkshopTools.Declarations, credential, transport, ct).ConfigureAwait(false);
        }
        catch (AgentDriftException e)
        {
            await error.WriteLineAsync($"{Redaction.Redact(e.Message)} No run starts.").ConfigureAwait(false);
            return null;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            await error.WriteLineAsync($"The prompt agent could not be checked, so no run starts: {Redaction.Describe(e, fullName: false)}").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>The console's line for one run; its infrastructure message is redacted again, whatever wrote it.</summary>
    internal static string Line(Transcript t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var verdict = t.InfraError ? $"infrastructure error: {Redaction.Redact(t.InfraMessage)}"
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
