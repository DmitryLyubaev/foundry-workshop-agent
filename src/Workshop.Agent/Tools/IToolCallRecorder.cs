using System.Text.Json;

namespace Workshop.Agent.Tools;

/// <summary>
/// Takes the tool calls the engine answers itself, so the transcript holds every call the model
/// made (spec §4.6), in order with the others. <see cref="WorkshopTools"/> keeps them in its
/// <see cref="WorkshopTools.Records"/>.
/// </summary>
public interface IToolCallRecorder
{
    /// <summary>A call to a tool that does not exist, answered <c>bad_arguments</c>, or <c>tool_limit</c> past the budget.</summary>
    void RecordUnknown(string tool, JsonElement arguments, string outcome, string message, double ms);
}
