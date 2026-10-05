#pragma warning disable OPENAI001 // The OpenAI SDK marks its Responses types for evaluation; Foundry's agent definitions are built on them.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.AI.Projects.Agents;
using Azure.Core;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Engines.Foundry;

/// <summary>The prompt agent version a GPT run uses, and the hashes of what it was read back to hold.</summary>
/// <param name="Name">The agent's name.</param>
/// <param name="Version">The version, as the service numbers it.</param>
/// <param name="InstructionsSha256">The SHA-256 of the version's instructions, read back; it equals <see cref="AgentInstructions.Sha256"/>.</param>
/// <param name="ToolsSha256">The <see cref="ToolSchemas"/> hash of the version's tools, read back; it equals the code's.</param>
public sealed record AgentVersionRef(string Name, string Version, string InstructionsSha256, string ToolsSha256);

/// <summary>The prompt agent's version does not hold what the code says (Review Focus 2): the run refuses to start.</summary>
public sealed class AgentDriftException(string message) : Exception(message);

/// <summary>
/// Makes sure the GPT prompt agent has a version that holds exactly the code's definition, and
/// nothing more: a prompt agent on the GPT deployment, with <see cref="AgentInstructions.Text"/> and
/// the tools' schemas (<see cref="ToolSchemas"/>), and every other field of the definition at its
/// default: no temperature or top_p (<see cref="AgentSettings.Temperature"/>, the model's default),
/// no reasoning or text options, no tool choice but <c>auto</c>, no structured inputs and no
/// content-filter configuration (<c>rai_config</c>). The output-token limit is not part of a prompt
/// agent's definition; it travels with each request.
/// </summary>
/// <remarks>
/// The definition is compared as the service sent it, in its raw JSON, rather than through the SDK's
/// model, which drops fields it does not know: a field the service adds later, set in the portal,
/// is a difference too unless it is at its default (null, empty, or an object of defaults).
/// </remarks>
public static class PromptAgentProvisioner
{
    // The fields the code sets, compared on their own; any other must be at its default.
    private static readonly string[] Compared = ["kind", "model", "instructions", "tools"];

    // The fields of a function tool the code sets; strict is compared on its own, against StrictMode.
    private static readonly string[] ToolFields = ["type", "name", "description", "parameters", "strict"];

    /// <summary>
    /// The code's strict mode for every tool: null, unset, so neither engine's tool calls are checked
    /// against their schemas by the service. Strict mode on GPT alone would make the engines differ.
    /// </summary>
    internal static bool? StrictMode => null;

    /// <summary>
    /// Reuses the agent's latest version when it holds the code's definition, and creates a new
    /// version otherwise (or the agent's first). Either way it reads the version back, and throws
    /// <see cref="AgentDriftException"/> when what it reads is not the code's.
    /// </summary>
    public static Task<AgentVersionRef> EnsureAsync(FoundryOptions options, IReadOnlyList<AIFunction> tools, TokenCredential credential, CancellationToken ct = default) =>
        EnsureAsync(options, tools, credential, transport: null, ct);

    /// <summary>For tests: the project client's HTTP transport.</summary>
    internal static async Task<AgentVersionRef> EnsureAsync(FoundryOptions options, IReadOnlyList<AIFunction> tools, TokenCredential credential, PipelineTransport? transport, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(credential);

        var toolsSha256 = ToolSchemas.Sha256(tools);
        var admin = GptEngineFactory.ProjectClient(options, credential, transport).AgentAdministrationClient;

        var latest = await LatestAsync(admin, options.AgentName, ct).ConfigureAwait(false);
        var version = latest is { } found && Difference(found, options, toolsSha256) is null
            ? Text(found, "version")!
            : (await admin.CreateAgentVersionAsync(options.AgentName, new ProjectsAgentVersionCreationOptions(Definition(options, tools)), foundryFeatures: null, ct).ConfigureAwait(false)).Value.Version;

        // Read back what the service now holds, whether reused or new: that is what a run will use.
        var readBack = Raw(await admin.GetAgentVersionAsync(options.AgentName, version, ct).ConfigureAwait(false));
        if (Difference(readBack, options, toolsSha256) is { } difference)
        {
            throw new AgentDriftException($"The prompt agent '{options.AgentName}' version {version} does not hold the code's {difference}.");
        }

        var definition = readBack.GetProperty("definition");
        return new AgentVersionRef(options.AgentName, version, Sha256(Text(definition, "instructions")), ToolsSha256(definition.GetProperty("tools"))!);
    }

