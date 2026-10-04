using Microsoft.Extensions.AI;

namespace Workshop.Agent.Engines;

/// <summary>
/// Runs one task with a model and the given tools. Plan 2's only engine is <c>fake</c>, a
/// <see cref="ChatClientEngine"/> over <see cref="ScriptedChatClient"/>; plan 3 adds GPT and
/// Claude behind this same interface.
/// </summary>
public interface IAgentEngine
{
    /// <summary>The engine's name, such as <c>fake</c>.</summary>
    string Name { get; }

    /// <summary>The model it runs, as the transcript names it.</summary>
    string Model { get; }

    /// <summary>
    /// Runs the task to one of the <see cref="EngineOutcome"/>s. A failure of the app's endpoint,
    /// thrown by a tool, is not an outcome: it propagates, for the runner to report as an
    /// infrastructure error. Cancellation by <paramref name="ct"/> propagates too.
    /// </summary>
    Task<EngineResult> RunAsync(string task, IReadOnlyList<AIFunction> tools, CancellationToken ct);
}
