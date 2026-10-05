#pragma warning disable OPENAI001 // The OpenAI SDK marks its Responses client for evaluation; Foundry's agents run on it.

using System.ClientModel.Primitives;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Extensions.AI;
using Workshop.Agent.Runner;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Engines.Foundry;

/// <summary>
/// The <c>gpt</c> engine: GPT as a Foundry Agent Service prompt agent. A <see cref="ChatClientEngine"/>
/// over the project's Responses client for the agent, as Microsoft.Extensions.AI's
/// <see cref="IChatClient"/>, with <see cref="AgentReferenceChatClient"/> under the loop naming the
/// version <see cref="PromptAgentProvisioner"/> checked, and <see cref="SdkErrorChatClient"/> mapping
/// the SDK's errors. Keyless: an Entra token for <c>https://ai.azure.com/.default</c>. The SDK's own
/// retries are off, so the engine's throttling budget is the only retry.
/// </summary>
public static class GptEngineFactory
{
    public const string Name = "gpt";

    /// <summary>
    /// The engine for one run. <paramref name="agent"/> comes from
    /// <see cref="PromptAgentProvisioner.EnsureAsync(FoundryOptions, IReadOnlyList{AIFunction}, TokenCredential, CancellationToken)"/>,
    /// once before the runs: every run uses the version it read back.
    /// </summary>
    public static ChatClientEngine Create(FoundryOptions options, AgentVersionRef agent, EngineRun run, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Create(options, agent, run.Budget, run.TimeLimit, run, credential, transport: null);
    }

    /// <summary>For tests: the run's parts, and the project client's HTTP transport.</summary>
    internal static ChatClientEngine Create(FoundryOptions options, AgentVersionRef agent, ToolBudget budget, TimeSpan timeLimit, IToolCallRecorder? recorder, TokenCredential credential, PipelineTransport? transport) =>
        new(Name, options.GptDeployment, CreateModel(options, agent, credential, transport), budget, timeLimit, recorder)
        {
            Deployment = options.GptDeployment,
            AgentVersion = agent.Version,
        };

    /// <summary>The model's client under the engine's loop: the agent's Responses client, its errors mapped.</summary>
    internal static IChatClient CreateModel(FoundryOptions options, AgentVersionRef agent, TokenCredential credential, PipelineTransport? transport)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(credential);
        var responses = ProjectClient(options, credential, transport).ProjectOpenAIClient
            .GetProjectResponsesClientForAgent(new AgentReference(agent.Name, agent.Version));
        return new AgentReferenceChatClient(new SdkErrorChatClient(responses.AsIChatClient()), agent);
    }

    /// <summary>
    /// The project's client, keyless, with the pipeline's retries off and one call's time limit set.
    /// Its agent administration and Responses clients share this pipeline.
    /// </summary>
    internal static AIProjectClient ProjectClient(FoundryOptions options, TokenCredential credential, PipelineTransport? transport)
    {
        var clientOptions = new AIProjectClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            NetworkTimeout = FoundryOptions.CallTimeout,
        };
        if (transport is not null)
        {
            clientOptions.Transport = transport;
        }

        return new AIProjectClient(options.ProjectEndpoint, credential, clientOptions);
    }
}
