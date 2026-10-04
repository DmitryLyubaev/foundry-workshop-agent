using System.Text.Json;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Runner;

/// <summary>
/// What one run shares with the engine built for it: the tool-call budget that the run's tools
/// take from, and, as an <see cref="IToolCallRecorder"/>, the place for the calls the engine answers
/// itself, which are the run's tools' records, so the transcript holds every call in order.
/// </summary>
/// <remarks>
/// The runner builds the engine before it starts the app, so a run whose app cannot start still
/// names its engine; the tools, and so the records, exist once the app is up, before the engine runs.
/// </remarks>
public sealed class EngineRun : IToolCallRecorder
{
    private WorkshopTools? tools;

    internal EngineRun(string directory)
    {
        Directory = directory;
    }

    /// <summary>The run's budget: pass it to the engine, which shares it with the tools.</summary>
    public ToolBudget Budget { get; } = new();

    /// <summary>The run's temporary directory: its database, its audit log and its session file.</summary>
    internal string Directory { get; }

    /// <summary>The run's app, once it is up: for tests that act on it behind the tools' back.</summary>
    internal AppProcess? App { get; private set; }

    public void RecordUnknown(string tool, JsonElement arguments, string outcome, string message, double ms) =>
        (tools ?? throw new InvalidOperationException("The run's tools do not exist until its app is up; no engine runs before that."))
            .RecordUnknown(tool, arguments, outcome, message, ms);

    internal void Bind(AppProcess app, WorkshopTools runTools)
    {
        App = app;
        tools = runTools;
    }
}
