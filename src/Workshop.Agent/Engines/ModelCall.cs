namespace Workshop.Agent.Engines;

/// <summary>One model call the model answered, as the transcript keeps it.</summary>
/// <param name="Index">The call's place in the run, from 1.</param>
/// <param name="InputTokens">The input tokens the model reported, or 0 when it reported none.</param>
/// <param name="OutputTokens">The output tokens the model reported, or 0 when it reported none.</param>
/// <param name="Ms">How long the call took, in milliseconds, including any throttling waits.</param>
/// <param name="FinishReason">Why the model stopped, such as <c>tool_calls</c>, <c>stop</c>, <c>length</c> or <c>content_filter</c>; null when it did not say.</param>
public sealed record ModelCall(int Index, long InputTokens, long OutputTokens, double Ms, string? FinishReason);
