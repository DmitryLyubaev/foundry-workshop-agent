using System.Diagnostics;

namespace Workshop.Agent.Telemetry;

/// <summary>
/// The agent's trace source, <c>Workshop.Agent</c> (spec §4.7). One <c>scenario.run</c> span
/// holds a run; inside it, each model call is a <c>model.call</c> span and each tool call a
/// <c>tool.execute</c> span, tagged <c>tool.name</c>, <c>tool.outcome</c> and, when the approval
/// gate was asked, <c>tool.approved</c>.
/// </summary>
public static class AgentTelemetry
{
    public const string SourceName = "Workshop.Agent";

    public const string ScenarioRun = "scenario.run";

    public const string ScenarioId = "scenario.id";

    public const string ScenarioPass = "scenario.pass";

    public const string EngineName = "engine.name";

    public const string EngineModel = "engine.model";

    public const string ModelCall = "model.call";

    public const string ModelName = "model.name";

    /// <summary>The call's place in the run, from 1, as in the transcript's model calls.</summary>
    public const string ModelCallIndex = "model.call.index";

    public const string ModelInputTokens = "model.input_tokens";

    public const string ModelOutputTokens = "model.output_tokens";

    public const string ModelFinishReason = "model.finish_reason";

    public const string ToolExecute = "tool.execute";

    public const string ToolName = "tool.name";

    public const string ToolOutcome = "tool.outcome";

    public const string ToolApproved = "tool.approved";

    public static ActivitySource Source { get; } = new(SourceName);

    /// <summary>
    /// Starts the span one scenario run is traced in; the runner runs the engine inside it, so the
    /// model calls and tool calls nest under it. Null when nothing listens.
    /// </summary>
    public static Activity? StartScenarioRun(string scenarioId, int pass, string engine, string model)
    {
        var run = Source.StartActivity(ScenarioRun);
        run?.SetTag(ScenarioId, scenarioId);
        run?.SetTag(ScenarioPass, pass);
        run?.SetTag(EngineName, engine);
        run?.SetTag(EngineModel, model);
        return run;
    }
}
