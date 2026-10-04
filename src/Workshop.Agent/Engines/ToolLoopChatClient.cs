using System.Runtime.ExceptionServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Workshop.Agent.Telemetry;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Engines;

/// <summary>
/// The library's function-invocation loop, with the study's rules where the library's own differ:
/// <list type="bullet">
/// <item>A call to a tool that does not exist takes one from the budget, like any call, and is
/// answered <c>bad_arguments</c>, which the model can read (Review Focus 3); the library would
/// answer free text.</item>
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
    private const string BadArgumentsOutcome = "bad_arguments";

    // As the tools write their own answers: the text goes to a model, so it stays readable rather than escaped.
    private static readonly JsonSerializerOptions ReplyJson = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ToolBudget budget;
    private string[] offered = [];
    private bool limitReached;
    private Exception? toolFailure;

    public ToolLoopChatClient(IChatClient inner, ToolBudget budget)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(budget);
        this.budget = budget;
        AllowConcurrentInvocation = false;
        MaximumIterationsPerRequest = int.MaxValue;
        TerminateOnUnknownCalls = false;
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Kept for the answer to an unknown tool, which names the tools there are.
        offered = options?.Tools?.Select(t => t.Name).ToArray() ?? [];
        return base.GetResponseAsync(messages, options, cancellationToken);
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

        var messages = base.CreateResponseMessages(results);
        foreach (var result in results)
        {
            if (result.Status == FunctionInvocationStatus.NotFound)
            {
                var answer = AnswerUnknown(result.CallContent.Name);
                foreach (var content in messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Where(c => c.CallId == result.CallContent.CallId))
                {
                    content.Result = answer;
                    content.Exception = null;
                }
            }
        }

        if (limitReached)
        {
            throw new ToolLimitReachedException();
        }

        return messages;
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

    /// <summary>Answers a call to a tool that does not exist, traced and counted like any tool call.</summary>
    private string AnswerUnknown(string name)
    {
        using var span = AgentTelemetry.Source.StartActivity(AgentTelemetry.ToolExecute);
        span?.SetTag(AgentTelemetry.ToolName, name);

        string outcome, message;
        if (!budget.TryTake())
        {
            limitReached = true;
            (outcome, message) = (ToolLimitOutcome, $"The tool-call limit of {budget.Max} is reached.");
        }
        else
        {
            var known = offered.Length > 0 ? string.Join(", ", offered) : "none";
            (outcome, message) = (BadArgumentsOutcome, $"There is no tool '{name}'. The tools are {known}.");
        }

        span?.SetTag(AgentTelemetry.ToolOutcome, outcome);
        return JsonSerializer.Serialize(new { outcome, message }, ReplyJson);
    }
}

/// <summary>The model asked for a tool call past the budget; the engine's outcome is <see cref="EngineOutcome.ToolLimit"/>.</summary>
internal sealed class ToolLimitReachedException() : Exception("The tool-call limit is reached.");
