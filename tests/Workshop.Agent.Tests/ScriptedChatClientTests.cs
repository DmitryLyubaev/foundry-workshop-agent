using System.Text.Json;
using Microsoft.Extensions.AI;
using Workshop.Agent.Engines;

namespace Workshop.Agent.Tests;

/// <summary>The scripted fake model: its step shapes, its fixed usage, and a run driven through function invocation.</summary>
public sealed class ScriptedChatClientTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Script_drives_tool_calls_then_reply()
    {
        var script = Script.Parse("""
            [
              { "call": "open_screen", "args": { "screen": "parts" } },
              { "call": "set_field", "args": { "field": "search", "value": "battery" } },
              { "reply": "There are 4 laptop batteries." }
            ]
            """);
        var seen = new List<string>();
        AIFunction[] tools =
        [
            AIFunctionFactory.Create((string screen) => { seen.Add($"open {screen}"); return "opened"; }, "open_screen"),
            AIFunctionFactory.Create((string field, string value) => { seen.Add($"set {field}={value}"); return "set"; }, "set_field"),
        ];
        var model = new ScriptedChatClient(script);
        using var invoking = new FunctionInvokingChatClient(model);

        var response = await invoking.GetResponseAsync("How many laptop batteries?", new ChatOptions { Tools = [.. tools] }, Cancel);

        Assert.Equal(["open parts", "set search=battery"], seen);
        Assert.Equal("There are 4 laptop batteries.", response.Text);
        Assert.Equal(3, model.Requests.Count);

        // The model sees each tool's result before its next step.
        var results = model.Requests[2].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToArray();
        Assert.Equal(2, results.Length);
    }

    [Fact]
    public async Task Each_step_is_one_response_of_1000_in_and_50_out()
    {
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "call": "describe_screen", "args": {} },
              { "filter": true },
              { "reply": "Done." }
            ]
            """));

        var call = await model.GetResponseAsync("go", cancellationToken: Cancel);
        var filtered = await model.GetResponseAsync("go", cancellationToken: Cancel);
        var reply = await model.GetResponseAsync("go", cancellationToken: Cancel);

        var content = Assert.Single(call.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("describe_screen", content.Name);
        Assert.Empty(content.Arguments!);
        Assert.Equal(ChatFinishReason.ToolCalls, call.FinishReason);
        Assert.Equal(ChatFinishReason.ContentFilter, filtered.FinishReason);
        Assert.Equal(ChatFinishReason.Stop, reply.FinishReason);
        Assert.Equal("Done.", reply.Text);
        Assert.All([call, filtered, reply], r =>
        {
            Assert.Equal(1000, r.Usage!.InputTokenCount);
            Assert.Equal(50, r.Usage.OutputTokenCount);
        });
    }

    [Fact]
    public async Task Throttle_step_throws_with_retry_after_and_the_next_call_moves_on()
    {
        var model = new ScriptedChatClient(Script.Parse("""[ { "throttle": 2.5 }, { "reply": "ok" } ]"""));

        var thrown = await Assert.ThrowsAsync<ThrottledException>(() => model.GetResponseAsync("go", cancellationToken: Cancel));
        var reply = await model.GetResponseAsync("go", cancellationToken: Cancel);

        Assert.Equal(TimeSpan.FromSeconds(2.5), thrown.RetryAfter);
        Assert.Equal("ok", reply.Text);
    }

    [Fact]
    public async Task Call_arguments_reach_the_tool_as_the_script_wrote_them()
    {
        var model = new ScriptedChatClient(Script.Parse("""[ { "call": "set_field", "args": { "field": "qty", "value": 3 } } ]"""));

        var response = await model.GetResponseAsync("go", cancellationToken: Cancel);

        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("qty", Assert.IsType<JsonElement>(call.Arguments!["field"]).GetString());
        Assert.Equal(3, Assert.IsType<JsonElement>(call.Arguments["value"]).GetInt32());
        Assert.False(string.IsNullOrEmpty(call.CallId));
    }

    [Fact]
    public async Task A_script_with_no_step_left_throws()
    {
        var model = new ScriptedChatClient(Script.Parse("""[ { "reply": "Done." } ]"""));
        await model.GetResponseAsync("go", cancellationToken: Cancel);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => model.GetResponseAsync("go", cancellationToken: Cancel));

        Assert.Contains("no step left", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "reply": "x" }""", "array")]
    [InlineData("""[ { "shout": "x" } ]""", "step 1")]
    [InlineData("""[ { "reply": "x", "filter": true } ]""", "step 1")]
    [InlineData("""[ { "reply": "x" }, { "call": "", "args": {} } ]""", "step 2")]
    [InlineData("""[ { "call": "open_screen", "args": [] } ]""", "step 1")]
    [InlineData("""[ { "throttle": -1 } ]""", "step 1")]
    [InlineData("""[ { "filter": false } ]""", "step 1")]
    public void Parse_refuses_a_malformed_script(string json, string expected)
    {
        var thrown = Assert.Throws<FormatException>(() => Script.Parse(json));

        Assert.Contains(expected, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_reads_every_step_shape()
    {
        var script = Script.Parse("""
            [
              { "call": "press_button", "args": { "button": "save" } },
              { "call": "describe_screen" },
              { "throttle": 3 },
              { "filter": true },
              { "reply": "Saved." }
            ]
            """);

        Assert.Collection(
            script.Steps,
            s => Assert.Equal("save", Assert.IsType<ScriptStep.Call>(s).Args["button"].GetString()),
            s => Assert.Empty(Assert.IsType<ScriptStep.Call>(s).Args),
            s => Assert.Equal(TimeSpan.FromSeconds(3), Assert.IsType<ScriptStep.Throttle>(s).RetryAfter),
            s => Assert.IsType<ScriptStep.Filter>(s),
            s => Assert.Equal("Saved.", Assert.IsType<ScriptStep.Reply>(s).Text));
    }
}
