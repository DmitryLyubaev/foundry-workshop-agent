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
/// Makes sure the GPT prompt agent has a version that holds exactly the code's definition: the GPT
/// deployment, <see cref="AgentInstructions.Text"/>, the tools' schemas (<see cref="ToolSchemas"/>)
/// and no temperature (<see cref="AgentSettings.Temperature"/>, the model's default). The output-token
/// limit is not part of a prompt agent's definition; it travels with each request.
/// </summary>
public static class PromptAgentProvisioner
{
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
        var chosen = latest is not null && Difference(latest, options, toolsSha256) is null
            ? latest
            : (await admin.CreateAgentVersionAsync(options.AgentName, new ProjectsAgentVersionCreationOptions(Definition(options, tools)), foundryFeatures: null, ct).ConfigureAwait(false)).Value;

        // Read back what the service now holds, whether reused or new: that is what a run will use.
        var readBack = (await admin.GetAgentVersionAsync(options.AgentName, chosen.Version, ct).ConfigureAwait(false)).Value;
        if (Difference(readBack, options, toolsSha256) is { } difference)
        {
            throw new AgentDriftException($"The prompt agent '{options.AgentName}' version {readBack.Version} does not hold the code's {difference}.");
        }

        var definition = (DeclarativeAgentDefinition)readBack.Definition;
        return new AgentVersionRef(options.AgentName, readBack.Version, Sha256(definition.Instructions), ToolsSha256(definition)!);
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
                strictModeEnabled: null,
                tool.Description));
        }

        return definition;
    }

    /// <summary>What in <paramref name="version"/> is not the code's, first found; null when nothing.</summary>
    private static string? Difference(ProjectsAgentVersion version, FoundryOptions options, string toolsSha256)
    {
        if (version.Definition is not DeclarativeAgentDefinition definition)
        {
            return "kind (prompt)";
        }

        if (definition.Model != options.GptDeployment)
        {
            return "model";
        }

        if (definition.Instructions != AgentInstructions.Text)
        {
            return "instructions";
        }

        if (ToolsSha256(definition) != toolsSha256)
        {
            return "tools";
        }

        if (definition.Temperature != AgentSettings.Temperature)
        {
            return "temperature";
        }

        // The code sets no sampling but the temperature: a top_p would change it all the same.
        return definition.TopP is null ? null : "top_p";
    }

    /// <summary>The agent's latest version; null when there is no agent of that name yet.</summary>
    private static async Task<ProjectsAgentVersion?> LatestAsync(AgentAdministrationClient admin, string name, CancellationToken ct)
    {
        try
        {
            return (await admin.GetAgentAsync(name, ct).ConfigureAwait(false)).Value.GetLatestVersion();
        }
        catch (ClientResultException e) when (e.Status == 404)
        {
            return null;
        }
    }

    /// <summary>The <see cref="ToolSchemas"/> hash of a definition's tools; null when one is not a function tool.</summary>
    private static string? ToolsSha256(DeclarativeAgentDefinition definition)
    {
        var functions = definition.Tools.OfType<FunctionTool>().ToList();
        if (functions.Count != definition.Tools.Count)
        {
            return null;
        }

        return ToolSchemas.Sha256(functions.Select(f => (f.FunctionName, (string?)f.FunctionDescription, JsonDocument.Parse(f.FunctionParameters ?? BinaryData.FromString("{}")).RootElement.Clone())));
    }

    private static string Sha256(string? text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")));
}
