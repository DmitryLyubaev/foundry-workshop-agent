using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Engines;

/// <summary>
/// Runs a task on Agent Framework's <see cref="ChatClientAgent"/>, with
/// <see cref="AgentInstructions.Text"/> and the tools. Under the agent, each run builds:
/// <see cref="ToolLoopChatClient"/> (function invocation, with the study's rules) around
/// <see cref="RecordingChatClient"/> around <see cref="ThrottleRetryChatClient"/> around the model.
/// </summary>
public sealed class ChatClientEngine : IAgentEngine
{
    private readonly IChatClient inner;
    private readonly ToolBudget budget;
    private readonly TimeSpan timeLimit;
    private readonly TimeSpan throttleBudget;
    private readonly Func<TimeSpan, CancellationToken, Task>? delay;
    private readonly IToolCallRecorder? recorder;

    /// <param name="name">The engine's name, such as <c>fake</c>.</param>
    /// <param name="model">The model's name, for the transcript and the trace.</param>
    /// <param name="inner">The model's client.</param>
    /// <param name="budget">The run's tool-call budget, shared with the tools, which take from it.</param>
    /// <param name="timeLimit">How long the run may take before its outcome is <see cref="EngineOutcome.TimeLimit"/>.</param>
    /// <param name="recorder">
    /// Takes the calls the engine answers itself, to tools that do not exist, so the transcript
    /// holds every call: normally the run's <see cref="WorkshopTools"/>. Null keeps them only in the trace.
    /// </param>
    public ChatClientEngine(string name, string model, IChatClient inner, ToolBudget budget, TimeSpan timeLimit, IToolCallRecorder? recorder = null)
        : this(name, model, inner, budget, timeLimit, ThrottleRetryChatClient.DefaultBudget, null, recorder)
    {
    }

    /// <summary>For tests: a throttling wait budget and a way to wait other than the clock's.</summary>
    internal ChatClientEngine(string name, string model, IChatClient inner, ToolBudget budget, TimeSpan timeLimit, TimeSpan throttleBudget, Func<TimeSpan, CancellationToken, Task>? delay, IToolCallRecorder? recorder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeLimit, TimeSpan.Zero);
        Name = name;
        Model = model;
        this.inner = inner;
        this.budget = budget;
        this.timeLimit = timeLimit;
        this.throttleBudget = throttleBudget;
        this.delay = delay;
        this.recorder = recorder;
    }

    public string Name { get; }

    public string Model { get; }

    public async Task<EngineResult> RunAsync(string task, IReadOnlyList<AIFunction> tools, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentNullException.ThrowIfNull(tools);

        // Built per run: the throttling budget and the call records belong to one run.
        // The recorder hears of each answer before the loop runs the tool calls it asks for.
        var recording = new RecordingChatClient(new ThrottleRetryChatClient(inner, throttleBudget, delay), Model, recorder is null ? null : recorder.ModelCallAnswered);
        // Not disposed: a delegating client disposes its inner one, and the model's client is the caller's.
        var loop = new ToolLoopChatClient(recording, budget, recorder);
        var agent = new ChatClientAgent(loop, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions { Instructions = AgentInstructions.Text, Tools = [.. tools] },
            // The loop above is the function invocation; the agent's default one would replace its rules.
            UseProvidedChatClientAsIs = true,
        });

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeLimit);
        try
        {
            // WaitAsync is the backstop: a model client or a tool that ignores the token still ends at the limit.
            var response = await agent.RunAsync(task, cancellationToken: limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            var calls = recording.Calls;
            return calls.Count > 0 && calls[^1].FinishReason == ChatFinishReason.ContentFilter.Value
                ? new EngineResult(EngineOutcome.ContentFiltered, null, calls)
                : new EngineResult(EngineOutcome.Completed, LastAnswer(response), calls);
        }
        catch (ToolLimitReachedException)
        {
            return new EngineResult(EngineOutcome.ToolLimit, null, recording.Calls);
        }
        catch (ThrottledException)
        {
            return new EngineResult(EngineOutcome.Throttled, null, recording.Calls);
        }
        catch (Exception e) when (limit.IsCancellationRequested && !ct.IsCancellationRequested && !loop.IsToolFailure(e))
        {
            // Once the limit has fired, whatever surfaces is the limit's doing: a client may turn
            // cancellation into an exception of its own.
            return new EngineResult(EngineOutcome.TimeLimit, null, recording.Calls);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && !loop.IsToolFailure(e))
        {
            // The model or the loop failed; a tool's failure and the caller's cancellation go on as they were thrown.
            Activity.Current?.AddException(e);
            return new EngineResult(EngineOutcome.EngineError, null, recording.Calls, $"{e.GetType().FullName}: {e.Message}");
        }
    }

    /// <summary>
    /// The model's last answer: the response holds the whole run, and a model may write text
    /// beside a tool call ("Let me look"), which is not its reply.
    /// </summary>
    private static string? LastAnswer(AgentResponse response) =>
        response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text;
}
