using System.ClientModel;
using System.Globalization;
using System.Text;
using Azure.Identity;
using Workshop.Agent.Engines;
using Workshop.Agent.Engines.Claude;

namespace Portfolio.Analyst;

/// <summary>
/// The capstone's Foundry route:
/// <code>
/// portfolio-analyst run --freeze &lt;path&gt; --questions &lt;path&gt; --passes &lt;1-3&gt; --out &lt;dir&gt; --model &lt;name&gt;
/// portfolio-analyst verify --freeze &lt;path&gt; --questions &lt;path&gt; --model &lt;name&gt;
/// </code>
/// Both check the freeze first (<see cref="AnalystFreeze.Verify"/>) and refuse on drift. <c>run</c>
/// then reads <c>FWA_PROJECT_ENDPOINT</c>, <c>ANALYST_MCP_URL</c> and <c>ANALYST_CONNECTION_ID</c>
/// from the environment, never printing them, provisions the <c>portfolio-analyst</c> prompt agent
/// and asks every frozen question once per pass. Records go to
/// <c>&lt;out&gt;/foundry-&lt;short&gt;-&lt;UTC start&gt;.jsonl.partial</c>, renamed to <c>.jsonl</c>
/// when every pass is done, so an interrupted run never looks complete. Sign in first with
/// <c>az login</c>: the project's token comes from the Azure CLI.
/// </summary>
/// <remarks>
/// Exit codes: 0 done; 1 the freeze is broken; 2 a usage or input error; 3 Foundry or the agent
/// failed. No message holds an endpoint, a URL or an ID: a failure is described by its HTTP status
/// and its type or code only.
/// </remarks>
public static class Program
{
    public const string McpUrlVariable = "ANALYST_MCP_URL";
    public const string ConnectionIdVariable = "ANALYST_CONNECTION_ID";
    public const string ProjectEndpointVariable = "FWA_PROJECT_ENDPOINT";

    private const int Done = 0;
    private const int FreezeBroken = 1;
    private const int BadInput = 2;
    private const int AgentFailed = 3;

    private const string Usage =
        "Usage:\n" +
        "  portfolio-analyst run --freeze <path> --questions <path> --passes <1-3> --out <dir> --model <name>\n" +
        "  portfolio-analyst verify --freeze <path> --questions <path> --model <name>\n" +
        "run reads FWA_PROJECT_ENDPOINT, ANALYST_MCP_URL and ANALYST_CONNECTION_ID from the environment; --passes defaults to 3.";

    private static readonly string[] RunOptions = ["--freeze", "--questions", "--passes", "--out", "--model"];
    private static readonly string[] VerifyOptions = ["--freeze", "--questions", "--model"];

    public static async Task<int> Main(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (!cancel.IsCancellationRequested)
            {
                e.Cancel = true;
                cancel.Cancel();
            }
        };

        return await MainAsync(args, Console.Out, Console.Error, Environment.GetEnvironmentVariable, LiveEngineAsync, cancel.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="RunAsync(string[], TextWriter, TextWriter, Func{string, string?}, Func{AnalystSetup, CancellationToken, Task{IAnalystEngine}}, CancellationToken)"/>
    /// with the last guard: whatever escapes it (a failure while cancelling, which the run's own
    /// handler lets through) is described by <see cref="Describe"/> only, never by its message or
    /// stack trace, which may name an endpoint, a URL or an ID.
    /// </summary>
    internal static async Task<int> MainAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<string, string?> environment,
        Func<AnalystSetup, CancellationToken, Task<IAnalystEngine>> engines,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            return await RunAsync(args, output, error, environment, engines, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return AgentFailed;
        }
        catch (Exception e)
        {
            await error.WriteLineAsync($"the Foundry agent failed: {Describe(e)}").ConfigureAwait(false);
            return AgentFailed;
        }
    }

