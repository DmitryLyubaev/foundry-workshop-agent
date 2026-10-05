using Microsoft.Extensions.AI;
using Workshop.Agent.Engines;
using Workshop.Agent.Engines.Foundry;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests.RealEngines;

/// <summary>
/// The GPT engine: the prompt agent's version over the project's Responses API, through the SDK's
/// own HTTP pipeline against a fake service, with its errors classified (Review Focus 1).
/// </summary>
public sealed class GptEngineTests
{
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyList<AIFunction> Tools =
    [
        AIFunctionFactory.Create(() => """{"outcome":"ok","screen":"jobs"}""", new AIFunctionFactoryOptions { Name = "describe_screen", Description = "Describes the screen." }),
        AIFunctionFactory.Create((string screen) => """{"outcome":"ok"}""", new AIFunctionFactoryOptions { Name = "open_screen", Description = "Opens a screen." }),
    ];

    private static readonly AgentVersionRef Agent = new("fwa-workshop-agent", "3", AgentInstructions.Sha256, ToolSchemas.Sha256(Tools));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_engine_runs_the_loop_through_its_agent_version()
    {
        var service = new FakeModelService(
            () => FakeModelService.Json(200, Recorded.GptCall("resp_1", "describe_screen")),
            () => FakeModelService.Json(200, Recorded.GptReply("resp_2", "There are 4 laptop batteries.")));
        var credential = new FakeCredential();
        var engine = Engine(service, credential);

        var result = await engine.RunAsync("How many laptop batteries do we have?", Tools, Cancel);

        Assert.Equal("gpt", engine.Name);
        Assert.Equal("gpt-5.6-luna", engine.Model);
        Assert.Equal("gpt-5.6-luna", engine.Deployment);
        Assert.Equal("3", engine.AgentVersion);
        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        Assert.Equal("There are 4 laptop batteries.", result.FinalReply);
        Assert.Equal([812L, 900L], result.Calls.Select(c => c.InputTokens));
        Assert.Equal([24L, 12L], result.Calls.Select(c => c.OutputTokens));

        Assert.Equal(2, service.Requests.Count);
        Assert.All(service.Requests, r =>
        {
            Assert.Equal("https://example.services.ai.azure.com/api/projects/example/openai/v1/responses", r.Uri.GetLeftPart(UriPartial.Path));
            Assert.Equal("Bearer fake-token", r.Authorization);
        });
        Assert.All(credential.Scopes, s => Assert.Equal("https://ai.azure.com/.default", s));

        // The first request names the agent version and carries the task and the settings, not the
        // instructions, the tools or a model: the version holds those.
        var first = service.Requests[0].Json;
        Assert.Equal("agent_reference", first.GetProperty("agent_reference").GetProperty("type").GetString());
        Assert.Equal("fwa-workshop-agent", first.GetProperty("agent_reference").GetProperty("name").GetString());
        Assert.Equal("3", first.GetProperty("agent_reference").GetProperty("version").GetString());
        Assert.Equal(AgentSettings.MaxOutputTokens, first.GetProperty("max_output_tokens").GetInt32());
        foreach (var absent in new[] { "instructions", "tools", "model", "temperature" })
        {
            Assert.False(first.TryGetProperty(absent, out _), $"The request carries '{absent}'.");
        }

        Assert.Equal("How many laptop batteries do we have?", first.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());

        // The second goes on from the first answer, with the tool's result for its call.
        var second = service.Requests[1].Json;
        Assert.Equal("resp_1", second.GetProperty("previous_response_id").GetString());
        var output = second.GetProperty("input").EnumerateArray().Single(i => i.GetProperty("type").GetString() == "function_call_output");
        Assert.Equal("call_resp_1", output.GetProperty("call_id").GetString());
        Assert.Contains("jobs", output.GetProperty("output").GetString(), StringComparison.Ordinal);
        Assert.Equal("3", second.GetProperty("agent_reference").GetProperty("version").GetString());
    }

    [Fact]
    public async Task A_truncated_run_keeps_its_partial_text()
    {
        var service = new FakeModelService(() => FakeModelService.Json(200, Recorded.GptCutOff("resp_1", "There are 4 laptop bat")));

        var result = await Engine(service, new FakeCredential()).RunAsync("Count them.", Tools, Cancel);

        Assert.Equal(EngineOutcome.Truncated, result.Outcome);
        Assert.Equal("There are 4 laptop bat", result.FinalReply);
        Assert.Equal(["length"], result.Calls.Select(c => c.FinishReason));
    }

    [Fact]
    public async Task A_filtered_answer_is_content_filtered()
    {
        var service = new FakeModelService(() => FakeModelService.Json(200, Recorded.GptFiltered("resp_1")));

        var result = await Engine(service, new FakeCredential()).RunAsync("Look.", Tools, Cancel);

        Assert.Equal(EngineOutcome.ContentFiltered, result.Outcome);
        Assert.Null(result.FinalReply);
    }

    [Theory]
    [InlineData("429 Retry-After", 7000)]
    [InlineData("429 retry-after-ms", 1500)]
    public async Task A_429_is_throttled_with_its_wait_and_no_retry(string kind, int waitMs)
    {
        var service = new FakeModelService(Answer(kind));

        var e = await Assert.ThrowsAsync<ThrottledException>(() => Model(service, new FakeCredential()).GetResponseAsync("Look.", Options(), Cancel));

        Assert.Equal(TimeSpan.FromMilliseconds(waitMs), e.RetryAfter);
        Assert.Single(service.Requests);
    }

