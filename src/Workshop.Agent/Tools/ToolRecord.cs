using System.Text.Json;

namespace Workshop.Agent.Tools;

/// <summary>
/// One tool call, as the transcript keeps it.
/// </summary>
/// <param name="Index">The call's place in the run, from 1.</param>
/// <param name="ModelCallIndex">
/// The <see cref="Engines.ModelCall.Index"/> of the model call whose answer asked for this call, so
/// a transcript shows each turn's tool calls; null when no engine reported its model calls.
/// </param>
/// <param name="Tool">The tool's name, such as <c>press_button</c>.</param>
/// <param name="Arguments">The arguments as the model sent them, as a JSON object.</param>
/// <param name="Outcome">
/// The app's outcome, or the tool's own: <c>denied</c>, <c>not_found</c> for a button not on the
/// screen, <c>bad_arguments</c>, <c>tool_limit</c>, <c>error</c> when the app or the connection
/// failed, or <c>cancelled</c> when the run ended while the call was running.
/// </param>
/// <param name="Message">The outcome's message, if it has one.</param>
/// <param name="ScreenId">The current screen after the call, when the call saw it.</param>
/// <param name="Ms">How long the call took, in milliseconds.</param>
/// <param name="Approved">The gate's answer for a destructive button, kept even when the press then failed; null when the gate was not asked.</param>
/// <param name="Result">
/// The JSON text the model was given for this call, exactly as sent, so the transcript alone shows
/// what the model saw (spec §4.6); null when the call gave the model nothing, as for
/// <c>error</c> and <c>cancelled</c>.
/// </param>
public sealed record ToolRecord(int Index, int? ModelCallIndex, string Tool, JsonElement Arguments, string Outcome, string? Message, string? ScreenId, double Ms, bool? Approved, string? Result);
