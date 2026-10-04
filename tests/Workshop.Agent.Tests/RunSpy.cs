using System.Diagnostics;
using Microsoft.Extensions.AI;
using Workshop.Agent.Engines;
using Workshop.Agent.Runner;
using Workshop.Agent.Scenarios;

namespace Workshop.Agent.Tests;

/// <summary>What an engine saw of its run when it started: the app's process, its port and the run's directory.</summary>
internal sealed record SeenRun(Process App, int Port, string Directory);

/// <summary>
/// Wraps an engine to keep what it saw of each run, and, after the engine, optionally acts on the
/// app behind the tools' back, as an engine that skipped the gate would.
/// </summary>
internal sealed class SpyEngine(EngineRun run, IAgentEngine inner, List<SeenRun> seen, Func<EngineRun, CancellationToken, Task>? after = null) : IAgentEngine
{
    public string Name => inner.Name;

    public string Model => inner.Model;

    public async Task<EngineResult> RunAsync(string task, IReadOnlyList<AIFunction> tools, CancellationToken ct)
    {
        var app = run.App ?? throw new InvalidOperationException("The engine ran before the app was up.");
        lock (seen)
        {
            seen.Add(new SeenRun(Process.GetProcessById(app.ProcessId), app.Port, run.Directory));
        }

        var result = await inner.RunAsync(task, tools, ct);
        if (after is not null)
        {
            await after(run, ct);
        }

        return result;
    }
}

/// <summary>An engine that runs its body in place of a model, for the failures a model cannot cause.</summary>
internal sealed class BodyEngine(Func<CancellationToken, Task<EngineResult>> body) : IAgentEngine
{
    public string Name => "body";

    public string Model => "none";

    public Task<EngineResult> RunAsync(string task, IReadOnlyList<AIFunction> tools, CancellationToken ct) => body(ct);
}

/// <summary>A model call that never answers until it is cancelled.</summary>
internal sealed class HangingModel : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new UnreachableException();
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>An approval gate that never answers until it is cancelled: a person who never comes back.</summary>
internal sealed class HangingGate : Tools.IApprovalGate
{
    public async Task<bool> ApproveAsync(string buttonId, string label, string screenId, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new UnreachableException();
    }
}

/// <summary>The committed scenarios and their scripts, read once.</summary>
internal static class ScenarioSet
{
    private static readonly Lazy<Scenario[]> All = new(() => ScenarioLoader.LoadAll(RepoPaths.Scenarios));

    public static Scenario Get(string id) => All.Value.Single(s => s.Id == id);

    public static IEnumerable<string> Ids => All.Value.Select(s => s.Id);

    /// <summary><c>Scripts/&lt;id&gt;.&lt;kind&gt;.json</c>, where kind is <c>correct</c> or <c>wrong</c>.</summary>
    public static Script ScriptFor(string id, string kind) => Script.Load(Path.Combine(RepoPaths.Scripts, $"{id}.{kind}.json"));
}