    [Theory]
    [InlineData("401")]
    [InlineData("403")]
    [InlineData("500")]
    [InlineData("503")]
    [InlineData("408")]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task Service_failures_are_infra_and_not_retried(string kind)
    {
        var service = new FakeModelService(Answer(kind));

        var e = await Assert.ThrowsAnyAsync<Exception>(() => Model(service, new FakeCredential()).GetResponseAsync("Look.", Options(), Cancel));

        Assert.True(ServiceFailure.Is(e), $"{e.GetType().FullName}: {e.Message}");
        Assert.Single(service.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_identity_failure_is_infra(bool unavailable)
    {
        var service = new FakeModelService(Answer("200"));

        var e = await Assert.ThrowsAnyAsync<Exception>(() => Model(service, new FailingCredential(unavailable)).GetResponseAsync("Look.", Options(), Cancel));

        Assert.True(ServiceFailure.Is(e), $"{e.GetType().FullName}: {e.Message}");
        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task A_content_filter_400_is_content_filtered()
    {
        var service = new FakeModelService(Answer("content filter"));

        var e = await Assert.ThrowsAsync<ContentFilteredException>(() => Model(service, new FakeCredential()).GetResponseAsync("Look.", Options(), Cancel));

        Assert.Equal("content_filter", e.Code);
    }

    [Theory]
    [InlineData("400", 400, "invalid_value")]
    [InlineData("404", 404, "DeploymentNotFound")]
    public async Task Other_4xx_are_the_models_failure_with_their_code(string kind, int status, string code)
    {
        var service = new FakeModelService(Answer(kind));

        var e = await Assert.ThrowsAsync<ModelRequestException>(() => Model(service, new FakeCredential()).GetResponseAsync("Look.", Options(), Cancel));

        Assert.Equal(status, e.Status);
        Assert.Equal(code, e.Code);
        Assert.False(ServiceFailure.Is(e));
    }

    [Theory]
    [InlineData("503", "service_error", "ServiceUnavailable")]
    [InlineData("429 too long", "throttled", null)]
    [InlineData("content filter", "content_filtered", null)]
    [InlineData("400", "engine_error", "invalid_value")]
    [InlineData("404", "engine_error", "DeploymentNotFound")]
    public async Task Each_error_ends_the_run_with_its_outcome(string kind, string outcome, string? inError)
    {
        var service = new FakeModelService(Answer(kind));

        var result = await Engine(service, new FakeCredential()).RunAsync("Look.", Tools, Cancel);

        Assert.Equal(outcome, result.Outcome);
        Assert.Single(service.Requests);
        if (inError is not null)
        {
            Assert.Contains(inError, result.Error, StringComparison.Ordinal);
        }
    }

    private static ChatClientEngine Engine(FakeModelService service, Azure.Core.TokenCredential credential) =>
        GptEngineFactory.Create(FakeAgentService.Options, Agent, new ToolBudget(), FiveMinutes, null, credential, service.Transport());

    private static IChatClient Model(FakeModelService service, Azure.Core.TokenCredential credential) =>
        GptEngineFactory.CreateModel(FakeAgentService.Options, Agent, credential, service.Transport());

    private static ChatOptions Options() => new()
    {
        Instructions = AgentInstructions.Text,
        Tools = [.. Tools],
        MaxOutputTokens = AgentSettings.MaxOutputTokens,
    };

    /// <summary>What the Responses API answers, for each case.</summary>
    private static Func<HttpResponseMessage> Answer(string kind) => kind switch
    {
        "200" => () => FakeModelService.Json(200, Recorded.GptReply("resp_1", "Done.")),
        "429 Retry-After" => () => FakeModelService.Json(429, Recorded.AzureError("429", "Rate limit is exceeded."), ("Retry-After", "7")),
        "429 retry-after-ms" => () => FakeModelService.Json(429, Recorded.AzureError("429", "Rate limit is exceeded."), ("retry-after-ms", "1500"), ("Retry-After", "2")),
        // Longer than the engine's whole 60 s budget for throttling waits: the run ends throttled at once.
        "429 too long" => () => FakeModelService.Json(429, Recorded.AzureError("429", "Rate limit is exceeded."), ("Retry-After", "61")),
        "401" => () => FakeModelService.Json(401, Recorded.AzureError("PermissionDenied", "The principal lacks the data action.")),
        "403" => () => FakeModelService.Json(403, Recorded.AzureError("Forbidden", "Public access is disabled.")),
        "500" => () => FakeModelService.Json(500, Recorded.AzureError("InternalServerError", "The server had an error.")),
        "503" => () => FakeModelService.Json(503, Recorded.AzureError("ServiceUnavailable", "The service is busy.")),
        "408" => () => FakeModelService.Json(408, Recorded.AzureError("Timeout", "The request timed out.")),
        "network" => () => throw new HttpRequestException("No such host is known."),
        "timeout" => () => throw new TaskCanceledException("The request was canceled due to the configured timeout.", new TimeoutException("The operation timed out.")),
        "content filter" => () => FakeModelService.Json(400, Recorded.AzureContentFilter),
        "400" => () => FakeModelService.Json(400, Recorded.AzureInvalidRequest),
        "404" => () => FakeModelService.Json(404, Recorded.AzureError("DeploymentNotFound", "The API deployment for this resource does not exist.")),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
