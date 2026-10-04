using System.Text.Json;

namespace Workshop.Agent.Tools;

/// <summary>
/// Takes the tool calls the engine answers itself, so the transcript holds every call the model
/// made (spec §4.6), in order with the others; and hears of each model call as it answers, so each
/// tool call can name the turn that asked for it. <see cref="WorkshopTools"/> keeps them in its
/// <see cref="WorkshopTools.Records"/>.
/// </summary>
public interface IToolCallRecorder
{
    /// <summary>
    /// A call to a tool that does not exist, answered <c>bad_arguments</c>, or <c>tool_limit</c> past
    /// the budget; <paramref name="result"/> is the JSON text the model was given.
    /// </summary>
    void RecordUnknown(string tool, JsonElement arguments, string outcome, string message, double ms, string result);

    /// <summary>
    /// The model call <paramref name="index"/> (its <see cref="Engines.ModelCall.Index"/>) has
    /// answered: the tool calls from now until the next one are those it asked for.
    /// </summary>
    void ModelCallAnswered(int index);
}
