using System.Diagnostics;
using Microsoft.Extensions.AI;
using Workshop.Agent.Telemetry;

namespace Workshop.Agent.Engines;

/// <summary>
/// Records each model call the model answers as a <see cref="Engines.ModelCall"/>, and traces
/// every call, answered or not, as a <c>model.call</c> span. It sits above the throttling retry,
/// so one call is one entry however many times it was retried, and its time includes the waits.
/// </summary>
public sealed class RecordingChatClient : DelegatingChatClient
{
    private readonly string model;
    private readonly Action<int>? answered;
    private readonly Lock gate = new();
    private readonly List<ModelCall> calls = [];

    /// <param name="inner">The client it records the calls of.</param>
    /// <param name="model">The model's name, for the trace.</param>
    /// <param name="answered">
    /// Told each answered call's index before the answer goes on, so the tool calls it asks for can
    /// name it; null when nothing needs to know.
    /// </param>
    public RecordingChatClient(IChatClient inner, string model, Action<int>? answered = null)
        : base(inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        this.model = model;
        this.answered = answered;
    }

    /// <summary>The answered calls so far, in order.</summary>
    public IReadOnlyList<ModelCall> Calls
    {
        get
        {
            lock (gate)
            {
                return [.. calls];
            }
        }
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var span = AgentTelemetry.Source.StartActivity(AgentTelemetry.ModelCall);
        span?.SetTag(AgentTelemetry.ModelName, model);
        var timer = Stopwatch.StartNew();

        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            span?.SetStatus(ActivityStatusCode.Error, Redaction.Redact(e.Message));
            throw;
        }

        ModelCall call;
        lock (gate)
        {
            call = new ModelCall(
                calls.Count + 1,
                response.Usage?.InputTokenCount ?? 0,
                response.Usage?.OutputTokenCount ?? 0,
                timer.Elapsed.TotalMilliseconds,
                response.FinishReason?.Value);
            calls.Add(call);
        }

        span?.SetTag(AgentTelemetry.ModelCallIndex, call.Index);
        span?.SetTag(AgentTelemetry.ModelInputTokens, call.InputTokens);
        span?.SetTag(AgentTelemetry.ModelOutputTokens, call.OutputTokens);
        span?.SetTag(AgentTelemetry.ModelFinishReason, call.FinishReason);
        answered?.Invoke(call.Index);
        return response;
    }

    /// <summary>The engine never streams; a stream here would go unrecorded, so it is refused.</summary>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The engine calls the model without streaming.");
}
