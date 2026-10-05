using System.Globalization;
using Azure.Identity;
using Workshop.Agent.Engines;
using Workshop.Agent.Engines.Claude;
using Workshop.Agent.Engines.Foundry;
using Workshop.Agent.Runner;
using Workshop.Agent.Scenarios;
using Workshop.Agent.Telemetry;
using Workshop.Agent.Tools;

namespace Workshop.Agent;

/// <summary>
/// The command line: <c>run</c> runs scenarios through the runner and writes their transcripts;
/// <c>scenarios check</c> reads a scenario set and reports its first problem; <c>scenarios freeze</c>
/// writes the set's <c>freeze.json</c>. Exit codes: 0 done; 1 a run met an infrastructure error, the
/// scenario set is invalid, the freeze is missing or broken, the app is missing, or the GPT prompt
/// agent could not be made to hold the code's definition; 2 a bad command line, a Foundry setting
/// missing, or <c>--trace</c> without a usable Application Insights connection string.
/// </summary>
internal static class Program
{
    public const string Usage = """
        Usage:
          Workshop.Agent run --engine fake|gpt|claude --scenarios <dir> [--only <id>] [--passes <n>] [--out <dir>] [--script-dir <dir>] [--app <exe>]
                             [--project-endpoint <url>] [--resource-endpoint <url>] [--gpt-deployment <name>] [--claude-deployment <name>] [--agent-name <name>]
                             [--study | --frozen] [--trace [--trace-content]]
          Workshop.Agent scenarios check <dir>
          Workshop.Agent scenarios freeze <dir>

          --engine      fake runs each scenario's scripted model from <script-dir>/<id>.correct.json;
                        gpt runs the Foundry prompt agent, claude runs Claude in Foundry, both signed in with the Azure CLI.
          --scenarios   the directory of scenario files, <id>.json.
          --only        run this scenario only.
          --passes      passes per scenario (default 1).
          --out         where transcripts go (default %LOCALAPPDATA%\FoundryWorkshopAgent\transcripts\<timestamp>).
          --script-dir  the fake engine's scripts.
          --app         Workshop.App.exe (default: the one built beside this solution).
          --study       a study run: <scenarios>/freeze.json must match the scenarios, instructions, settings, tools and
                        decision rule, or no run starts; runs exactly 3 passes, in an output directory named with the freeze's short hash.
          --frozen      the same check with any number of passes and the output directory as given.
          --trace       sends the traces to the Application Insights FWA_APPINSIGHTS_CONNECTION_STRING names,
                        signed in with the Azure CLI; without message content.
          --trace-content  also puts the messages, tool arguments and tool results in the traces: for development
                        only, never with --study or --frozen.
          scenarios freeze  writes <dir>/freeze.json: a SHA-256 for each scenario and for the instructions, settings and tools,
                        and the decision rule. It does not overwrite a freeze.

          gpt and claude read Foundry's settings from these options, or else from FWA_PROJECT_ENDPOINT,
          FWA_RESOURCE_ENDPOINT, FWA_GPT_DEPLOYMENT, FWA_CLAUDE_DEPLOYMENT and FWA_AGENT_NAME
          (default fwa-workshop-agent).
        """;

    private const int Done = 0;
    private const int Failed = 1;
    private const int BadCommandLine = 2;

    private const string StudyFlag = "--study";
    private const string FrozenFlag = "--frozen";
    private const string TraceFlag = "--trace";
    private const string TraceContentFlag = "--trace-content";

    /// <summary>The options that take no value.</summary>
    private static readonly string[] RunFlags = [StudyFlag, FrozenFlag, TraceFlag, TraceContentFlag];

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
            case ["scenarios", "freeze", var dir]:
                return await FreezeScenariosAsync(dir, output, error).ConfigureAwait(false);
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

    private static async Task<int> FreezeScenariosAsync(string dir, TextWriter output, TextWriter error)
    {
        try
        {
            var path = Freeze.Write(dir);
            var count = ScenarioLoader.Files(dir).Length;
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Froze {count} scenarios, the instructions, the settings and the tools in {path} (short hash {Freeze.ShortHash(dir)}).")).ConfigureAwait(false);
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

        // Before anything is read or started, as a missing Foundry setting is: a traced run that could not send its trace does not begin.
        string? traceTo = null;
        var traceContent = options.ContainsKey(TraceContentFlag);
        if (TraceSettings(options, environment, ref traceTo) is { } traceProblem)
        {
            await error.WriteLineAsync(traceProblem).ConfigureAwait(false);
            return BadCommandLine;
        }

        var study = options.ContainsKey(StudyFlag);
        if (study)
        {
            // Exactly the pre-registered passes: a study of 1 or 5 passes would not be the one registered.
            if (options.ContainsKey("--passes") && passes != DecisionRule.PreRegistered.Passes)
            {
                await error.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"--study runs exactly {DecisionRule.PreRegistered.Passes} passes; leave out --passes, or use --frozen for any number.")).ConfigureAwait(false);
                return BadCommandLine;
            }

            passes = DecisionRule.PreRegistered.Passes;
        }

