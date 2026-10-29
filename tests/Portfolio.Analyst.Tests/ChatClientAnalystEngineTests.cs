using System.Text.Json;
using Workshop.Agent.Engines;

namespace Portfolio.Analyst.Tests;

/// <summary><see cref="ChatClientAnalystEngine"/>: one question, one fresh request, one answer.</summary>
public sealed class ChatClientAnalystEngineTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_answer_carries_its_text_its_tokens_and_its_raw_output()
    {
        var engine = new ChatClientAnalystEngine(Frozen.Scripted("p2 spent A$1,234.56."));

        var answer = await engine.AskAsync("How much?", Cancel);

        Assert.Equal("p2 spent A$1,234.56.", answer.Text);
        Assert.Equal(0, answer.ToolCalls);
        Assert.Equal(ScriptedChatClient.InputTokensPerResponse, answer.InputTokens);
        Assert.Equal(ScriptedChatClient.OutputTokensPerResponse, answer.OutputTokens);
        Assert.Equal("stop", answer.RawOutput["finish_reason"]!.GetValue<string>());
        Assert.Equal("p2 spent A$1,234.56.", answer.RawOutput["items"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_request_for_a_client_side_tool_is_a_failure_not_an_answer()
    {
        // The prompt agent's one tool runs in the service: a function call left to the client means the agent is not the code's.
        var script = new Script([new ScriptStep.Call("portfolio-data", new Dictionary<string, JsonElement>())]);
        var engine = new ChatClientAnalystEngine(new ScriptedChatClient(script));

        var e = await Assert.ThrowsAsync<AnalystEngineException>(() => engine.AskAsync("How much?", Cancel));

        Assert.Equal("tool_calls", e.Code);
    }

    [Fact]
    public async Task A_filtered_answer_is_recorded_empty()
    {
        // The content filter's refusal is the model's outcome: an unreadable answer, which the scorer marks wrong.
        var engine = new ChatClientAnalystEngine(new ScriptedChatClient(new Script([new ScriptStep.Filter()])));

        var answer = await engine.AskAsync("How much?", Cancel);

        Assert.Equal("", answer.Text);
        Assert.Equal("content_filter", answer.RawOutput["finish_reason"]!.GetValue<string>());
    }
}
