namespace Workshop.Agent.Engines;

/// <summary>How a run ended, the model's last reply when it completed, and every model call it answered.</summary>
/// <param name="Outcome">One of the <see cref="EngineOutcome"/> values.</param>
/// <param name="FinalReply">
/// The model's reply when the outcome is <see cref="EngineOutcome.Completed"/>; the partial text it
/// wrote before the limit cut it off when <see cref="EngineOutcome.Truncated"/>; otherwise null.
/// </param>
/// <param name="Calls">The model calls the model answered, in order, whatever the outcome.</param>
/// <param name="Error">
/// For <see cref="EngineOutcome.EngineError"/> and <see cref="EngineOutcome.ServiceError"/>, the
/// failure's exception type and message; for a run the runner ended at its backstop, why; otherwise null.
/// </param>
public sealed record EngineResult(string Outcome, string? FinalReply, IReadOnlyList<ModelCall> Calls, string? Error = null);

/// <summary>The ways a run can end (spec §4.4: a limit hit is the run's outcome).</summary>
public static class EngineOutcome
{
    /// <summary>The model gave its final reply.</summary>
    public const string Completed = "completed";

    /// <summary>The model asked for a tool call past the budget.</summary>
    public const string ToolLimit = "tool_limit";

    /// <summary>The run's time limit passed.</summary>
    public const string TimeLimit = "time_limit";

    /// <summary>The model's answer, or the request itself, was blocked by the content filter.</summary>
    public const string ContentFiltered = "content_filtered";

    /// <summary>
    /// The model's last answer stopped at the output-token limit (<see cref="AgentSettings.MaxOutputTokens"/>,
    /// finish reason <c>length</c>): it gave no whole reply, so the run is not completed. Its partial
    /// text is kept as the final reply, for the write-up.
    /// </summary>
    public const string Truncated = "truncated";

    /// <summary>The model kept throttling past the wait budget.</summary>
    public const string Throttled = "throttled";

    /// <summary>
    /// The model's service failed: its network, its credentials, or its server (401, 403 or 5xx).
    /// Not the model's doing, so the runner makes the run an infrastructure error (spec §5.4).
    /// </summary>
    public const string ServiceError = "service_error";

    /// <summary>The model call, or the loop around it, failed in any other way.</summary>
    public const string EngineError = "engine_error";
}
