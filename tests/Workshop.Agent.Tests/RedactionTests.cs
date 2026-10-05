using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Workshop.Agent.Engines;
using Workshop.Agent.Engines.Claude;
using Workshop.Agent.Engines.Foundry;
using Workshop.Agent.Runner;
using Workshop.Agent.Telemetry;
using Workshop.Agent.Tests.RealEngines;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests;

/// <summary>
/// No identifier reaches a transcript, the console or a trace (Review Focus 4): the services' error
/// bodies name principals, subscriptions, projects and hosts, and an HTTP client's message names the
/// host it could not reach. Each is replaced before it is kept.
/// </summary>
public sealed class RedactionTests
{
    // Made-up identifiers, distinct enough that finding one anywhere means it leaked.
    private const string Principal = "11111111-2222-3333-4444-555555555555";
    private const string Project = "example-leak-project";
    private const string Host = "example-leak-resource.services.ai.azure.com";

    private static readonly string[] Identifiers = [Principal, Project, Host, "example-leak-resource"];

    private static readonly string ForbiddenBody = Recorded.AzureError(
        "PermissionDenied",
        $"The principal `{Principal}` lacks the required data action `Microsoft.CognitiveServices/accounts/AIServices/agents/write` to perform `POST /api/projects/{Project}/agents/fwa-workshop-agent/versions` operation.");

    private static readonly string AnthropicForbiddenBody = Recorded.AnthropicError(
        "permission_error",
        $"Principal {Principal} does not have access to /subscriptions/{Principal}/resourceGroups/rg-x/providers/Microsoft.CognitiveServices/accounts/example-leak-resource/projects/{Project}.");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Redact_replaces_every_kind_of_identifier_and_keeps_the_rest()
    {
        var text = $"HTTP 403 (PermissionDenied): principal {Principal} at https://{Host}/api/projects/{Project}/openai/v1/responses, "
            + $"host {Host}:443, /subscriptions/{Principal}/resourceGroups/rg-x, owner someone@example.com, "
            + "header Bearer abc.DEF-123_x, token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln, and /api/projects/" + Project + " too; "
            + "login.microsoftonline.com and api.anthropic.com and store.blob.core.windows.net";

        var redacted = Redaction.Redact(text)!;

        Assert.Equal(
            "HTTP 403 (PermissionDenied): principal <guid> at <url>, "
            + "host <host>, /subscriptions/<redacted>, owner <email>, "
            + "header Bearer <token>, token <token>, and /api/projects/<project> too; "
            + "<host> and <host> and <host>",
            redacted);
        Assert.Null(Redaction.Redact(null));
    }

    [Fact]
    public void Redact_replaces_local_paths_and_run_names()
    {
        var temp = Path.GetTempPath();
        var sha256 = new string('a', 64);
        var text = $"Workshop.App wrote nothing in '{Path.Combine(temp, "WorkshopAgentRuns", "0123456789abcdef0123456789abcdef", "session")}'; "
            + @"db C:\Users\example-user\AppData\Local\Temp\WorkshopAgentRuns\FEDCBA9876543210FEDCBA9876543210\workshop.db; "
            + @"exe C:/Users/example-user/source/Workshop.App.exe; home c:\users\example-user; "
            + $"request 00112233445566778899aabbccddeeff; settings {sha256}; guid {Principal}";

        var redacted = Redaction.Redact(text)!;

        Assert.Equal(
            @"Workshop.App wrote nothing in '<temp>\WorkshopAgentRuns\<run>\session'; "
            + @"db <temp>\WorkshopAgentRuns\<run>\workshop.db; "
            + "exe <home>/source/Workshop.App.exe; home <home>; "
            + $"request <hex>; settings {sha256}; guid <guid>",
            redacted);
    }

    [Fact]
    public void Redact_replaces_this_machines_temp_and_profile_directories_however_they_are_cased()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

        var redacted = Redaction.Redact($@"{temp.ToUpperInvariant()}\x and {profile.ToLowerInvariant()}\y")!;

