#pragma warning disable OPENAI001 // CreateResponseOptions is the Responses API's request; the OpenAI SDK marks it for evaluation.

using System.Security.Cryptography;
using System.Text;
using Azure.AI.Extensions.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Engines.Foundry;

/// <summary>
/// Under the function loop, turns each request into one for the prompt agent's version: it names
/// the version (<c>agent_reference</c>) and leaves out the instructions and the tools, which the
/// version holds. The output-token limit and the conversation go on as they were. The loop above
/// keeps its own options, so it still sees, and runs, the tools.
/// </summary>
/// <remarks>
/// It refuses (<see cref="AgentDriftException"/>) a request whose instructions or tools are missing,
/// or are not the ones the version was read back to hold: what the loop runs and what the model was told cannot
/// part. The Responses chat client of Microsoft.Extensions.AI.OpenAI sends its requests through the
/// SDK's protocol method, past the project client's default agent, so the agent is named here, in
/// the request it builds from (<see cref="ChatOptions.RawRepresentationFactory"/>).
/// </remarks>
internal sealed class AgentReferenceChatClient(IChatClient inner, AgentVersionRef agent) : DelegatingChatClient(inner)
{
    private readonly Lock gate = new();
    private IList<AITool>? checkedTools;

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Check(options);
        var sent = options?.Clone() ?? new ChatOptions();
        sent.Instructions = null;
        sent.Tools = null;
        sent.RawRepresentationFactory = _ =>
        {
            var request = new CreateResponseOptions();
            request.Agent = new AgentReference(agent.Name, agent.Version);
            return request;
        };
        return base.GetResponseAsync(messages, sent, cancellationToken);
    }

    /// <summary>The engine never streams; a stream here would not name the agent, so it is refused.</summary>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The engine calls the model without streaming.");

    private void Check(ChatOptions? options)
    {
        // A request without the instructions or the tools is not the loop's: it is refused, not waved through.
        if (options?.Instructions is not { } instructions
            || Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions))) != agent.InstructionsSha256)
        {
            throw Drift("instructions");
        }

        if (options.Tools is not { } tools)
        {
            throw Drift("tools");
        }

        lock (gate)
        {
            // The loop sends the same list on every request: it is hashed once.
            if (ReferenceEquals(tools, checkedTools))
            {
                return;
            }
        }

        if (ToolSchemas.Sha256OrNull(tools) != agent.ToolsSha256)
        {
            throw Drift("tools");
        }

        lock (gate)
        {
            checkedTools = tools;
        }
    }

    private AgentDriftException Drift(string what) =>
        new($"The loop's {what} are not the ones prompt agent '{agent.Name}' version {agent.Version} holds.");
}
