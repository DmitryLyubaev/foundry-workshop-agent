using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Workshop.Agent.Engines;

/// <summary>What the scripted model was sent on one call: kept so a test can see what the model saw.</summary>
internal sealed record ScriptedRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

/// <summary>
/// The <c>fake</c> engine's model: each call takes the script's next step, whatever it was sent.
/// Every answer costs a fixed 1,000 input and 50 output tokens. A throttle step throws
/// <see cref="ThrottledException"/>; a script with no step left throws, which the engine reports
/// as <see cref="EngineOutcome.EngineError"/>.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    public const long InputTokensPerResponse = 1_000;

    public const long OutputTokensPerResponse = 50;

    private readonly Script script;
    private readonly Lock gate = new();
    private readonly List<ScriptedRequest> requests = [];
    private int next;

    public ScriptedChatClient(Script script)
    {
        ArgumentNullException.ThrowIfNull(script);
        this.script = script;
    }

    /// <summary>Every call so far, in order, including those a throttle step refused.</summary>
    internal IReadOnlyList<ScriptedRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Next(messages, options));
        }
        catch (Exception e)
        {
            // As a model's client would: a failed call is a faulted task, not a throw on the call.
            return Task.FromException<ChatResponse>(e);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // Nothing to release: the script is in memory.
    }

    private ChatResponse Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ScriptStep step;
        int number;
        lock (gate)
        {
            requests.Add(new ScriptedRequest([.. messages], options));
            if (next >= script.Steps.Count)
            {
                throw new InvalidOperationException($"The script has no step left: all {script.Steps.Count} are used.");
            }

            step = script.Steps[next];
            number = ++next;
        }

        return step switch
        {
            ScriptStep.Call call => Respond(
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(CallId(number), call.Tool, call.Args.ToDictionary(a => a.Key, a => (object?)a.Value))]),
                ChatFinishReason.ToolCalls),
            ScriptStep.Reply reply => Respond(new ChatMessage(ChatRole.Assistant, reply.Text), ChatFinishReason.Stop),
            ScriptStep.Filter => Respond(new ChatMessage(ChatRole.Assistant, []), ChatFinishReason.ContentFilter),
            ScriptStep.Throttle throttle => throw new ThrottledException(throttle.RetryAfter),
            _ => throw new InvalidOperationException($"The script's step {number} is of an unknown kind."),
        };
    }

    private static string CallId(int step) => "call-" + step.ToString(CultureInfo.InvariantCulture);

    private static ChatResponse Respond(ChatMessage message, ChatFinishReason finish) => new(message)
    {
        FinishReason = finish,
        Usage = new UsageDetails
        {
            InputTokenCount = InputTokensPerResponse,
            OutputTokenCount = OutputTokensPerResponse,
            TotalTokenCount = InputTokensPerResponse + OutputTokensPerResponse,
        },
    };
}
