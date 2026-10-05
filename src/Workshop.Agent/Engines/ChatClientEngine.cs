using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Workshop.Agent.Telemetry;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Engines;

/// <summary>
/// Runs a task on Agent Framework's <see cref="ChatClientAgent"/>, with
/// <see cref="AgentInstructions.Text"/>, <see cref="AgentSettings"/> and the tools. Under the agent, each run builds:
/// <see cref="ToolLoopChatClient"/> (function invocation, with the study's rules) around
/// <see cref="RecordingChatClient"/> around <see cref="ThrottleRetryChatClient"/> around the model.
/// Around the agent, Agent Framework's <see cref="OpenTelemetryAgent"/> traces the run.
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

    /// <summary>The Foundry deployment the engine calls, for the transcript; null for an engine with none.</summary>
    public string? Deployment { get; init; }

    /// <summary>The prompt agent version the engine runs, for the transcript; null for an engine that has none.</summary>
    public string? AgentVersion { get; init; }

    public async Task<EngineResult> RunAsync(string task, IReadOnlyList<AIFunction> tools, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentNullException.ThrowIfNull(tools);

        // Built per run: the throttling budget and the call records belong to one run.
        // The recorder hears of each answer before the loop runs the tool calls it asks for.
        var recording = new RecordingChatClient(new ThrottleRetryChatClient(inner, throttleBudget, delay), Model, recorder is null ? null : recorder.ModelCallAnswered);
        // Not disposed: a delegating client disposes its inner one, and the model's client is the caller's.
        var loop = new ToolLoopChatClient(recording, budget, recorder);
        var chatAgent = new ChatClientAgent(loop, new ChatClientAgentOptions
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

        // Agent Framework's span for the run (invoke_agent, on TraceExport.AgentFrameworkSource), when a
        // trace export listens. The messages go in it only when the export asked for them (--trace-content).
        // Disposing it leaves the loop and the model's client as they were.
        using var agent = new OpenTelemetryAgent(chatAgent) { EnableSensitiveData = TraceExport.CaptureContent };

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
            // was whole, not because the model was done, so the run is not completed. What it wrote
            // is kept, for the write-up, as the partial text it is.
            return finish == ChatFinishReason.Length.Value
                ? new EngineResult(EngineOutcome.Truncated, LastAnswer(response), calls)
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
        catch (ContentFilteredException) when (!ct.IsCancellationRequested)
        {
            // The service's content filter refused the request itself (an HTTP 400), rather than
            // stopping an answer: the same outcome as a filtered answer.
            return new EngineResult(EngineOutcome.ContentFiltered, null, recording.Calls);
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
            Redaction.AddException(Activity.Current, e);
            return new EngineResult(EngineOutcome.ServiceError, null, recording.Calls, Describe(e));
        }
        catch (Exception e) when (!ct.IsCancellationRequested && !loop.IsToolFailure(e))
        {
            // The model or the loop failed; a tool's failure and the caller's cancellation go on as they were thrown.
            Redaction.AddException(Activity.Current, e);
            return new EngineResult(EngineOutcome.EngineError, null, recording.Calls, Describe(e));
        }
    }

    // Redacted: a service's error text names principals, projects and hosts, and Error reaches the transcript and the console.
    private static string Describe(Exception e) => Redaction.Describe(e);

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
