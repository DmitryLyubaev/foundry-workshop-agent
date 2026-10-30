using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Portfolio.Analyst;

/// <summary>Where a live run finds Foundry and the data agent, and what the agent is told. Its <see cref="ToString"/> names no endpoint or ID.</summary>
/// <param name="ProjectEndpoint">The Foundry project's endpoint (<c>FWA_PROJECT_ENDPOINT</c>), project 3's.</param>
/// <param name="Model">The model deployment the agent runs on: the freeze's Foundry model.</param>
/// <param name="McpUrl">The data agent's MCP endpoint (<c>ANALYST_MCP_URL</c>).</param>
/// <param name="ConnectionId">The project connection that signs the MCP calls in (<c>ANALYST_CONNECTION_ID</c>).</param>
/// <param name="Instructions">The agent's instructions: <c>instructions.md</c>, LF-normalised, as frozen.</param>
public sealed record AnalystSetup(Uri ProjectEndpoint, string Model, Uri McpUrl, string ConnectionId, string Instructions)
{
    public override string ToString() => $"AnalystSetup {{ Model = {Model} }}";
}

/// <summary>One answer of the analyst.</summary>
/// <param name="Text">The answer's text, unredacted: the record writer redacts it.</param>
/// <param name="RawOutput">What the model returned, as JSON: <c>{"finish_reason", "items": [...]}</c>.</param>
/// <param name="ToolCalls">The tool calls made for this answer: the MCP calls the service ran.</param>
/// <param name="InputTokens">The input tokens, when the service counts them.</param>
/// <param name="OutputTokens">The output tokens, when the service counts them.</param>
public sealed record AnalystAnswer(string Text, JsonNode RawOutput, int ToolCalls, long? InputTokens, long? OutputTokens);

/// <summary>The seam between a run and its model: one question in, one answer out, in a fresh conversation.</summary>
public interface IAnalystEngine
{
    Task<AnalystAnswer> AskAsync(string question, CancellationToken ct);
}

/// <summary>The agent did not answer: it failed in a way that is not an answer to score. <see cref="Code"/> names how, never a value.</summary>
public sealed class AnalystEngineException(string code) : Exception($"the agent did not answer: {code}")
{
    public string Code { get; } = code;
}

/// <summary>
/// The analyst over a Microsoft.Extensions.AI chat client: the prompt agent's Responses client in a
/// live run (<see cref="AnalystAgent.CreateEngine"/>), a scripted model in tests. Each question is
/// sent alone, as a new message list with no conversation or previous response, so nothing carries
/// over from one question to the next. The tool runs in the service: the answer comes back with
/// its MCP calls already made, and those are counted. A tool error or an approval request is an
/// <see cref="AnalystEngineException"/> (<c>tool_error</c>, <c>approval_request</c>), never an answer.
/// </summary>
public sealed class ChatClientAnalystEngine(IChatClient client, Func<ChatOptions?>? options = null) : IAnalystEngine
{
    public async Task<AnalystAnswer> AskAsync(string question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, question)], options?.Invoke(), ct).ConfigureAwait(false);

        // A function call left to the client: the agent holds a tool this client does not run, so it is not the code's agent.
        if (response.FinishReason == ChatFinishReason.ToolCalls)
        {
            throw new AnalystEngineException("tool_calls");
        }

        // The tool's own failures are failed calls, not answers, as on the direct route (an MCP isError
        // stops it): an approval request the agent's "never" should rule out, and a tool result that
        // is an error. Neither is scored; the run stops, and nothing is recorded for this question.
        var all = response.Messages.SelectMany(m => m.Contents).ToList();
        if (all.Any(c => c is ToolApprovalRequestContent))
        {
            throw new AnalystEngineException("approval_request");
        }

        if (all.OfType<McpServerToolResultContent>().Any(r => r.Outputs?.Any(o => o is ErrorContent) == true))
        {
            throw new AnalystEngineException("tool_error");
        }

        var contents = response.Messages.Where(m => m.Role == ChatRole.Assistant).SelectMany(m => m.Contents).ToList();
        var items = new JsonArray();
        foreach (var content in contents)
        {
            items.Add(Item(content));
        }

        var raw = new JsonObject
        {
            ["finish_reason"] = response.FinishReason?.Value,
            ["items"] = items,
        };
        var toolCalls = contents.Count(c => c is McpServerToolCallContent or FunctionCallContent);
        return new AnalystAnswer(response.Text, raw, toolCalls, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
    }

    /// <summary>One piece of the answer, as the record's raw output keeps it: text, an MCP call and its result, or the kind of anything else.</summary>
    private static JsonObject Item(AIContent content) => content switch
    {
        TextContent text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
        McpServerToolCallContent call => new JsonObject
        {
            ["type"] = "mcp_call",
            ["server_label"] = call.ServerName,
            ["name"] = call.Name,
            ["arguments"] = Json(call.Arguments),
        },
        McpServerToolResultContent result => new JsonObject
        {
            ["type"] = "mcp_result",
            ["output"] = new JsonArray([.. (result.Outputs ?? []).Select(o => (JsonNode?)(o is TextContent t ? JsonValue.Create(t.Text) : JsonValue.Create(o.GetType().Name)))]),
        },
        FunctionCallContent call => new JsonObject { ["type"] = "function_call", ["name"] = call.Name, ["arguments"] = Json(call.Arguments) },
        _ => new JsonObject { ["type"] = content.GetType().Name },
    };

    private static JsonNode? Json(IDictionary<string, object?>? arguments) =>
        arguments is null ? null : JsonSerializer.SerializeToNode(arguments, AIJsonUtilities.DefaultOptions);
}
