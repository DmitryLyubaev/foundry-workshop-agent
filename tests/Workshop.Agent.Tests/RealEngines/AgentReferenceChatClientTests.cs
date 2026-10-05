#pragma warning disable OPENAI001 // CreateResponseOptions is the Responses API's request; the SDK marks it for evaluation.

using Azure.AI.Extensions.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Workshop.Agent.Engines;
using Workshop.Agent.Engines.Foundry;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests.RealEngines;

/// <summary>
/// Under the function loop, the GPT engine's requests name the agent version and leave out the
/// instructions and the tools, which the version holds; the loop above still sees the tools.
/// </summary>
public sealed class AgentReferenceChatClientTests
{
    private static readonly AgentVersionRef Agent = new("fwa-workshop-agent", "3", AgentInstructions.Sha256, ToolSchemas.Sha256(WorkshopTools.Declarations));

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Instructions_and_tools_are_stripped_and_the_agent_named()
    {
        var inner = new CapturingClient();
        using var client = new AgentReferenceChatClient(inner, Agent);
        var options = new ChatOptions
        {
            Instructions = AgentInstructions.Text,
            Tools = [.. WorkshopTools.Declarations],
            MaxOutputTokens = AgentSettings.MaxOutputTokens,
            Temperature = AgentSettings.Temperature,
            ConversationId = "resp_1",
        };

        await client.GetResponseAsync("Look.", options, Cancel);

        var sent = Assert.Single(inner.Options);
        Assert.Null(sent.Instructions);
        Assert.Null(sent.Tools);
        Assert.Equal(AgentSettings.MaxOutputTokens, sent.MaxOutputTokens);
        Assert.Null(sent.Temperature);
        Assert.Equal("resp_1", sent.ConversationId);
        var request = Assert.IsType<CreateResponseOptions>(sent.RawRepresentationFactory!(inner));
        Assert.Equal("fwa-workshop-agent", request.Agent.Name);
        Assert.Equal("3", request.Agent.Version);

        // The loop's own options are untouched: it keeps the tools it runs.
        Assert.Equal(AgentInstructions.Text, options.Instructions);
        Assert.Equal(6, options.Tools.Count);
    }

    [Fact]
    public async Task Tools_other_than_the_versions_are_refused()
    {
        using var client = new AgentReferenceChatClient(new CapturingClient(), Agent);
        var options = new ChatOptions { Instructions = AgentInstructions.Text, Tools = [.. WorkshopTools.Declarations.Take(5)] };

        var e = await Assert.ThrowsAsync<AgentDriftException>(() => client.GetResponseAsync("Look.", options, Cancel));

        Assert.Equal("The loop's tools are not the ones prompt agent 'fwa-workshop-agent' version 3 holds.", e.Message);
    }

    [Fact]
    public async Task Instructions_other_than_the_versions_are_refused()
    {
        using var client = new AgentReferenceChatClient(new CapturingClient(), Agent);
        var options = new ChatOptions { Instructions = "Be quick.", Tools = [.. WorkshopTools.Declarations] };

        var e = await Assert.ThrowsAsync<AgentDriftException>(() => client.GetResponseAsync("Look.", options, Cancel));

        Assert.Equal("The loop's instructions are not the ones prompt agent 'fwa-workshop-agent' version 3 holds.", e.Message);
    }

    [Theory]
    [InlineData("instructions")]
    [InlineData("tools")]
    [InlineData("options")]
    public async Task A_request_without_the_instructions_or_the_tools_is_refused(string missing)
    {
        using var client = new AgentReferenceChatClient(new CapturingClient(), Agent);
        var options = missing switch
        {
            "instructions" => new ChatOptions { Tools = [.. WorkshopTools.Declarations] },
            "tools" => new ChatOptions { Instructions = AgentInstructions.Text },
            _ => null,
        };

        var e = await Assert.ThrowsAsync<AgentDriftException>(() => client.GetResponseAsync("Look.", options, Cancel));

        Assert.Contains(missing == "tools" ? "tools" : "instructions", e.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingClient : IChatClient
    {
        public List<ChatOptions> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options.Add(options!);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