    /// <summary>The code's definition: the GPT deployment, the instructions and each tool as a function with its closed schema.</summary>
    internal static DeclarativeAgentDefinition Definition(FoundryOptions options, IReadOnlyList<AIFunction> tools)
    {
        var definition = new DeclarativeAgentDefinition(options.GptDeployment) { Instructions = AgentInstructions.Text };
        foreach (var tool in tools)
        {
            definition.Tools.Add(ResponseTool.CreateFunctionTool(
                tool.Name,
                BinaryData.FromString(ToolSchemas.Parameters(tool).GetRawText()),
                strictModeEnabled: StrictMode,
                tool.Description));
        }

        return definition;
    }

    /// <summary>What in the version (its raw JSON) is not the code's, first found, by its field's name; null when nothing.</summary>
    private static string? Difference(JsonElement version, FoundryOptions options, string toolsSha256)
    {
        if (!version.TryGetProperty("definition", out var definition) || definition.ValueKind != JsonValueKind.Object)
        {
            return "definition";
        }

        if (Text(definition, "kind") != "prompt")
        {
            return "kind (prompt)";
        }

        if (Text(definition, "model") != options.GptDeployment)
        {
            return "model";
        }

        if (Text(definition, "instructions") != AgentInstructions.Text)
        {
            return "instructions";
        }

        if (!definition.TryGetProperty("tools", out var tools) || ToolsSha256(tools) != toolsSha256)
        {
            return "tools";
        }

        // Everything else the definition holds must be at its default: the code sets none of it.
        return definition.EnumerateObject()
            .Where(p => !Compared.Contains(p.Name, StringComparer.Ordinal) && !IsDefault(p.Name, p.Value))
            .Select(p => p.Name)
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether a field is at its default: null, an empty list, an object whose members all are,
    /// <c>tool_choice: "auto"</c> or a text format of type <c>text</c>.
    /// </summary>
    private static bool IsDefault(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.Object => value.EnumerateObject().All(p => IsDefault(p.Name, p.Value)),
        JsonValueKind.String => (name, value.GetString()) is ("tool_choice", "auto") or ("type", "text"),
        _ => false,
    };

    /// <summary>The agent's latest version, in its raw JSON; null when there is no agent of that name yet.</summary>
    private static async Task<JsonElement?> LatestAsync(AgentAdministrationClient admin, string name, CancellationToken ct)
    {
        try
        {
            var agent = Raw(await admin.GetAgentAsync(name, ct).ConfigureAwait(false));
            return agent.TryGetProperty("versions", out var versions) && versions.TryGetProperty("latest", out var latest) && latest.ValueKind == JsonValueKind.Object
                ? latest
                : null;
        }
        catch (ClientResultException e) when (e.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// The <see cref="ToolSchemas"/> hash of a definition's tools; null when one is not a function,
    /// or holds a field the code does not set at other than its default.
    /// </summary>
    private static string? ToolsSha256(JsonElement tools)
    {
        if (tools.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var entries = new List<(string Name, string? Description, JsonElement Parameters)>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object
                || Text(tool, "type") != "function"
                || Text(tool, "name") is not { } name
                || tool.EnumerateObject().Any(p => !ToolFields.Contains(p.Name, StringComparer.Ordinal) && !IsDefault(p.Name, p.Value))
                || !StrictIsTheCodes(tool))
            {
                return null;
            }

            var parameters = tool.TryGetProperty("parameters", out var given) && given.ValueKind == JsonValueKind.Object
                ? given
                : JsonDocument.Parse("{}").RootElement;
            entries.Add((name, Text(tool, "description"), parameters));
        }

        return ToolSchemas.Sha256(entries);
    }

    /// <summary>
    /// Whether a tool's <c>strict</c> is the code's (<see cref="StrictMode"/>): absent or null for
    /// null, and exactly <see langword="true"/> or <see langword="false"/> otherwise. A portal edit
    /// that turns strict mode on, or any other value, is a difference.
    /// </summary>
    private static bool StrictIsTheCodes(JsonElement tool) =>
        !tool.TryGetProperty("strict", out var strict)
            ? StrictMode is null
            : strict.ValueKind switch
            {
                JsonValueKind.Null => StrictMode is null,
                JsonValueKind.True => StrictMode == true,
                JsonValueKind.False => StrictMode == false,
                _ => false,
            };

    /// <summary>A result's body as JSON, as the service sent it.</summary>
    private static JsonElement Raw(ClientResult result)
    {
        using var json = JsonDocument.Parse(result.GetRawResponse().Content);
        return json.RootElement.Clone();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Sha256(string? text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")));
}