        Assert.Equal(@"<temp>\x and <home>\y", redacted);
        Assert.DoesNotContain(Environment.UserName, redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_app_that_cannot_start_reaches_the_transcript_and_the_console_without_local_paths()
    {
        // The app is looked for in a temp directory with a 32-hex name, as a run's own directory is.
        using var output = TempRun.Create();
        var missingApp = Path.Combine(output.Directory, "Workshop.App.exe");
        var script = Script.Load(Path.Combine(RepoPaths.Scripts, "s05.correct.json"));
        var runner = new ScenarioRunner(missingApp, (_, run) => FakeEngine.Create(script, run), ScenarioRunner.GateFor);

        var transcript = await runner.RunAsync(ScenarioSet.Get("s05"), 1, output.Directory, Cancel);

        Assert.True(transcript.InfraError);
        Assert.StartsWith("AppStartException: Workshop.App could not be started from '<temp>", transcript.InfraMessage, StringComparison.Ordinal);
        var file = File.ReadAllText(Path.Combine(output.Directory, "s05.fake.p1.json"));
        foreach (var text in new[] { transcript.InfraMessage!, file, Program.Line(transcript) })
        {
            Assert.DoesNotContain(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFileName(output.Directory), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\Users\", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\\Users\\", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Transcripts_redact_reapplies_the_redaction_to_infra_message_and_error_only_and_logs_it()
    {
        using var study = TempRun.Create();
        var leaked = $"AppStartException: Workshop.App wrote no session file in '{Path.Combine(Path.GetTempPath(), "WorkshopAgentRuns", "0123456789abcdef0123456789abcdef", "session")}' within 20 seconds.";
        // A path in a tool's result is not the command's to touch: only infraMessage and error are.
        const string ToolResult = """{"outcome":"ok","message":"C:\\Users\\example-user"}""";
        var written = MakeTranscript(infraMessage: leaked, error: "System.InvalidOperationException: request 00112233445566778899aabbccddeeff failed.", toolResult: ToolResult);
        var path = written.Write(study.Directory);
        var cleanPath = MakeTranscript(infraMessage: null, error: null, toolResult: ToolResult, scenario: "s06").Write(study.Directory);
        var cleanBytes = File.ReadAllBytes(cleanPath);
        Directory.CreateDirectory(Path.Combine(study.Directory, "repeats"));
        var firstAttempt = Path.Combine(study.Directory, "repeats", "s05.fake.p1.first-attempt.json");
        File.Copy(path, firstAttempt);
        const string Scores = """{"status":"completed","rows":[]}""";
        File.WriteAllText(Path.Combine(study.Directory, "eval-scores.json"), Scores);

        var (code, output, error) = await Run("transcripts", "redact", study.Directory);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Contains("Re-redacted 2 of 3 transcripts", output, StringComparison.Ordinal);
        using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
        {
            var root = json.RootElement;
            Assert.Equal(@"AppStartException: Workshop.App wrote no session file in '<temp>\WorkshopAgentRuns\<run>\session' within 20 seconds.", root.GetProperty("infraMessage").GetString());
            Assert.Equal("System.InvalidOperationException: request <hex> failed.", root.GetProperty("error").GetString());
            // Nothing else moved: the same transcript, written the same way, but for the two fields.
            var expected = JsonSerializer.SerializeToUtf8Bytes(
                written with { InfraMessage = root.GetProperty("infraMessage").GetString(), Error = root.GetProperty("error").GetString() },
                Transcript.Json);
            Assert.Equal(expected, File.ReadAllBytes(path));
        }

        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(firstAttempt));
        Assert.Equal(cleanBytes, File.ReadAllBytes(cleanPath));
        Assert.Equal(Scores, File.ReadAllText(Path.Combine(study.Directory, "eval-scores.json")));

        // The log names each file and field, and none of the text it replaced.
        var log = File.ReadAllLines(Path.Combine(study.Directory, "redactions.md"));
        Assert.Equal(2, log.Length);
        Assert.Matches(@"^- \d{4}-\d{2}-\d{2}: repeats/s05\.fake\.p1\.first-attempt\.json: infraMessage, error re-redacted by Workshop\.Agent transcripts redact\.$", log[0]);
        Assert.Matches(@"^- \d{4}-\d{2}-\d{2}: s05\.fake\.p1\.json: infraMessage, error re-redacted by Workshop\.Agent transcripts redact\.$", log[1]);
        Assert.DoesNotContain(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), string.Join('\n', log), StringComparison.OrdinalIgnoreCase);

        // Run again: nothing left to do, and nothing more logged.
        (code, output, _) = await Run("transcripts", "redact", study.Directory);

        Assert.Equal(0, code);
        Assert.Contains("Nothing to re-redact in 3 transcripts", output, StringComparison.Ordinal);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(study.Directory, "redactions.md")).Length);
    }

