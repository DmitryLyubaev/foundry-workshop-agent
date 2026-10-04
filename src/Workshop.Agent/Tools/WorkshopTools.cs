using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Workshop.Agent.Surface;
using Workshop.Agent.Telemetry;

namespace Workshop.Agent.Tools;

/// <summary>
/// The six tools the model drives the app with, over its endpoint. Every call takes one from the
/// budget, is recorded and traced, and answers the model with JSON text: the app's reply, the
/// screen, the screens list, or the tool's own <c>{"outcome","message"}</c>. A destructive press
/// needs the approval gate's yes, asked against a fresh description of the current screen.
/// </summary>
public sealed class WorkshopTools
{
    public const string ListScreensName = "list_screens";
    public const string DescribeScreenName = "describe_screen";
    public const string OpenScreenName = "open_screen";
    public const string SetFieldName = "set_field";
    public const string SelectRowName = "select_row";
    public const string PressButtonName = "press_button";

    // The text goes to a model, never into a page, so quotes and accents stay readable rather than escaped.
    private static readonly JsonSerializerOptions ReplyJson = CreateReplyJson();

    private readonly SurfaceClient client;
    private readonly IApprovalGate gate;
    private readonly ToolBudget budget;

    // One call at a time, so nothing acts between a press's fresh describe and the press itself.
    private readonly SemaphoreSlim turn = new(1, 1);
    private readonly Lock recordsLock = new();
    private readonly List<ToolRecord> records = [];

    public WorkshopTools(SurfaceClient client, IApprovalGate gate, ToolBudget budget)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(budget);
        this.client = client;
        this.gate = gate;
        this.budget = budget;

