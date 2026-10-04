using Workshop.Agent.Runner;

namespace Workshop.Agent.Engines;

/// <summary>
/// The <c>fake</c> engine: <see cref="ChatClientEngine"/> over <see cref="ScriptedChatClient"/>,
/// with the run's budget, its recorder and its time limit, 5 minutes in a study run (spec §4.4). Offline and free.
/// </summary>
public static class FakeEngine
{
    public const string Name = "fake";

    /// <summary>The model's name in the transcript: the script stands in for a model.</summary>
    public const string Model = "scripted";

    public static ChatClientEngine Create(Script script, EngineRun run)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(run);
        return new ChatClientEngine(Name, Model, new ScriptedChatClient(script), run.Budget, run.TimeLimit, run);
    }
}
