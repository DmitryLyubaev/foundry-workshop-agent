using Microsoft.Extensions.AI;
using Workshop.Agent.Engines;
using Workshop.Agent.Engines.Claude;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests.RealEngines;

/// <summary>
/// The Claude engine: Claude Haiku 4.5 deployed in Foundry, through the Anthropic SDK's own HTTP
/// stack against a fake service, keyless, with its errors classified (Review Focus 1).
/// </summary>
public sealed class ClaudeEngineTests
{
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyList<AIFunction> Tools =
    [
        AIFunctionFactory.Create(() => """{"outcome":"ok","screen":"jobs"}""", new AIFunctionFactoryOptions { Name = "describe_screen", Description = "Describes the screen." }),
        AIFunctionFactory.Create((string screen) => """{"outcome":"ok"}""", new AIFunctionFactoryOptions { Name = "open_screen", Description = "Opens a screen." }),
    ];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_engine_runs_the_loop_with_instructions_tools_and_settings_on_each_request()
    {
        var service = new FakeModelService(
            () => FakeModelService.Json(200, Recorded.ClaudeCall("msg_1", "describe_screen")),
            () => FakeModelService.Json(200, Recorded.ClaudeReply("msg_2", "There are 4 laptop batteries.")));
        var credential = new FakeCredential();
        var engine = Engine(service, credential);

        var result = await engine.RunAsync("How many laptop batteries do we have?", Tools, Cancel);

        Assert.Equal("claude", engine.Name);
        Assert.Equal("claude-haiku-4-5", engine.Model);
        Assert.Equal("claude-haiku-4-5", engine.Deployment);
        Assert.Null(engine.AgentVersion);
        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        Assert.Equal("There are 4 laptop batteries.", result.FinalReply);
        Assert.Equal([950L, 990L], result.Calls.Select(c => c.InputTokens));
        Assert.Equal(["tool_calls", "stop"], result.Calls.Select(c => c.FinishReason));

        Assert.Equal(2, service.Requests.Count);
        Assert.All(service.Requests, r =>
        {
            Assert.Equal("https://example.services.ai.azure.com/anthropic/v1/messages", r.Uri.ToString());
            Assert.Equal("bearer fake-token", r.Authorization);
            var body = r.Json;
            Assert.Equal("claude-haiku-4-5", body.GetProperty("model").GetString());
            Assert.Equal(AgentSettings.MaxOutputTokens, body.GetProperty("max_tokens").GetInt32());
            Assert.False(body.TryGetProperty("temperature", out _));
            Assert.Equal(AgentInstructions.Text, Assert.Single(body.GetProperty("system").EnumerateArray()).GetProperty("text").GetString());
            Assert.Equal(["describe_screen", "open_screen"], body.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        });

        // Keyless: an Entra token for the Foundry scope, fetched once and reused, never a key.
        Assert.Equal(["https://ai.azure.com/.default"], credential.Scopes);
        Assert.All(service.Requests, r => Assert.DoesNotContain("x-api-key", r.Body, StringComparison.OrdinalIgnoreCase));

        // The second request carries the whole conversation, with the tool's result for its call.
        var messages = service.Requests[1].Json.GetProperty("messages").EnumerateArray().ToArray();
        var toolResult = messages[^1].GetProperty("content").EnumerateArray().Single();
        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("toolu_msg_1", toolResult.GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public async Task A_truncated_run_keeps_its_partial_text()
    {
        var service = new FakeModelService(() => FakeModelService.Json(200, Recorded.ClaudeCutOff("msg_1", "There are 4 laptop bat")));

        var result = await Engine(service, new FakeCredential()).RunAsync("Count them.", Tools, Cancel);

        Assert.Equal(EngineOutcome.Truncated, result.Outcome);
        Assert.Equal("There are 4 laptop bat", result.FinalReply);
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
    [InlineData("529")]
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
    }

    [Theory]
    [InlineData("content policy")]
    [InlineData("azure content filter")]
    public async Task A_content_policy_400_is_content_filtered(string kind)
    {
        var service = new FakeModelService(Answer(kind));

        await Assert.ThrowsAsync<ContentFilteredException>(() => Model(service, new FakeCredential()).GetResponseAsync("Look.", Options(), Cancel));
    }

    [Theory]
    [InlineData("400", 400, "invalid_request_error")]
    [InlineData("404", 404, "not_found_error")]
    public async Task Other_4xx_are_the_models_failure_with_their_code(string kind, int status, string code)
    {
        var service = new FakeModelService(Answer(kind));

        var e = await Assert.ThrowsAsync<ModelRequestException>(() => Model(service, new FakeCredential()).GetResponseAsync("Look.", Options(), Cancel));

        Assert.Equal(status, e.Status);
        Assert.Equal(code, e.Code);
        Assert.False(ServiceFailure.Is(e));
    }

    [Theory]
    [InlineData("529", "service_error", "overloaded_error")]
    [InlineData("429 too long", "throttled", null)]
    [InlineData("content policy", "content_filtered", null)]
    [InlineData("400", "engine_error", "invalid_request_error")]
    [InlineData("404", "engine_error", "not_found_error")]
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
        ClaudeEngineFactory.Create(FakeAgentService.Options, new ToolBudget(), FiveMinutes, null, credential, service.Client());

    private static IChatClient Model(FakeModelService service, Azure.Core.TokenCredential credential) =>
        ClaudeEngineFactory.CreateModel(FakeAgentService.Options, credential, service.Client());

    private static ChatOptions Options() => new()
    {
        Instructions = AgentInstructions.Text,
        Tools = [.. Tools],
        MaxOutputTokens = AgentSettings.MaxOutputTokens,
    };

    /// <summary>What the Messages API on Foundry answers, for each case.</summary>
    private static Func<HttpResponseMessage> Answer(string kind) => kind switch
    {
        "200" => () => FakeModelService.Json(200, Recorded.ClaudeReply("msg_1", "Done.")),
        "429 Retry-After" => () => FakeModelService.Json(429, Recorded.AnthropicError("rate_limit_error", "Number of requests has exceeded your rate limit."), ("Retry-After", "7")),
        "429 retry-after-ms" => () => FakeModelService.Json(429, Recorded.AnthropicError("rate_limit_error", "Number of requests has exceeded your rate limit."), ("retry-after-ms", "1500"), ("Retry-After", "2")),
        "429 too long" => () => FakeModelService.Json(429, Recorded.AnthropicError("rate_limit_error", "Number of requests has exceeded your rate limit."), ("Retry-After", "61")),
        "401" => () => FakeModelService.Json(401, Recorded.AnthropicError("authentication_error", "The token is not valid.")),
        "403" => () => FakeModelService.Json(403, Recorded.AnthropicError("permission_error", "The principal lacks the data action.")),
        "500" => () => FakeModelService.Json(500, Recorded.AnthropicError("api_error", "Internal server error.")),
        "529" => () => FakeModelService.Json(529, Recorded.AnthropicError("overloaded_error", "Overloaded.")),
        "network" => () => throw new HttpRequestException("No such host is known."),
        "timeout" => () => throw new TaskCanceledException("The request was canceled due to the configured timeout.", new TimeoutException("The operation timed out.")),
        "content policy" => () => FakeModelService.Json(400, Recorded.AnthropicError("invalid_request_error", "Output blocked by content filtering policy.")),
        "azure content filter" => () => FakeModelService.Json(400, Recorded.AzureContentFilter),
        "400" => () => FakeModelService.Json(400, Recorded.AnthropicError("invalid_request_error", "messages: at least one message is required.")),
        "404" => () => FakeModelService.Json(404, Recorded.AnthropicError("not_found_error", "model: claude-haiku-4-5")),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