        // The lambdas' parameter names become the schema's: they are the argument names the model sends.
        Functions =
        [
            Create(ListScreensName, ToolDescriptions.ListScreens, (AIFunctionArguments arguments, CancellationToken ct) =>
                RunAsync(ListScreensName, arguments, [], (_, c) => ListScreensAsync(c), ct)),
            Create(DescribeScreenName, ToolDescriptions.DescribeScreen, (AIFunctionArguments arguments, CancellationToken ct) =>
                RunAsync(DescribeScreenName, arguments, [], (_, c) => DescribeAsync(c), ct)),
            Create(OpenScreenName, ToolDescriptions.OpenScreen, (string screen, AIFunctionArguments arguments, CancellationToken ct) =>
                RunAsync(OpenScreenName, arguments, [nameof(screen)], (_, c) => ActAsync(ActionRequest.Open(screen), c), ct)),
            Create(SetFieldName, ToolDescriptions.SetField, (string field, string value, AIFunctionArguments arguments, CancellationToken ct) =>
                RunAsync(SetFieldName, arguments, [nameof(field), nameof(value)], (_, c) => ActAsync(ActionRequest.Set(field, value), c), ct)),
            Create(SelectRowName, ToolDescriptions.SelectRow, (string list, string row, AIFunctionArguments arguments, CancellationToken ct) =>
                RunAsync(SelectRowName, arguments, [nameof(list), nameof(row)], (_, c) => ActAsync(ActionRequest.Select(list, row), c), ct)),
            Create(PressButtonName, ToolDescriptions.PressButton, (string button, AIFunctionArguments arguments, CancellationToken ct) =>
                RunAsync(PressButtonName, arguments, [nameof(button)], (call, c) => PressAsync(button, call, c), ct)),
        ];
    }

    public IReadOnlyList<AIFunction> Functions { get; }

    /// <summary>Every call so far, in order, including those refused by the budget or for their arguments.</summary>
    public IReadOnlyList<ToolRecord> Records
    {
        get
        {
            lock (recordsLock)
            {
                return [.. records];
            }
        }
    }

    private static AIFunction Create(string name, string description, Delegate tool) =>
        AIFunctionFactory.Create(tool, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            ConfigureParameterBinding = ToolArguments.Bind,
            // The tool already returns JSON text; the default would serialise it again, as a JSON string.
            MarshalResult = static (result, _, _) => new ValueTask<object?>(result),
        });

    private async Task<string> RunAsync(string tool, AIFunctionArguments arguments, string[] required, Func<Call, CancellationToken, Task<Result>> work, CancellationToken ct)
    {
        await turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var span = AgentTelemetry.Source.StartActivity(AgentTelemetry.ToolExecute);
            span?.SetTag(AgentTelemetry.ToolName, tool);
            var timer = Stopwatch.StartNew();
            var given = ToolArguments.Snapshot(arguments);
            var call = new Call();

            Result result;
            try
            {
                result = !budget.TryTake() ? Message("tool_limit", $"The tool-call limit of {budget.Max} is reached.")
                    : ToolArguments.FirstProblem(arguments, required) is { } problem ? Message("bad_arguments", problem)
                    : await work(call, ct).ConfigureAwait(false);
            }
            catch (SurfaceHttpException e) when (e.Status == 400)
            {
                // The endpoint could not read one valid action: the arguments' fault, not the app's.
                result = Message("bad_arguments", $"The app could not read the action: {e.Body}") with { ScreenId = call.ScreenId, Approved = call.Approved };
            }
            catch (Exception e)
            {
                // The app or the connection failed: recorded and traced, then left to the engine and the runner.
                // An approval already given stays on the record: a press that failed on the way may still have run.
                Record(tool, given, new Result("", "error", e.Message, call.ScreenId, call.Approved), timer);
                span?.SetTag(AgentTelemetry.ToolOutcome, "error");
                span?.SetTag(AgentTelemetry.ToolApproved, call.Approved);
                span?.SetStatus(ActivityStatusCode.Error, e.Message);
                throw;
            }

            Record(tool, given, result, timer);
            span?.SetTag(AgentTelemetry.ToolOutcome, result.Outcome);
            span?.SetTag(AgentTelemetry.ToolApproved, result.Approved);
            return result.Json;
        }
        finally
        {
            turn.Release();
        }
    }

    private async Task<Result> ListScreensAsync(CancellationToken ct)
    {
        var screens = await client.ListScreensAsync(ct).ConfigureAwait(false);
        return new Result(Json(screens), "ok", null, null);
    }

    private async Task<Result> DescribeAsync(CancellationToken ct)
    {
        var screen = await client.DescribeAsync(ct).ConfigureAwait(false);
        return new Result(Json(screen), "ok", null, screen.Id);
    }

    private async Task<Result> ActAsync(ActionRequest action, CancellationToken ct)
    {
        var reply = await client.ActAsync(action, ct).ConfigureAwait(false);
        return new Result(Json(reply), reply.Outcome, reply.Message, reply.Screen.Id);
    }

    /// <summary>
    /// Looks at the screen as it is now, never as the model last saw it, and presses only a button
    /// that fresh description shows: the gate is asked when it flags the button destructive. A
    /// button that is not there is answered <c>not_found</c> here, with the app's own message, and
    /// never sent, so the app's audit log holds no press that the gate did not see.
    /// </summary>
    private async Task<Result> PressAsync(string buttonId, Call call, CancellationToken ct)
    {
        var screen = await client.DescribeAsync(ct).ConfigureAwait(false);
        call.ScreenId = screen.Id;
        var button = screen.Buttons.FirstOrDefault(b => b.Id == buttonId);

        if (button is null)
        {
            return Message("not_found", $"There is no button '{buttonId}' on this screen.") with { ScreenId = screen.Id };
        }

        if (button.Destructive)
        {
            call.Approved = await gate.ApproveAsync(button.Id, button.Label, screen.Id, ct).ConfigureAwait(false);
            if (call.Approved == false)
            {
                return Message("denied", $"The user did not approve pressing {button.Label}.") with { ScreenId = screen.Id, Approved = false };
            }
        }

        return await ActAsync(ActionRequest.Press(buttonId), ct).ConfigureAwait(false) with { Approved = call.Approved };
    }

    private void Record(string tool, JsonElement arguments, Result result, Stopwatch timer)
    {
        lock (recordsLock)
        {
            records.Add(new ToolRecord(records.Count + 1, tool, arguments, result.Outcome, result.Message, result.ScreenId, timer.Elapsed.TotalMilliseconds, result.Approved));
        }
    }

    private static Result Message(string outcome, string message) =>
        new(Json(new ToolMessage(outcome, message)), outcome, message, null);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ReplyJson);

    private static JsonSerializerOptions CreateReplyJson()
    {
        var options = new JsonSerializerOptions(ContractJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>What a call has learnt so far, kept for its record if the call then fails.</summary>
    private sealed class Call
    {
        public string? ScreenId { get; set; }

        public bool? Approved { get; set; }
    }

    /// <summary>What a call did: the text the model sees, and what the record and the span keep.</summary>
    private sealed record Result(string Json, string Outcome, string? Message, string? ScreenId, bool? Approved = null);

    /// <summary>The tool's own answer, when the app was not asked or could not answer: denied, not_found, bad_arguments or tool_limit.</summary>
    private sealed record ToolMessage(string Outcome, string Message);
}
