using System.Diagnostics;

namespace Workshop.Agent.Telemetry;

/// <summary>
/// The agent's trace source, <c>Workshop.Agent</c>. Each tool call is a <c>tool.execute</c> span,
/// tagged <c>tool.name</c>, <c>tool.outcome</c> and, when the approval gate was asked,
/// <c>tool.approved</c>.
/// </summary>
public static class AgentTelemetry
{
    public const string SourceName = "Workshop.Agent";

    public const string ToolExecute = "tool.execute";

    public const string ToolName = "tool.name";

    public const string ToolOutcome = "tool.outcome";

    public const string ToolApproved = "tool.approved";

    public static ActivitySource Source { get; } = new(SourceName);
}
