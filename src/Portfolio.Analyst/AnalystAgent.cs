#pragma warning disable OPENAI001 // The OpenAI SDK marks its Responses types (McpTool among them) for evaluation; Foundry's agents are built on them.
#pragma warning disable SCME0001 // JsonPatch: the only way 2.0.1 has to set the MCP tool's project_connection_id (VERIFY.md).

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Core;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Workshop.Agent.Engines;
using Workshop.Agent.Engines.Foundry;

namespace Portfolio.Analyst;

/// <summary>The analyst's prompt agent does not hold what the code says: the run refuses to start. The message names the field, never its value.</summary>
public sealed class AnalystDriftException(string message) : Exception(message);

/// <summary>
/// The <c>portfolio-analyst</c> prompt agent, in project 3's Foundry project: its instructions
/// (<c>instructions.md</c>, as frozen) and one tool, Foundry's generic MCP tool labelled
/// <c>portfolio-data</c>, whose server is the Fabric data agent's MCP endpoint and whose calls are
/// signed in through a project connection, never approved by hand (<c>require_approval: never</c>).
/// Nothing else of the definition is set. Keyless: the project client is
/// <see cref="GptEngineFactory.ProjectClient"/>'s, with an Entra token for <c>https://ai.azure.com/.default</c>.
/// </summary>
/// <remarks>
/// Azure.AI.Projects 2.0.1 builds the tool from the OpenAI SDK's <see cref="McpTool"/>, which has a
/// server URL, headers and an approval policy but no typed project connection; the connection goes
/// in through the model's <c>Patch</c> as <c>project_connection_id</c>. VERIFY.md has what plan 2's
/// spike must confirm against the live service.
/// </remarks>
public static class AnalystAgent
{
    public const string Name = "portfolio-analyst";
    public const string ServerLabel = "portfolio-data";
    public const string RequireApproval = "never";

    // The fields the code sets, compared on their own; any other must be at its default.
    private static readonly string[] Compared = ["kind", "model", "instructions", "tools"];
    private static readonly string[] ToolFields = ["type", "server_label", "server_url", "require_approval", "project_connection_id"];

    /// <summary>The code's definition: the model, the instructions and the one MCP tool.</summary>
    public static DeclarativeAgentDefinition Definition(AnalystSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var tool = ResponseTool.CreateMcpTool(
            ServerLabel,
            setup.McpUrl,
            toolCallApprovalPolicy: new McpToolCallApprovalPolicy(GlobalMcpToolCallApprovalPolicy.NeverRequireApproval));
        tool.Patch.Set("$.project_connection_id"u8, setup.ConnectionId);
        var definition = new DeclarativeAgentDefinition(setup.Model) { Instructions = setup.Instructions };
        definition.Tools.Add(tool);
        return definition;
    }

    /// <summary>
    /// Reuses the agent's latest version when it holds the code's definition, and creates a new
    /// version otherwise (or the agent's first). Either way it reads the version back and throws
    /// <see cref="AnalystDriftException"/> when what it reads is not the code's.
    /// </summary>
    /// <returns>The version a run uses.</returns>
    public static async Task<string> EnsureAsync(AnalystSetup setup, TokenCredential credential, PipelineTransport? transport, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(credential);
        var admin = ProjectClient(setup, credential, transport).AgentAdministrationClient;

        var latest = await LatestAsync(admin, ct).ConfigureAwait(false);
        var version = latest is { } found && Difference(found, setup) is null
            ? Text(found, "version")!
            : (await admin.CreateAgentVersionAsync(Name, new ProjectsAgentVersionCreationOptions(Definition(setup)), foundryFeatures: null, ct).ConfigureAwait(false)).Value.Version;

        var readBack = Raw(await admin.GetAgentVersionAsync(Name, version, ct).ConfigureAwait(false));
        if (Difference(readBack, setup) is { } difference)
        {
            throw new AnalystDriftException($"The prompt agent {Name} version {version} does not hold the code's {difference}.");
        }

        return version;
    }

    /// <summary>
    /// The analyst over the agent's version: its Responses client, naming the version on each
    /// request, with the SDK's errors mapped and throttling waited out as project 3's engines do.
    /// </summary>
    public static IAnalystEngine CreateEngine(AnalystSetup setup, string version, TokenCredential credential, PipelineTransport? transport)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(credential);
        var reference = new AgentReference(Name, version);
        var responses = ProjectClient(setup, credential, transport).ProjectOpenAIClient.GetProjectResponsesClientForAgent(reference);
        var client = new ThrottleRetryChatClient(new SdkErrorChatClient(responses.AsIChatClient()));

