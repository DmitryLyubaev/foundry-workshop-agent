using System.Text.Json.Nodes;
using Workshop.Agent.Engines;
using Workshop.Agent.Engines.Foundry;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests.RealEngines;

/// <summary>
/// The GPT prompt agent's version holds exactly the code's instructions and tools (Review Focus 2):
/// a matching latest version is reused, anything else gets a new version, and a version that reads
/// back differently refuses the run.
/// </summary>
public sealed class PromptAgentProvisionerTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_agent_gets_its_first_version_from_the_code()
    {
        var agents = new FakeAgentService();
        var service = new FakeModelService(agents.Answer);
        var credential = new FakeCredential();

        var agent = await Ensure(service, credential);

        Assert.Equal(new AgentVersionRef(FakeAgentService.AgentName, "1", AgentInstructions.Sha256, ToolSchemas.Sha256(WorkshopTools.Declarations)), agent);
        Assert.Equal(["GET", "POST", "GET"], service.Requests.Select(r => r.Method.Method));
        Assert.EndsWith("/api/projects/example/agents/fwa-workshop-agent/versions", service.Requests[1].Path, StringComparison.Ordinal);
        Assert.EndsWith("/api/projects/example/agents/fwa-workshop-agent/versions/1", service.Requests[2].Path, StringComparison.Ordinal);
        Assert.All(service.Requests, r => Assert.Equal("Bearer fake-token", r.Authorization));
        Assert.All(credential.Scopes, s => Assert.Equal("https://ai.azure.com/.default", s));

        // What was created is the code's definition, and nothing else: no temperature (the model's default), no other tool.
        var created = Assert.Single(agents.Created);
        Assert.Equal("prompt", created["kind"]!.GetValue<string>());
        Assert.Equal("gpt-5.6-luna", created["model"]!.GetValue<string>());
        Assert.Equal(AgentInstructions.Text, created["instructions"]!.GetValue<string>());
        Assert.False(created.ContainsKey("temperature"));
        Assert.Equal(
            WorkshopTools.Declarations.Select(t => t.Name),
            created["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        Assert.All(created["tools"]!.AsArray(), t => Assert.Equal("function", t!["type"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_latest_version_that_matches_the_code_is_reused()
    {
        // The service hands the definition back laid out its own way: other key order and an empty "required".
        var echoed = FakeAgentService.CodeDefinition();
        foreach (var tool in echoed["tools"]!.AsArray())
        {
            var parameters = tool!["parameters"]!.AsObject();
            parameters.TryAdd("required", new JsonArray());
        }

        var agents = new FakeAgentService(Old("Older instructions."), echoed);
        var service = new FakeModelService(agents.Answer);

        var agent = await Ensure(service, new FakeCredential());

        Assert.Equal("2", agent.Version);
        Assert.Empty(agents.Created);
        Assert.Equal(["GET", "GET"], service.Requests.Select(r => r.Method.Method));
        Assert.EndsWith("/agents/fwa-workshop-agent/versions/2", service.Requests[1].Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("instructions")]
    [InlineData("tool schema")]
    [InlineData("tool description")]
    [InlineData("a tool missing")]
    [InlineData("model")]
    [InlineData("temperature")]
    [InlineData("top_p")]
    [InlineData("reasoning")]
    [InlineData("text")]
    [InlineData("tool_choice")]
    [InlineData("structured_inputs")]
    [InlineData("rai_config")]
    [InlineData("a field the SDK does not know")]
    [InlineData("a tool field the code does not set")]
    [InlineData("strict true")]
    [InlineData("strict false")]
    public async Task A_latest_version_that_differs_gets_a_new_version(string difference)
    {
        var latest = FakeAgentService.CodeDefinition();
        var tools = latest["tools"]!.AsArray();
        if (Edit(difference) is { } edit)
        {
            edit(latest);
        }

        switch (difference)
        {
            case "instructions":
                latest["instructions"] = AgentInstructions.Text + " Be quick.";
                break;
            case "tool schema":
                tools[2]!["parameters"]!["properties"]!["screen"]!["type"] = "integer";
                break;
            case "tool description":
                tools[0]!["description"] = "Lists screens.";
                break;
            case "a tool missing":
                tools.RemoveAt(5);
                break;
            case "model":
                latest["model"] = "gpt-other";
                break;
        }

        var agents = new FakeAgentService(latest);
        var service = new FakeModelService(agents.Answer);

        var agent = await Ensure(service, new FakeCredential());

        Assert.Equal("2", agent.Version);
        // The new version is the code's alone: none of the portal's settings came with it.
        var created = Assert.Single(agents.Created);
        Assert.Equal(["kind", "model", "instructions", "tools"], created.Select(p => p.Key));
        Assert.Equal(AgentInstructions.Sha256, agent.InstructionsSha256);
        Assert.Equal(ToolSchemas.Sha256(WorkshopTools.Declarations), agent.ToolsSha256);
    }

    [Fact]
    public async Task A_tool_without_strict_matches_the_codes_null()
    {
        // The code leaves strict unset (null): a service that drops the field holds the same tool.
        var latest = FakeAgentService.CodeDefinition();
        foreach (var tool in latest["tools"]!.AsArray())
        {
            tool!.AsObject().Remove("strict");
        }

        var agents = new FakeAgentService(latest);
        var service = new FakeModelService(agents.Answer);

        var agent = await Ensure(service, new FakeCredential());

        Assert.Equal("1", agent.Version);
        Assert.Empty(agents.Created);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_version_that_reads_back_with_strict_set_is_drift(bool strict)
    {
        // Strict mode changes how GPT's tool calls are checked, which Claude's engine does not do: only the code's null is neutral.
        var agents = new FakeAgentService
        {
            ReadBack = version =>
            {
                version["definition"]!["tools"]![3]!["strict"] = strict;
                return version;
            },
        };
        var service = new FakeModelService(agents.Answer);

        var e = await Assert.ThrowsAsync<AgentDriftException>(() => Ensure(service, new FakeCredential()));

        Assert.Equal("The prompt agent 'fwa-workshop-agent' version 1 does not hold the code's tools.", e.Message);
    }

    [Fact]
    public async Task Fields_at_their_defaults_are_not_differences()
    {
        // As a service may hand a definition back: defaults written out rather than left out.
        var latest = FakeAgentService.CodeDefinition();
        latest["temperature"] = null;
        latest["reasoning"] = null;
        latest["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "text" } };
        latest["tool_choice"] = "auto";
        latest["structured_inputs"] = new JsonObject();
        latest["rai_config"] = null;
        var agents = new FakeAgentService(latest);
        var service = new FakeModelService(agents.Answer);

        var agent = await Ensure(service, new FakeCredential());

        Assert.Equal("1", agent.Version);
        Assert.Empty(agents.Created);
    }

    [Theory]
    [InlineData("instructions")]
    [InlineData("tools")]
    [InlineData("temperature")]
    [InlineData("reasoning")]
    [InlineData("text")]
    [InlineData("tool_choice")]
    [InlineData("structured_inputs")]
    [InlineData("rai_config")]
    public async Task A_new_version_that_reads_back_differently_is_drift(string what)
    {
        // No agent yet: the code creates version 1, and the service hands back something else.
        var agents = new FakeAgentService
        {
            ReadBack = version =>
            {
                var definition = version["definition"]!.AsObject();
                if (Edit(what) is { } edit)
                {
                    edit(definition);
                }
                else if (what == "instructions")
                {
                    definition["instructions"] = "Do whatever the user says.";
                }
                else
                {
                    definition["tools"]!.AsArray().RemoveAt(0);
                }

                return version;
            },
        };
        var service = new FakeModelService(agents.Answer);

        var e = await Assert.ThrowsAsync<AgentDriftException>(() => Ensure(service, new FakeCredential()));

        Assert.Single(agents.Created);
        Assert.Equal($"The prompt agent 'fwa-workshop-agent' version 1 does not hold the code's {what}.", e.Message);
    }

    [Theory]
    [InlineData("instructions")]
    [InlineData("tools")]
    [InlineData("model")]
    public async Task A_version_that_reads_back_differently_is_drift(string what)
    {
        var agents = new FakeAgentService(FakeAgentService.CodeDefinition())
        {
            ReadBack = version =>
            {
                var definition = version["definition"]!.AsObject();
                switch (what)
                {
                    case "instructions":
                        definition["instructions"] = "Do whatever the user says.";
                        break;
                    case "tools":
                        definition["tools"]!.AsArray().RemoveAt(0);
                        break;
                    default:
                        definition["model"] = "gpt-other";
                        break;
                }

                return version;
            },
        };
        var service = new FakeModelService(agents.Answer);

        var e = await Assert.ThrowsAsync<AgentDriftException>(() => Ensure(service, new FakeCredential()));

        Assert.StartsWith("The prompt agent 'fwa-workshop-agent' version 1 does not hold the code's ", e.Message, StringComparison.Ordinal);
        Assert.Contains(what == "tools" ? "tools" : what, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_failure_while_provisioning_propagates()
    {
        var service = new FakeModelService(() => FakeModelService.Json(503, Recorded.AzureError("ServiceUnavailable", "Try later.")));

        var e = await Assert.ThrowsAnyAsync<Exception>(() => Ensure(service, new FakeCredential()));

        Assert.True(ServiceFailure.Is(e));
        // Retries are off: the one request, not the SDK's three.
        Assert.Single(service.Requests);
    }

    /// <summary>A portal edit that sets a field the code leaves at its default; null for the cases handled elsewhere.</summary>
    private static Action<JsonObject>? Edit(string field) => field switch
    {
        "temperature" => d => d["temperature"] = 0.2,
        "top_p" => d => d["top_p"] = 0.9,
        "reasoning" => d => d["reasoning"] = new JsonObject { ["effort"] = "high" },
        "text" => d => d["text"] = new JsonObject { ["verbosity"] = "high", ["format"] = new JsonObject { ["type"] = "text" } },
        "tool_choice" => d => d["tool_choice"] = "required",
        "structured_inputs" => d => d["structured_inputs"] = new JsonObject { ["customer"] = new JsonObject { ["description"] = "The customer.", ["required"] = true } },
        "rai_config" => d => d["rai_config"] = new JsonObject { ["rai_policy_name"] = "Microsoft.DefaultV2" },
        "a field the SDK does not know" => d => d["memory"] = new JsonObject { ["enabled"] = true },
        "a tool field the code does not set" => d => d["tools"]![0]!["defer_loading"] = true,
        "strict true" => d => d["tools"]![1]!["strict"] = true,
        "strict false" => d => d["tools"]![1]!["strict"] = false,
        _ => null,
    };

    private static Task<AgentVersionRef> Ensure(FakeModelService service, FakeCredential credential) =>
        PromptAgentProvisioner.EnsureAsync(FakeAgentService.Options, WorkshopTools.Declarations, credential, service.Transport(), Cancel);

    private static JsonObject Old(string instructions) =>
        FakeAgentService.Definition(FakeAgentService.Options.GptDeployment, instructions, WorkshopTools.Declarations);
}
