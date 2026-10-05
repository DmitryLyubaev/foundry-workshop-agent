using System.Collections.Concurrent;
using System.Diagnostics;
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
}
