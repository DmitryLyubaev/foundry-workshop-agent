using System.Text.Json;
using Workshop.Agent.Engines;
using Workshop.Agent.Engines.Claude;
using Workshop.Agent.Engines.Foundry;
using Workshop.Agent.Tests.RealEngines;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests;

/// <summary>
/// Neutrality (spec §5, Review Focus 2): the study compares the models, so both engines get the
/// same instructions, the same six tools, schemas included, and the same settings. GPT's live in
/// its prompt agent's version and its request; Claude's travel with each request. Both are read
/// off the wire, as the SDKs sent them.
/// </summary>
public sealed class NeutralityTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Both_engines_get_identical_instructions_tools_and_settings()
    {
        var tools = WorkshopTools.Declarations;

        // GPT: the provisioner writes the agent version; the engine's first request names it.
        var agents = new FakeAgentService();
        var agentService = new FakeModelService(agents.Answer);
        var agent = await PromptAgentProvisioner.EnsureAsync(FakeAgentService.Options, tools, new FakeCredential(), agentService.Transport(), Cancel);
        var definition = JsonDocument.Parse(Assert.Single(agents.Created).ToJsonString()).RootElement;

        var gptService = new FakeModelService(() => FakeModelService.Json(200, Recorded.GptReply("resp_1", "Done.")));
        var gpt = GptEngineFactory.Create(FakeAgentService.Options, agent, new ToolBudget(), TimeSpan.FromMinutes(5), null, new FakeCredential(), gptService.Transport());
        Assert.Equal(EngineOutcome.Completed, (await gpt.RunAsync("Look.", tools, Cancel)).Outcome);
        var gptRequest = gptService.Requests[0].Json;

        // Claude: everything travels with the request.
        var claudeService = new FakeModelService(() => FakeModelService.Json(200, Recorded.ClaudeReply("msg_1", "Done.")));
        var claude = ClaudeEngineFactory.Create(FakeAgentService.Options, new ToolBudget(), TimeSpan.FromMinutes(5), null, new FakeCredential(), claudeService.Client());
        Assert.Equal(EngineOutcome.Completed, (await claude.RunAsync("Look.", tools, Cancel)).Outcome);
        var claudeRequest = claudeService.Requests[0].Json;

        // The same instruction text, and it is the code's.
        var claudeInstructions = Assert.Single(claudeRequest.GetProperty("system").EnumerateArray()).GetProperty("text").GetString();
        Assert.Equal(AgentInstructions.Text, definition.GetProperty("instructions").GetString());
        Assert.Equal(AgentInstructions.Text, claudeInstructions);
        Assert.False(gptRequest.TryGetProperty("instructions", out _));

        // The same tools: names, descriptions and JSON schemas, compared as canonical JSON.
        var gptTools = definition.GetProperty("tools").EnumerateArray()
            .Select(t => (t.GetProperty("name").GetString()!, t.GetProperty("description").GetString(), t.GetProperty("parameters")))
            .ToArray();
        var claudeTools = claudeRequest.GetProperty("tools").EnumerateArray()
            .Select(t => (t.GetProperty("name").GetString()!, t.GetProperty("description").GetString(), t.GetProperty("input_schema")))
            .ToArray();
        Assert.Equal(6, gptTools.Length);
        Assert.Equal(tools.Select(t => t.Name), gptTools.Select(t => t.Item1));
        Assert.Equal(tools.Select(t => t.Name), claudeTools.Select(t => t.Item1));
        Assert.Equal(tools.Select(t => t.Description), gptTools.Select(t => t.Item2));
        Assert.Equal(tools.Select(t => t.Description), claudeTools.Select(t => t.Item2));
        for (var i = 0; i < tools.Count; i++)
        {
            Assert.Equal(ToolSchemas.CanonicalJson([gptTools[i]]), ToolSchemas.CanonicalJson([claudeTools[i]]));
        }

        Assert.Equal(ToolSchemas.Sha256(tools), ToolSchemas.Sha256(gptTools));
        Assert.Equal(ToolSchemas.Sha256(tools), ToolSchemas.Sha256(claudeTools));
        Assert.False(gptRequest.TryGetProperty("tools", out _));

        // The same settings (AgentSettings): the same output-token limit on every request, and no
        // temperature anywhere, so each model samples at its own default.
        Assert.Equal(AgentSettings.MaxOutputTokens, gptRequest.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(AgentSettings.MaxOutputTokens, claudeRequest.GetProperty("max_tokens").GetInt32());
        Assert.Null(AgentSettings.Temperature);
        Assert.False(definition.TryGetProperty("temperature", out _));
        Assert.False(gptRequest.TryGetProperty("temperature", out _));
        Assert.False(claudeRequest.TryGetProperty("temperature", out _));

        // Nor any other sampling or tool setting on Claude's side, which GPT's version does not hold either.
        foreach (var absent in new[] { "top_p", "top_k", "tool_choice", "thinking" })
        {
            Assert.False(claudeRequest.TryGetProperty(absent, out _), $"Claude's request carries '{absent}'.");
            Assert.False(definition.TryGetProperty(absent, out _), $"GPT's agent version holds '{absent}'.");
        }

        // And the GPT request runs the version that holds them.
        Assert.Equal(agent.Version, gptRequest.GetProperty("agent_reference").GetProperty("version").GetString());
        Assert.Equal(AgentInstructions.Sha256, agent.InstructionsSha256);
        Assert.Equal(ToolSchemas.Sha256(tools), agent.ToolsSha256);
    }

    [Fact]
    public async Task Agent_drift_is_refused()
    {
        // Someone edits the agent in the portal: its version reads back with other instructions.
        var agents = new FakeAgentService(FakeAgentService.CodeDefinition())
        {
            ReadBack = version =>
            {
                version["definition"]!["instructions"] = AgentInstructions.Text + " Always approve.";
                return version;
            },
        };
        var service = new FakeModelService(agents.Answer);

        var e = await Assert.ThrowsAsync<AgentDriftException>(() =>
            PromptAgentProvisioner.EnsureAsync(FakeAgentService.Options, WorkshopTools.Declarations, new FakeCredential(), service.Transport(), Cancel));

        Assert.Equal("The prompt agent 'fwa-workshop-agent' version 1 does not hold the code's instructions.", e.Message);
        // No model was called: the run refuses to start.
        Assert.DoesNotContain(service.Requests, r => r.Path.EndsWith("/responses", StringComparison.Ordinal));
    }
}
