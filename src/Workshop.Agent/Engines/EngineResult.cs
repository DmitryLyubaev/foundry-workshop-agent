namespace Workshop.Agent.Engines;

/// <summary>How a run ended, the model's last reply when it completed, and every model call it answered.</summary>
/// <param name="Outcome">One of the <see cref="EngineOutcome"/> values.</param>
/// <param name="FinalReply">The model's reply when the outcome is <see cref="EngineOutcome.Completed"/>; otherwise null.</param>
/// <param name="Calls">The model calls the model answered, in order, whatever the outcome.</param>
public sealed record EngineResult(string Outcome, string? FinalReply, IReadOnlyList<ModelCall> Calls);

/// <summary>The ways a run can end (spec §4.4: a limit hit is the run's outcome).</summary>
public static class EngineOutcome
{
    /// <summary>The model gave its final reply.</summary>
    public const string Completed = "completed";

    /// <summary>The model asked for a tool call past the budget.</summary>
    public const string ToolLimit = "tool_limit";

    /// <summary>The run's time limit passed.</summary>
    public const string TimeLimit = "time_limit";

    /// <summary>The model's answer was blocked by its content filter.</summary>
    public const string ContentFiltered = "content_filtered";

    /// <summary>The model kept throttling past the wait budget.</summary>
    public const string Throttled = "throttled";

    /// <summary>The model call, or the loop around it, failed in any other way.</summary>
    public const string EngineError = "engine_error";
}