        // First of all, before the engine's settings are read, an agent is made or anything is started: a
        // run whose freeze does not hold never gets as far as a model.
        var frozen = study || options.ContainsKey(FrozenFlag);
        if (frozen && Freeze.Verify(scenarioDir) is { } broken)
        {
            await error.WriteLineAsync($"{broken} No run starts.").ConfigureAwait(false);
            return Failed;
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

        // Before any Foundry client is made: the SDK reads its tracing switches once. Disposed last, sending what is left.
        IDisposable? tracing = null;
        if (traceTo is not null)
        {
            try
            {
                tracing = StartTrace(traceTo, traceContent);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                // A malformed connection string; the exporter's message quotes the part it could not read, so it is redacted.
                await error.WriteLineAsync($"{TraceExport.ConnectionStringVariable} cannot be used, so no run starts: {Redaction.Describe(e, fullName: false)}").ConfigureAwait(false);
                return BadCommandLine;
            }
        }

        using var traced = tracing;
        if (tracing is not null)
        {
            await output.WriteLineAsync(traceContent
                ? "Traces go to Application Insights, with the messages, tool arguments and tool results: for development only."
                : "Traces go to Application Insights, without message content.").ConfigureAwait(false);
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
        if (study)
        {
            // The directory names the freeze its transcripts were run under.
            outDir = $"{outDir}-{Freeze.ShortHash(scenarioDir)}";
        }

        var runner = new ScenarioRunner(appExe, engineFor, ScenarioRunner.GateFor) { FrozenScenarios = frozen ? scenarioDir : null };
        await output.WriteLineAsync($"Transcripts go to {Path.GetFullPath(outDir)}").ConfigureAwait(false);

        int succeeded = 0, infraErrors = 0, runs = 0;
        try
        {
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
        }
        catch (FreezeBrokenException e)
        {
            // Something changed after the run began: the runs so far stand, and none starts after it.
            await error.WriteLineAsync($"{e.Message} The study stops here, after {runs} runs.").ConfigureAwait(false);
            return Failed;
        }

        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{succeeded} of {runs} runs succeeded; {infraErrors} infrastructure errors.")).ConfigureAwait(false);
        // A task failure is a result, not an error; a run that never got to try is.
        return infraErrors == 0 ? Done : Failed;
    }

    /// <summary>
    /// What is wrong with the trace options, or null; <paramref name="connectionString"/> is set when
    /// <c>--trace</c> is given and its connection string is.
    /// </summary>
    private static string? TraceSettings(Dictionary<string, string> options, Func<string, string?> environment, ref string? connectionString)
    {
        var trace = options.ContainsKey(TraceFlag);
        if (options.ContainsKey(TraceContentFlag))
        {
            if (!trace)
            {
                return $"{TraceContentFlag} needs {TraceFlag}.";
            }

            // The messages may name customers and devices: a study's trace holds none.
            if (options.ContainsKey(StudyFlag) || options.ContainsKey(FrozenFlag))
            {
                return $"{TraceContentFlag} is for development only: it cannot be used with {StudyFlag} or {FrozenFlag}.";
            }
        }

        if (!trace)
        {
            return null;
        }

        connectionString = environment(TraceExport.ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = null;
            return $"{TraceExport.ConnectionStringVariable} is not set: {TraceFlag} sends the traces to the Application Insights it names. Set it, or leave out {TraceFlag}.";
        }

        return null;
    }

    /// <summary>
    /// The trace export, signed in with the Azure CLI. Its own credential: the exporter's pipeline keeps
    /// its Azure Monitor token, and a shared cache holding one scope would swap it with the engines' each call.
    /// </summary>
    private static IDisposable StartTrace(string connectionString, bool captureContent) =>
        TraceExport.Start(connectionString, new AzureCliCredential(), captureContent);

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
            if (RunFlags.Contains(args[i], StringComparer.Ordinal))
            {
                if (!options.TryAdd(args[i], string.Empty))
                {
                    return null;
                }

                i--; // a flag has no value: the next argument is an option
                continue;
            }

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
