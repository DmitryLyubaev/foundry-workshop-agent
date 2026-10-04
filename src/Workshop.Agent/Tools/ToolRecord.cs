using System.Text.Json;

namespace Workshop.Agent.Tools;

/// <summary>
/// One tool call, as the transcript keeps it.
/// </summary>
/// <param name="Index">The call's place in the run, from 1.</param>
/// <param name="Tool">The tool's name, such as <c>press_button</c>.</param>
/// <param name="Arguments">The arguments as the model sent them, as a JSON object.</param>
/// <param name="Outcome">The app's outcome, or the tool's own: <c>denied</c>, <c>not_found</c> for a button not on the screen, <c>bad_arguments</c>, <c>tool_limit</c> or <c>error</c>.</param>
/// <param name="Message">The outcome's message, if it has one.</param>
/// <param name="ScreenId">The current screen after the call, when the call saw it.</param>
/// <param name="Ms">How long the call took, in milliseconds.</param>
/// <param name="Approved">The gate's answer for a destructive button, kept even when the press then failed; null when the gate was not asked.</param>
public sealed record ToolRecord(int Index, string Tool, JsonElement Arguments, string Outcome, string? Message, string? ScreenId, double Ms, bool? Approved);
