using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Engines;

/// <summary>
/// The library's function-invocation loop, with the study's rules where the library's own differ:
/// <list type="bullet">
/// <item>A call to a tool that does not exist is run, in the model's order, as an
/// <see cref="UnknownTool"/>: it takes one from the budget, like any call, is answered
/// <c>bad_arguments</c>, which the model can read (Review Focus 3), and is recorded with the other
/// calls. The library would answer free text, after the batch's other calls.</item>
/// <item>A tool answering <c>tool_limit</c> ends the loop with <see cref="ToolLimitReachedException"/>:
/// the model asked for a call past the budget (Review Focus 4).</item>
/// <item>An exception a tool throws, such as the app's endpoint failing, leaves the loop as it
/// was thrown; the library would hand the model an error text and carry on.</item>
/// </list>
/// The loop runs until the model replies or one of those ends it; the budget and the engine's time
/// limit bound it, so the library's iteration cap, which would end it as if it had completed, is off.
/// </summary>
internal sealed class ToolLoopChatClient : FunctionInvokingChatClient
{
    private const string ToolLimitOutcome = "tool_limit";

    // Tools the loop can run but never offers the model: the unknown tools the model has named.
    private readonly List<AITool> unknownTools = [];
    private readonly ToolBudget budget;
    private readonly IToolCallRecorder? recorder;
    private bool limitReached;
    private Exception? toolFailure;

    public ToolLoopChatClient(IChatClient inner, ToolBudget budget, IToolCallRecorder? recorder)
        : this(new UnknownToolBinder(inner), budget, recorder)
    {
    }

    private ToolLoopChatClient(UnknownToolBinder binder, ToolBudget budget, IToolCallRecorder? recorder)
        : base(binder)
    {
        ArgumentNullException.ThrowIfNull(budget);
        this.budget = budget;
        this.recorder = recorder;
        binder.Loop = this;
        AdditionalTools = unknownTools;
        AllowConcurrentInvocation = false;
        MaximumIterationsPerRequest = int.MaxValue;
        TerminateOnUnknownCalls = false;
    }

    /// <summary>Whether <paramref name="e"/> is the exception a tool threw, which the engine lets go on as it is.</summary>
    public bool IsToolFailure(Exception e) => toolFailure is not null && ReferenceEquals(e, toolFailure);

    protected override async ValueTask<object?> InvokeFunctionAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = await base.InvokeFunctionAsync(context, cancellationToken).ConfigureAwait(false);
        if (OutcomeOf(result) == ToolLimitOutcome)
        {
            // Terminate skips any further calls in this answer; the run then ends in CreateResponseMessages.
            limitReached = true;
            context.Terminate = true;
        }

        return result;
    }

    protected override IList<ChatMessage> CreateResponseMessages(ReadOnlySpan<FunctionInvocationResult> results)
    {
        foreach (var result in results)
        {
            if (result.Status == FunctionInvocationStatus.Exception && result.Exception is { } failure)
            {
                toolFailure = failure;
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        if (limitReached)
        {
            throw new ToolLimitReachedException();
        }

        return base.CreateResponseMessages(results);
    }

    /// <summary>The <c>outcome</c> of a tool's JSON answer, whether it came as text or as a JSON value; null if it has none.</summary>
    private static string? OutcomeOf(object? result)
    {
        var text = result switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement { ValueKind: JsonValueKind.Object } e => e.GetRawText(),
            _ => null,
        };

        if (text is null)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("outcome", out var outcome)
                && outcome.ValueKind == JsonValueKind.String
                ? outcome.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Before the loop sees the model's answer, gives each tool it names that was not offered an
    /// <see cref="UnknownTool"/> of that name, so the loop runs it in its place among the others.
    /// </summary>
    private void BindUnknown(ChatResponse response, ChatOptions? options)
    {
        var offered = options?.Tools?.Select(t => t.Name).ToArray() ?? [];
        var names = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => c.Name)
            .Where(name => !offered.Contains(name, StringComparer.Ordinal) && !unknownTools.Any(t => t.Name == name))
            .Distinct(StringComparer.Ordinal);

        foreach (var name in names)
        {
            unknownTools.Add(new UnknownTool(name, offered, budget, recorder));
        }
    }

    /// <summary>Sits under the loop, so it sees each answer of the model before the loop acts on it.</summary>
    private sealed class UnknownToolBinder(IChatClient inner) : DelegatingChatClient(inner)
    {
        public ToolLoopChatClient? Loop { get; set; }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            Loop?.BindUnknown(response, options);
            return response;
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The engine calls the model without streaming.");
    }
}

/// <summary>The model asked for a tool call past the budget; the engine's outcome is <see cref="EngineOutcome.ToolLimit"/>.</summary>
internal sealed class ToolLimitReachedException() : Exception("The tool-call limit is reached.");
