using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Workshop.Agent.Engines;
using Workshop.Agent.Engines.Foundry;

namespace Workshop.Agent.Tests.RealEngines;

/// <summary>
/// The project's agent endpoints, faked: it keeps an agent's versions as the service stores them,
/// answers <c>GET agents/{name}</c> with the latest, stores each new version a <c>POST</c> sends, and
/// reads a version back, through <see cref="ReadBack"/> when a test makes the service's copy differ.
/// </summary>
internal sealed class FakeAgentService
{
    public const string AgentName = FoundryOptions.DefaultAgentName;

    private readonly Lock gate = new();
    private readonly List<JsonObject> versions = [];

    /// <param name="definitions">The agent's versions before the test, oldest first; none means no agent yet.</param>
    public FakeAgentService(params JsonObject[] definitions)
    {
        foreach (var definition in definitions)
        {
            Store(definition);
        }
    }

    /// <summary>Changes a version as it is read back, for the drift a service-side edit would be.</summary>
    public Func<JsonObject, JsonObject>? ReadBack { get; set; }

    /// <summary>The versions the code created, as their definitions arrived.</summary>
    public List<JsonObject> Created { get; } = [];

    public static FoundryOptions Options { get; } = new(
        new Uri("https://example.services.ai.azure.com/api/projects/example"),
        new Uri("https://example.services.ai.azure.com/"),
        "gpt-5.6-luna",
        "claude-haiku-4-5",
        AgentName);

    /// <summary>
    /// A prompt agent definition, as the service stores it, written here from the tools themselves
    /// rather than by the code under test: each schema closed with <c>additionalProperties: false</c>.
    /// </summary>
    public static JsonObject Definition(string model, string instructions, IEnumerable<AIFunction> tools)
    {
        var list = new JsonArray();
        foreach (var tool in tools)
        {
            var parameters = JsonNode.Parse(tool.JsonSchema.GetRawText())!.AsObject();
            parameters["additionalProperties"] = false;
            list.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = parameters,
                ["strict"] = null,
            });
        }

        return new JsonObject { ["kind"] = "prompt", ["model"] = model, ["instructions"] = instructions, ["tools"] = list };
    }

    /// <summary>The definition the code should hold: <see cref="AgentInstructions.Text"/> and the six tools on the GPT deployment.</summary>
    public static JsonObject CodeDefinition() => Definition(Options.GptDeployment, AgentInstructions.Text, Tools.WorkshopTools.Declarations);

    public HttpResponseMessage Answer(SentRequest request)
    {
        var path = request.Path;
        var agent = $"/agents/{AgentName}";
        lock (gate)
        {
            if (request.Method == HttpMethod.Get && path.EndsWith(agent, StringComparison.Ordinal))
            {
                return versions.Count == 0
                    ? FakeModelService.Json(404, Recorded.AzureError("not_found", $"Agent {AgentName} was not found."))
                    : FakeModelService.Json(200, new JsonObject
                    {
                        ["object"] = "agent",
                        ["id"] = AgentName,
                        ["name"] = AgentName,
                        ["versions"] = new JsonObject { ["latest"] = versions[^1].DeepClone() },
                    }.ToJsonString());
            }

            if (request.Method == HttpMethod.Post && path.EndsWith($"{agent}/versions", StringComparison.Ordinal))
            {
                var definition = JsonNode.Parse(request.Body)!["definition"]!.AsObject();
                Created.Add(definition);
                return FakeModelService.Json(200, Store(definition).ToJsonString());
            }

            var prefix = $"{agent}/versions/";
            var at = path.LastIndexOf(prefix, StringComparison.Ordinal);
            if (request.Method == HttpMethod.Get && at >= 0)
            {
                var version = int.Parse(path[(at + prefix.Length)..], CultureInfo.InvariantCulture);
                var stored = versions[version - 1].DeepClone().AsObject();
                return FakeModelService.Json(200, (ReadBack?.Invoke(stored) ?? stored).ToJsonString());
            }
        }

        throw new InvalidOperationException($"The fake agent service has no answer for {request.Method} {path}.");
    }

    private JsonObject Store(JsonObject definition)
    {
        var version = (versions.Count + 1).ToString(CultureInfo.InvariantCulture);
        var stored = new JsonObject
        {
            ["object"] = "agent.version",
            ["id"] = $"{AgentName}:{version}",
            ["name"] = AgentName,
            ["version"] = version,
            ["created_at"] = 1759600000,
            ["definition"] = definition.DeepClone(),
        };
        versions.Add(stored);
        return stored;
    }
}