        // A fresh request each time: no instructions or tools (the version holds them), no
        // conversation and no previous response, so no question sees another's context.
        return new ChatClientAnalystEngine(client, () => new ChatOptions
        {
            RawRepresentationFactory = _ =>
            {
                var request = new CreateResponseOptions();
                request.Agent = reference;
                return request;
            },
        });
    }

    /// <summary>
    /// <see cref="GptEngineFactory.ProjectClient"/>: keyless, the pipeline's retries off, one call's
    /// time limit set. It reads only the project endpoint from the options.
    /// </summary>
    private static AIProjectClient ProjectClient(AnalystSetup setup, TokenCredential credential, PipelineTransport? transport) =>
        GptEngineFactory.ProjectClient(new FoundryOptions(setup.ProjectEndpoint, setup.ProjectEndpoint, setup.Model, setup.Model, Name), credential, transport);

    /// <summary>What in the version (its raw JSON) is not the code's, first found, by its field's name; null when nothing.</summary>
    private static string? Difference(JsonElement version, AnalystSetup setup)
    {
        if (!version.TryGetProperty("definition", out var definition) || definition.ValueKind != JsonValueKind.Object)
        {
            return "definition";
        }

        if (Text(definition, "kind") != "prompt")
        {
            return "kind (prompt)";
        }

        if (Text(definition, "model") != setup.Model)
        {
            return "model";
        }

        if (Text(definition, "instructions") != setup.Instructions)
        {
            return "instructions";
        }

        if (!definition.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() != 1)
        {
            return "tools (one MCP tool)";
        }

        if (ToolDifference(tools[0], setup) is { } tool)
        {
            return $"MCP tool's {tool}";
        }

        return definition.EnumerateObject()
            .Where(p => !Compared.Contains(p.Name, StringComparer.Ordinal) && !IsDefault(p.Name, p.Value))
            .Select(p => p.Name)
            .FirstOrDefault();
    }

    private static string? ToolDifference(JsonElement tool, AnalystSetup setup)
    {
        if (tool.ValueKind != JsonValueKind.Object || Text(tool, "type") != "mcp")
        {
            return "type";
        }

        if (Text(tool, "server_label") != ServerLabel)
        {
            return "server_label";
        }

        if (!Uri.TryCreate(Text(tool, "server_url"), UriKind.Absolute, out var url) || url != setup.McpUrl)
        {
            return "server_url";
        }

        if (Text(tool, "project_connection_id") != setup.ConnectionId)
        {
            return "project_connection_id";
        }

        if (Text(tool, "require_approval") != RequireApproval)
        {
            return "require_approval";
        }

        return tool.EnumerateObject()
            .Where(p => !ToolFields.Contains(p.Name, StringComparer.Ordinal) && !IsDefault(p.Name, p.Value))
            .Select(p => p.Name)
            .FirstOrDefault();
    }

    /// <summary>Whether a field is at its default: null, an empty list, an object whose members all are, <c>tool_choice: "auto"</c> or a text format of type <c>text</c>.</summary>
    private static bool IsDefault(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.Object => value.EnumerateObject().All(p => IsDefault(p.Name, p.Value)),
        JsonValueKind.String => (name, value.GetString()) is ("tool_choice", "auto") or ("type", "text"),
        _ => false,
    };

    /// <summary>The agent's latest version, in its raw JSON; null when there is no agent of that name yet.</summary>
    private static async Task<JsonElement?> LatestAsync(AgentAdministrationClient admin, CancellationToken ct)
    {
        try
        {
            var agent = Raw(await admin.GetAgentAsync(Name, ct).ConfigureAwait(false));
            return agent.TryGetProperty("versions", out var versions) && versions.TryGetProperty("latest", out var latest) && latest.ValueKind == JsonValueKind.Object
                ? latest
                : null;
        }
        catch (ClientResultException e) when (e.Status == 404)
        {
            return null;
        }
    }

    private static JsonElement Raw(ClientResult result)
    {
        using var json = JsonDocument.Parse(result.GetRawResponse().Content);
        return json.RootElement.Clone();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