    /// <summary>
    /// The command line, with the environment and the engine supplied: tests pass a scripted engine.
    /// <paramref name="engines"/> is called only after the freeze verified and the settings were read.
    /// </summary>
    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<string, string?> environment,
        Func<AnalystSetup, CancellationToken, Task<IAnalystEngine>> engines,
        CancellationToken ct)
    {
        var (command, options) = args switch
        {
            ["run", .. var rest] => ("run", ReadOptions(rest, RunOptions)),
            ["verify", .. var rest] => ("verify", ReadOptions(rest, VerifyOptions)),
            _ => ("", null),
        };
        var required = command == "run" ? RunOptions.Where(o => o != "--passes") : VerifyOptions;
        var passes = 3;
        if (options is null || required.Any(o => !options.ContainsKey(o))
            || (options.TryGetValue("--passes", out var text) && !(int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out passes) && passes is >= 1 and <= 3)))
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return BadInput;
        }

        try
        {
            var (shortHash, questions, instructions) = Verify(options);
            if (command == "verify")
            {
                await output.WriteLineAsync($"freeze intact: {shortHash}").ConfigureAwait(false);
                return Done;
            }

            var setup = Setup(environment, options["--model"], instructions);
            return await RunAsync(setup, shortHash, AnalystRun.ReadQuestions(questions), passes, options["--out"], output, error, engines, ct).ConfigureAwait(false);
        }
        catch (FreezeBrokenException e)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return FreezeBroken;
        }
        catch (Exception e) when (e is FreezeFormatException or InvalidDataException)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return BadInput;
        }
    }

    private static async Task<int> RunAsync(
        AnalystSetup setup,
        string shortHash,
        IReadOnlyList<AnalystQuestion> questions,
        int passes,
        string outDir,
        TextWriter output,
        TextWriter error,
        Func<AnalystSetup, CancellationToken, Task<IAnalystEngine>> engines,
        CancellationToken ct)
    {
        var final = Path.Combine(outDir, string.Create(CultureInfo.InvariantCulture, $"foundry-{shortHash}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.jsonl"));
        var partial = final + ".partial";
        if (File.Exists(final) || File.Exists(partial))
        {
            await error.WriteLineAsync($"--out: {Path.GetFileName(final)} already exists").ConfigureAwait(false);
            return BadInput;
        }

        try
        {
            Directory.CreateDirectory(outDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync("--out: the folder cannot be created").ConfigureAwait(false);
            return BadInput;
        }

        try
        {
            var engine = await engines(setup, ct).ConfigureAwait(false);
            await using (var file = new StreamWriter(partial, append: false, new UTF8Encoding(false)))
            {
                // The settings are taken out of every record as literals: a connection ID has no shape the patterns know.
                var writer = new RecordWriter(file, shortHash, [
                    (setup.McpUrl.OriginalString, AnswerRedaction.Url),
                    (setup.McpUrl.AbsoluteUri, AnswerRedaction.Url),
                    (setup.ProjectEndpoint.OriginalString, AnswerRedaction.Url),
                    (setup.ProjectEndpoint.AbsoluteUri, AnswerRedaction.Url),
                    (setup.ConnectionId, AnswerRedaction.Id),
                ]);
                await AnalystRun.AskAllAsync(engine, questions, passes, writer, output, TimeProvider.System, ct).ConfigureAwait(false);
            }

            File.Move(partial, final);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"wrote {questions.Count * passes} records to {Path.GetFileName(final)}")).ConfigureAwait(false);
            return Done;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            await error.WriteLineAsync($"the Foundry agent failed: {Describe(e)}").ConfigureAwait(false);
            return AgentFailed;
        }
    }

    /// <summary>The freeze verified against the questions' and the instructions' bytes and the model; InvalidDataException (fixed text) for a missing file.</summary>
    private static (string Short, byte[] Questions, byte[] Instructions) Verify(Dictionary<string, string> options)
    {
        var freezeBytes = ReadFile("--freeze", options["--freeze"]);
        var questions = ReadFile("--questions", options["--questions"]);
        var instructions = ReadFile("instructions.md", Path.Combine(AppContext.BaseDirectory, "instructions.md"));
        string freeze;
        try
        {
            freeze = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(freezeBytes);
        }
        catch (DecoderFallbackException)
        {
            throw new FreezeFormatException("--freeze: not UTF-8 text");
        }

        return (AnalystFreeze.Verify(freeze, questions, instructions, options["--model"]), questions, instructions);
    }

    private static byte[] ReadFile(string name, string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"{name}: file not found");
        }
    }

    /// <summary>The live run's settings from the environment; InvalidDataException naming the variable, never its value.</summary>
    private static AnalystSetup Setup(Func<string, string?> environment, string model, byte[] instructions)
    {
        string Required(string variable) =>
            environment(variable) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new InvalidDataException($"{variable} is not set");

        Uri Https(string variable)
        {
            var value = Required(variable);
            return !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
                && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host)
                ? uri
                : throw new InvalidDataException($"{variable} is not an https URL with a host");
        }

        var project = Https(ProjectEndpointVariable);
        var mcp = Https(McpUrlVariable);
        var connection = Required(ConnectionIdVariable);
        // The frozen bytes, LF-normalised, are what the agent is told; a BOM is not part of the text.
        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(FreezeHash.Lf(instructions)).TrimStart('﻿');
        return new AnalystSetup(project, model, mcp, connection, text);
    }

    /// <summary>The live engine: the prompt agent provisioned, keyless, through the Azure CLI's sign-in, asked once an hour.</summary>
    private static Task<IAnalystEngine> LiveEngineAsync(AnalystSetup setup, CancellationToken ct) =>
        AnalystRun.StartAsync(setup, new CachingTokenCredential(new AzureCliCredential()), transport: null, ct);

    /// <summary>A failure as its HTTP status and its type or code: never its message, which may name an endpoint, a URL or an ID.</summary>
    internal static string Describe(Exception e) => e switch
    {
        AnalystEngineException a => $"code {a.Code}",
        AnalystDriftException d => d.Message,
        ThrottledException => "HTTP 429, throttled",
        ModelRequestException m => string.Create(CultureInfo.InvariantCulture, $"HTTP {m.Status}, code {AnswerRedaction.Redact(m.Code ?? "none")}"),
        ClientResultException { Status: > 0 } c => string.Create(CultureInfo.InvariantCulture, $"HTTP {c.Status}, {nameof(ClientResultException)}"),
        HttpRequestException { StatusCode: { } status } h => string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)status}, {h.GetType().Name}"),
        _ => e.GetType().Name,
    };

    /// <summary>The options as name and value, or null when one is unknown, repeated or has no value (a value never starts with <c>--</c>).</summary>
    private static Dictionary<string, string>? ReadOptions(string[] args, string[] known)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!known.Contains(args[i], StringComparer.Ordinal)
                || i + 1 >= args.Length
                || args[i + 1].StartsWith("--", StringComparison.Ordinal)
                || !options.TryAdd(args[i], args[i + 1]))
            {
                return null;
            }
        }

        return options;
    }
}
