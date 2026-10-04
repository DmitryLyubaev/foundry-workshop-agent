using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Workshop.Agent.Engines;
using Workshop.Agent.Telemetry;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests;

/// <summary>The trace: one <c>scenario.run</c> span holding each <c>model.call</c> and <c>tool.execute</c>.</summary>
public sealed class TelemetryTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spans_for_run_model_and_tool_with_tags()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "call": "describe_screen", "args": {} },
              { "call": "no_such_tool", "args": {} },
              { "reply": "Done." }
            ]
            """));
        var budget = new ToolBudget();
        var describe = AIFunctionFactory.Create(() => Traced("describe_screen"), "describe_screen");
        var engine = new ChatClientEngine("fake", "scripted", model, budget, TimeSpan.FromMinutes(5));

        ActivityTraceId trace;
        using (var run = AgentTelemetry.StartScenarioRun("s01", 2, engine.Name, engine.Model))
        {
            Assert.NotNull(run);
            trace = run.TraceId;
            var result = await engine.RunAsync("Look.", [describe], Cancel);
            Assert.Equal(EngineOutcome.Completed, result.Outcome);
        }

        // Other tests may trace at the same time: only this run's trace counts.
        var spans = stopped.Where(a => a.TraceId == trace).ToArray();

        var scenario = Assert.Single(spans, a => a.OperationName == AgentTelemetry.ScenarioRun);
        Assert.Null(scenario.Parent);
        Assert.Equal("s01", scenario.GetTagItem(AgentTelemetry.ScenarioId));
        Assert.Equal(2, scenario.GetTagItem(AgentTelemetry.ScenarioPass));
        Assert.Equal("fake", scenario.GetTagItem(AgentTelemetry.EngineName));
        Assert.Equal("scripted", scenario.GetTagItem(AgentTelemetry.EngineModel));

        var calls = spans.Where(a => a.OperationName == AgentTelemetry.ModelCall).OrderBy(a => a.StartTimeUtc).ToArray();
        Assert.Equal(3, calls.Length);
        Assert.All(calls, c =>
        {
            Assert.Equal(scenario.SpanId, c.ParentSpanId);
            Assert.Equal("scripted", c.GetTagItem(AgentTelemetry.ModelName));
            Assert.Equal(1000L, c.GetTagItem(AgentTelemetry.ModelInputTokens));
            Assert.Equal(50L, c.GetTagItem(AgentTelemetry.ModelOutputTokens));
        });
        Assert.Equal([1, 2, 3], calls.Select(c => c.GetTagItem(AgentTelemetry.ModelCallIndex)));
        Assert.Equal(["tool_calls", "tool_calls", "stop"], calls.Select(c => c.GetTagItem(AgentTelemetry.ModelFinishReason)));

        var tools = spans.Where(a => a.OperationName == AgentTelemetry.ToolExecute).OrderBy(a => a.StartTimeUtc).ToArray();
        Assert.Equal(2, tools.Length);
        Assert.All(tools, t => Assert.Equal(scenario.SpanId, t.ParentSpanId));
        Assert.Equal(["describe_screen", "no_such_tool"], tools.Select(t => t.GetTagItem(AgentTelemetry.ToolName)));
        Assert.Equal(["ok", "bad_arguments"], tools.Select(t => t.GetTagItem(AgentTelemetry.ToolOutcome)));
        Assert.Equal([true, null], tools.Select(t => t.GetTagItem(AgentTelemetry.ToolApproved)));
    }

    /// <summary>A stand-in for a tool that traces as the real ones do, with an approval the gate gave.</summary>
    private static string Traced(string name)
    {
        using var span = AgentTelemetry.Source.StartActivity(AgentTelemetry.ToolExecute);
        span?.SetTag(AgentTelemetry.ToolName, name);
        span?.SetTag(AgentTelemetry.ToolOutcome, "ok");
        span?.SetTag(AgentTelemetry.ToolApproved, true);
        return """{"outcome":"ok"}""";
    }
}
