using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Engines;

/// <summary>
/// Runs a task on Agent Framework's <see cref="ChatClientAgent"/>, with
/// <see cref="AgentInstructions.Text"/>, <see cref="AgentSettings"/> and the tools. Under the agent, each run builds:
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
            // The same instructions and settings for every engine: the study compares the models, nothing else.
            ChatOptions = new ChatOptions
            {
                Instructions = AgentInstructions.Text,
                Tools = [.. tools],
                MaxOutputTokens = AgentSettings.MaxOutputTokens,
                Temperature = AgentSettings.Temperature,
            },
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
            var finish = calls.Count > 0 ? calls[^1].FinishReason : null;
            if (finish == ChatFinishReason.ContentFilter.Value)
            {
                return new EngineResult(EngineOutcome.ContentFiltered, null, calls);
            }

            // The last answer stopped at the output-token limit: the loop ended because no tool call
            // was whole, not because the model was done, so what it wrote is not its reply.
            return finish == ChatFinishReason.Length.Value
                ? new EngineResult(EngineOutcome.Truncated, null, calls)
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
        catch (Exception e) when (!ct.IsCancellationRequested && !loop.IsToolFailure(e) && ServiceFailure.Is(e))
        {
            // The model's service failed, not the model: the runner drops the run as infrastructure,
            // keeping the calls the model answered, so their tokens are still counted.
            Activity.Current?.AddException(e);
            return new EngineResult(EngineOutcome.ServiceError, null, recording.Calls, Describe(e));
        }
        catch (Exception e) when (!ct.IsCancellationRequested && !loop.IsToolFailure(e))
        {
            // The model or the loop failed; a tool's failure and the caller's cancellation go on as they were thrown.
            Activity.Current?.AddException(e);
            return new EngineResult(EngineOutcome.EngineError, null, recording.Calls, Describe(e));
        }
    }

    private static string Describe(Exception e) => $"{e.GetType().FullName}: {e.Message}";

    /// <summary>
    /// The model's answer: every assistant message after the last tool result, joined by new lines,
    /// as one answer may come as several messages (the Responses API's message items). Text the
    /// model wrote beside a tool call ("Let me look") comes before that tool's result, so it is not
    /// the reply. Null when the model wrote no text after the last tool result.
    /// </summary>
    private static string? LastAnswer(AgentResponse response)
    {
        var messages = response.Messages;
        var start = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.Tool)
            {
                start = i + 1;
                break;
            }
        }

        var texts = messages.Skip(start)
            .Where(m => m.Role == ChatRole.Assistant && !string.IsNullOrEmpty(m.Text))
            .Select(m => m.Text)
            .ToArray();
        return texts.Length > 0 ? string.Join('\n', texts) : null;
    }
}
