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
    public async Task A_latest_version_that_differs_gets_a_new_version(string difference)
    {
        var latest = FakeAgentService.CodeDefinition();
        var tools = latest["tools"]!.AsArray();
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
            case "temperature":
                latest["temperature"] = 0.2;
                break;
        }

        var agents = new FakeAgentService(latest);
        var service = new FakeModelService(agents.Answer);

        var agent = await Ensure(service, new FakeCredential());

        Assert.Equal("2", agent.Version);
        Assert.Single(agents.Created);
        Assert.Equal(AgentInstructions.Sha256, agent.InstructionsSha256);
        Assert.Equal(ToolSchemas.Sha256(WorkshopTools.Declarations), agent.ToolsSha256);
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

    private static Task<AgentVersionRef> Ensure(FakeModelService service, FakeCredential credential) =>
        PromptAgentProvisioner.EnsureAsync(FakeAgentService.Options, WorkshopTools.Declarations, credential, service.Transport(), Cancel);

    private static JsonObject Old(string instructions) =>
        FakeAgentService.Definition(FakeAgentService.Options.GptDeployment, instructions, WorkshopTools.Declarations);
}
