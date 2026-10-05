using System.Text.Json;
using Microsoft.Extensions.AI;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests.RealEngines;

/// <summary>
/// One definition of "the tools": their names, descriptions and JSON schemas, as canonical JSON and
/// its SHA-256. The prompt agent's version is checked against it, and the freeze records it.
/// </summary>
public sealed class ToolSchemasTests
{
    [Fact]
    public void The_six_declarations_are_the_runs_tools()
    {
        Assert.Equal(
            ["list_screens", "describe_screen", "open_screen", "set_field", "select_row", "press_button"],
            WorkshopTools.Declarations.Select(t => t.Name));

        // A run's tools come from the same code: the hash cannot tell them apart.
        using var client = new Surface.SurfaceClient(1, "unused");
        var runTools = new WorkshopTools(client, new ScriptedGate(false), new ToolBudget()).Functions;
        Assert.Equal(ToolSchemas.Sha256(WorkshopTools.Declarations), ToolSchemas.Sha256(runTools));
        Assert.Matches("^[0-9a-f]{64}$", ToolSchemas.Sha256(runTools));
    }

    [Fact]
    public void Parameters_close_the_object_as_both_SDKs_send_it()
    {
        var open = WorkshopTools.Declarations.Single(t => t.Name == "open_screen");

        var parameters = ToolSchemas.Parameters(open);

        Assert.Equal(JsonValueKind.Object, parameters.ValueKind);
        Assert.False(parameters.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("string", parameters.GetProperty("properties").GetProperty("screen").GetProperty("type").GetString());
        Assert.Equal(["screen"], parameters.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Canonical_json_is_sorted_compact_and_names_each_tool()
    {
        var tools = new[]
        {
            Tool("b_tool", "Second.", (string x) => x),
            Tool("a_tool", "First.", () => "ok"),
        };

        Assert.Equal(
            """[{"description":"First.","name":"a_tool","parameters":{"additionalProperties":false,"properties":{},"type":"object"}},"""
            + """{"description":"Second.","name":"b_tool","parameters":{"additionalProperties":false,"properties":{"x":{"type":"string"}},"required":["x"],"type":"object"}}]""",
            ToolSchemas.CanonicalJson(tools));
    }

    [Fact]
    public void The_hash_ignores_order_layout_and_an_empty_required_list()
    {
        var code = ToolSchemas.Sha256(WorkshopTools.Declarations);

        // As a service may hand the definition back: other order, other key order, white space, "required": [].
        var echoed = WorkshopTools.Declarations.Reverse().Select(t =>
        {
            var schema = JsonSerializer.Serialize(ToolSchemas.Parameters(t), new JsonSerializerOptions { WriteIndented = true });
            var node = System.Text.Json.Nodes.JsonNode.Parse(schema)!.AsObject();
            node.TryAdd("required", new System.Text.Json.Nodes.JsonArray());
            var reordered = new System.Text.Json.Nodes.JsonObject(node.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
            return (t.Name, (string?)t.Description, JsonDocument.Parse(reordered.ToJsonString()).RootElement.Clone());
        });

        Assert.Equal(code, ToolSchemas.Sha256(echoed));
    }

    [Fact]
    public void The_hash_changes_with_a_name_a_description_or_a_schema()
    {
        var code = ToolSchemas.Sha256(WorkshopTools.Declarations);
        var entries = WorkshopTools.Declarations.Select(t => (t.Name, (string?)t.Description, ToolSchemas.Parameters(t))).ToArray();

        var renamed = entries.Select((e, i) => i == 0 ? ("list_all_screens", e.Item2, e.Item3) : e);
        var redescribed = entries.Select((e, i) => i == 0 ? (e.Name, "Lists screens.", e.Item3) : e);
        var reschemed = entries.Select((e, i) => i == 2 ? (e.Name, e.Item2, JsonDocument.Parse("""{"type":"object","properties":{"screen":{"type":"integer"}},"required":["screen"],"additionalProperties":false}""").RootElement) : e);

        Assert.NotEqual(code, ToolSchemas.Sha256(renamed));
        Assert.NotEqual(code, ToolSchemas.Sha256(redescribed));
        Assert.NotEqual(code, ToolSchemas.Sha256(reschemed));
    }

    private static AIFunction Tool(string name, string description, Delegate body) =>
        AIFunctionFactory.Create(body, new AIFunctionFactoryOptions { Name = name, Description = description });
}
