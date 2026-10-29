using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace Portfolio.Analyst.Tests;

/// <summary>
/// The <c>portfolio-analyst</c> prompt agent: its instructions and one MCP tool, <c>portfolio-data</c>,
/// that reaches the data agent through the project connection; reused when its latest version holds
/// exactly that, and refused when what is read back does not.
/// </summary>
public sealed class AnalystAgentTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static Task<string> Ensure(FakeFoundry foundry, FakeCredential? credential = null) =>
        AnalystAgent.EnsureAsync(FakeFoundry.Setup, credential ?? new FakeCredential(), foundry.Transport(), Cancel);

    [Fact]
    public void The_definition_is_the_instructions_and_one_mcp_tool_through_the_connection()
    {
        var definition = JsonNode.Parse(ModelReaderWriter.Write(AnalystAgent.Definition(FakeFoundry.Setup)).ToString())!;

        Assert.True(JsonNode.DeepEquals(FakeFoundry.CodeDefinition(), definition), definition.ToJsonString());
    }

    [Fact]
    public async Task A_new_agent_gets_its_first_version_from_the_code()
    {
        var foundry = new FakeFoundry();
        var credential = new FakeCredential();

        var version = await Ensure(foundry, credential);

        Assert.Equal("1", version);
        Assert.Equal(["GET", "POST", "GET"], foundry.Requests.Select(r => r.Method.Method));
        Assert.EndsWith("/api/projects/example/agents/portfolio-analyst/versions", foundry.Requests[1].Path, StringComparison.Ordinal);
        Assert.All(foundry.Requests, r => Assert.Equal("Bearer fake-token", r.Authorization));
        Assert.All(credential.Scopes, s => Assert.Equal("https://ai.azure.com/.default", s));
        Assert.True(JsonNode.DeepEquals(FakeFoundry.CodeDefinition(), Assert.Single(foundry.Created)));
    }

    [Fact]
    public async Task A_latest_version_that_holds_the_code_is_reused()
    {
        var older = FakeFoundry.CodeDefinition();
        older["instructions"] = "Older instructions.";
        var foundry = new FakeFoundry(older, FakeFoundry.CodeDefinition());

        var version = await Ensure(foundry);

        Assert.Equal("2", version);
        Assert.Empty(foundry.Created);
        Assert.Equal(["GET", "GET"], foundry.Requests.Select(r => r.Method.Method));
    }

    public static TheoryData<string> Edits => ["instructions", "model", "server_url", "project_connection_id", "require_approval", "server_label", "headers", "a second tool", "temperature"];

    private static void Edit(JsonObject definition, string edit)
    {
        var tool = definition["tools"]![0]!.AsObject();
        switch (edit)
        {
            case "instructions":
                definition["instructions"] = "Answer from memory.";
                break;
            case "model":
                definition["model"] = "gpt-4.1";
                break;
            case "server_url":
                tool["server_url"] = "https://elsewhere.example.invalid/mcp";
                break;
            case "project_connection_id":
                tool.Remove("project_connection_id");
                break;
            case "require_approval":
                tool["require_approval"] = "always";
                break;
            case "server_label":
                tool["server_label"] = "other";
                break;
            case "headers":
                tool["headers"] = new JsonObject { ["x-extra"] = "1" };
                break;
            case "a second tool":
                definition["tools"]!.AsArray().Add(new JsonObject { ["type"] = "web_search_preview" });
                break;
            default:
                definition["temperature"] = 0.2;
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Edits))]
    public async Task A_latest_version_that_differs_gets_a_new_version(string edit)
    {
        var latest = FakeFoundry.CodeDefinition();
        Edit(latest, edit);
        var foundry = new FakeFoundry(latest);

        Assert.Equal("2", await Ensure(foundry));
        Assert.True(JsonNode.DeepEquals(FakeFoundry.CodeDefinition(), Assert.Single(foundry.Created)));
    }

    [Theory]
    [MemberData(nameof(Edits))]
    public async Task A_version_that_reads_back_differently_refuses_the_run_without_naming_a_value(string edit)
    {
        var foundry = new FakeFoundry
        {
            ReadBack = version =>
            {
                Edit(version["definition"]!.AsObject(), edit);
                return version;
            },
        };

        var e = await Assert.ThrowsAsync<AnalystDriftException>(() => Ensure(foundry));

        Assert.StartsWith("The prompt agent portfolio-analyst version 1 does not hold the code's ", e.Message, StringComparison.Ordinal);
        foreach (var value in new[] { "mcp.example.invalid", "elsewhere", "conn-example", "example.services" })
        {
            Assert.DoesNotContain(value, e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_engine_asks_the_agents_version_one_question_with_nothing_else()
    {
        var foundry = new FakeFoundry(FakeFoundry.CodeDefinition()) { Response = FakeFoundry.ResponseWith("Project p2 spent A$1,234.56.") };
        var engine = AnalystAgent.CreateEngine(FakeFoundry.Setup, "1", new FakeCredential(), foundry.Transport());

        var answer = await engine.AskAsync("How much has project p2 spent?", Cancel);

        var sent = Assert.Single(foundry.Requests).Json;
        Assert.Equal("portfolio-analyst", sent["agent_reference"]?["name"]?.GetValue<string>() ?? sent["agent"]?["name"]?.GetValue<string>());
        var input = Assert.Single(sent["input"]!.AsArray())!;
        Assert.Equal("user", input["role"]!.GetValue<string>());
        Assert.Contains("How much has project p2 spent?", input.ToJsonString(), StringComparison.Ordinal);
        Assert.False(sent.ContainsKey("tools"));
        Assert.False(sent.ContainsKey("instructions"));
        Assert.False(sent.ContainsKey("previous_response_id"));
        Assert.False(sent.ContainsKey("conversation"));

        Assert.Equal("Project p2 spent A$1,234.56.", answer.Text);
        Assert.Equal(1, answer.ToolCalls);
        Assert.Equal(1234, answer.InputTokens);
        Assert.Equal(56, answer.OutputTokens);
    }

    [Fact]
    public async Task Each_mcp_call_counts_and_raw_output_keeps_the_calls()
    {
        var foundry = new FakeFoundry(FakeFoundry.CodeDefinition()) { Response = FakeFoundry.ResponseWith("A$1,234.56", calls: 2) };
        var engine = AnalystAgent.CreateEngine(FakeFoundry.Setup, "1", new FakeCredential(), foundry.Transport());

        var answer = await engine.AskAsync("How much?", Cancel);

        Assert.Equal(2, answer.ToolCalls);
        var items = answer.RawOutput["items"]!.AsArray();
        Assert.Equal(2, items.Count(i => i!["type"]!.GetValue<string>() == "mcp_call"));
        Assert.Contains(items, i => i!["type"]!.GetValue<string>() == "mcp_result" && i.ToJsonString().Contains("A$1,234.56", StringComparison.Ordinal));
        Assert.Equal("A$1,234.56", items[^1]!["text"]!.GetValue<string>());
    }
}
