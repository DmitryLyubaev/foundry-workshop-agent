using Microsoft.Extensions.AI;

namespace Workshop.Agent.Engines;

/// <summary>
/// Runs one task with a model and the given tools. Every engine is a <see cref="ChatClientEngine"/>:
/// <c>fake</c> over <see cref="ScriptedChatClient"/>, <c>gpt</c> over the Foundry prompt agent
/// (<see cref="Foundry.GptEngineFactory"/>) and <c>claude</c> over Claude in Foundry
/// (<see cref="Claude.ClaudeEngineFactory"/>).
/// </summary>
public interface IAgentEngine
{
    /// <summary>The engine's name, such as <c>fake</c>.</summary>
    string Name { get; }

    /// <summary>The model it runs, as the transcript names it.</summary>
    string Model { get; }

    /// <summary>The Foundry deployment it calls, for the transcript; null when it calls none.</summary>
    string? Deployment => null;

    /// <summary>The prompt agent version it runs, for the transcript; null when it runs none.</summary>
    string? AgentVersion => null;

    /// <summary>
    /// Runs the task to one of the <see cref="EngineOutcome"/>s, ending at the run's time limit
    /// (<see cref="Runner.EngineRun.TimeLimit"/>) with <see cref="EngineOutcome.TimeLimit"/>. A failure
    /// of the app's endpoint, thrown by a tool, is not an outcome: it propagates, for the runner to
    /// report as an infrastructure error. Cancellation by <paramref name="ct"/> propagates too.
    /// </summary>
    Task<EngineResult> RunAsync(string task, IReadOnlyList<AIFunction> tools, CancellationToken ct);
}