    [Fact]
    public async Task Transcripts_redact_refuses_a_missing_directory()
    {
        using var study = TempRun.Create();
        var missing = Path.Combine(study.Directory, "none");

        var (code, _, error) = await Run("transcripts", "redact", missing);

        Assert.Equal(1, code);
        Assert.StartsWith("There is no directory '", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData("gpt", "403")]
    [InlineData("gpt", "dns")]
    [InlineData("claude", "403")]
    [InlineData("claude", "dns")]
    public async Task A_service_failure_reaches_the_transcript_the_console_and_the_trace_redacted(string engine, string failure)
    {
        var service = new FakeModelService(failure switch
        {
            "403" => () => FakeModelService.Json(403, engine == "gpt" ? ForbiddenBody : AnthropicForbiddenBody),
            _ => () => throw new HttpRequestException($"No such host is known. ({Host}:443)"),
        });
        var traced = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = traced.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var agent = new AgentVersionRef("fwa-workshop-agent", "3", AgentInstructions.Sha256, ToolSchemas.Sha256(WorkshopTools.Declarations));
        using var output = TempRun.Create();
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (_, run) => engine == "gpt"
                ? GptEngineFactory.Create(FakeAgentService.Options, agent, run.Budget, run.TimeLimit, run, new FakeCredential(), service.Transport())
                : ClaudeEngineFactory.Create(FakeAgentService.Options, run.Budget, run.TimeLimit, run, new FakeCredential(), service.Client()),
            ScenarioRunner.GateFor);

        var transcript = await runner.RunAsync(ScenarioSet.Get("s05"), 1, output.Directory, Cancel);

        // An infrastructure error, as before, and still saying what failed.
        Assert.Equal(EngineOutcome.ServiceError, transcript.Outcome);
        Assert.True(transcript.InfraError);
        if (failure == "403")
        {
            // The status and the principal's place in the text stay; the principal does not.
            Assert.Matches("403|Forbidden", transcript.Error);
            Assert.Contains(Redaction.Guid, transcript.Error, StringComparison.Ordinal);
        }

        var file = File.ReadAllText(Path.Combine(output.Directory, $"s05.{engine}.p1.json"));
        var line = Program.Line(transcript);
        var trace = string.Join('\n', traced.SelectMany(a => a.Events.SelectMany(e => e.Tags).Select(t => $"{t.Key}={t.Value}")
            .Append(a.StatusDescription ?? "")
            .Concat(a.TagObjects.Select(t => $"{t.Key}={t.Value}"))));
        foreach (var identifier in Identifiers)
        {
            Assert.DoesNotContain(identifier, transcript.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(identifier, transcript.InfraMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(identifier, file, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(identifier, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(identifier, trace, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("infrastructure error", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("403")]
    [InlineData("dns")]
    public async Task A_provisioning_failure_reaches_the_console_redacted(string failure)
    {
        var service = new FakeModelService(failure == "403"
            ? () => FakeModelService.Json(403, ForbiddenBody)
            : () => throw new HttpRequestException($"No such host is known. ({Host}:443)"));
        using var error = new StringWriter();

        var agent = await Program.EnsureAgentAsync(FakeAgentService.Options, new FakeCredential(), error, Cancel, service.Transport());

        Assert.Null(agent);
        var text = error.ToString();
        Assert.StartsWith("The prompt agent could not be checked, so no run starts: ", text, StringComparison.Ordinal);
        foreach (var identifier in Identifiers)
        {
            Assert.DoesNotContain(identifier, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Transcript MakeTranscript(string? infraMessage, string? error, string toolResult, string scenario = "s05") => new(
        scenario,
        1,
        "fake",
        "scripted",
        null,
        null,
        "Look up the job.",
        AgentInstructions.Sha256,
        AgentSettings.Sha256,
        infraMessage is null ? EngineOutcome.Completed : Transcript.InfraErrorOutcome,
        infraMessage is not null,
        infraMessage,
        [new ModelCall(1, 120, 30, 812.5, "stop")],
        [new ToolRecord(1, 1, "describe_screen", JsonDocument.Parse("{}").RootElement, "ok", null, null, 3.25, null, toolResult)],
        infraMessage is null ? "Done." : null,
        0,
        new Scenarios.CheckResult(true, []),
        infraMessage is null,
        1234.5,
        new DateTimeOffset(2026, 10, 5, 1, 2, 3, TimeSpan.Zero),
        error);

    private static async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Program.RunAsync(args, output, error, Cancel);
        return (code, output.ToString(), error.ToString());
    }
}
