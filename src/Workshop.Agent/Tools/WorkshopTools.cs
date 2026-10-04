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
public sealed class WorkshopTools : IToolCallRecorder
{
    public const string ListScreensName = "list_screens";
    public const string DescribeScreenName = "describe_screen";
    public const string OpenScreenName = "open_screen";
    public const string SetFieldName = "set_field";
    public const string SelectRowName = "select_row";
    public const string PressButtonName = "press_button";

    /// <summary>The record's outcome for a call the run's end cut off.</summary>
    public const string CancelledOutcome = "cancelled";

    private const string CutOffMessage = "The run ended before the call finished.";

    // The text goes to a model, never into a page, so quotes and accents stay readable rather than escaped.
    private static readonly JsonSerializerOptions ReplyJson = CreateReplyJson();

    private readonly SurfaceClient client;
    private readonly IApprovalGate gate;
    private readonly ToolBudget budget;

    // One call at a time, so nothing acts between a press's fresh describe and the press itself.
    private readonly SemaphoreSlim turn = new(1, 1);
    private readonly Lock recordsLock = new();
    private readonly List<ToolRecord> records = [];

    // The model call whose answer asked for the calls now running; null until an engine reports one.
    private int? modelCall;

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

    /// <summary>Every call so far, in order, including those refused by the budget or for their arguments, and calls to tools that do not exist.</summary>
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

    /// <summary>Records a call the engine answered itself, in order with the tools' own calls.</summary>
    public void RecordUnknown(string tool, JsonElement arguments, string outcome, string message, double ms, string result)
    {
        lock (recordsLock)
        {
            records.Add(new ToolRecord(records.Count + 1, modelCall, tool, arguments, outcome, message, null, ms, null, result));
        }
    }

    /// <summary>Notes the model call that the next tool calls belong to.</summary>
    public void ModelCallAnswered(int index)
    {
        lock (recordsLock)
        {
            modelCall = index;
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
            var call = new Call(tool, ToolArguments.Snapshot(arguments), CurrentModelCall());

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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The run ended, at its time limit or by its caller, while the call ran: not the app's failure.
                Record(call, new Result(null, CancelledOutcome, CutOffMessage, call.ScreenId, call.Approved));
                span?.SetTag(AgentTelemetry.ToolOutcome, CancelledOutcome);
                span?.SetTag(AgentTelemetry.ToolApproved, call.Approved);
                throw;
            }
            catch (Exception e)
            {
                // The app or the connection failed: recorded and traced, then left to the engine and the runner.
                // An approval already given stays on the record: a press that failed on the way may still have run.
                Record(call, new Result(null, "error", e.Message, call.ScreenId, call.Approved));
                span?.SetTag(AgentTelemetry.ToolOutcome, "error");
                span?.SetTag(AgentTelemetry.ToolApproved, call.Approved);
                span?.SetStatus(ActivityStatusCode.Error, e.Message);
                throw;
            }

            Record(call, result);
            span?.SetTag(AgentTelemetry.ToolOutcome, result.Outcome);
            span?.SetTag(AgentTelemetry.ToolApproved, result.Approved);
            return result.Json!;
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

            // Recorded before it is sent, as cut off until it ends: the app may log the press and the
            // run end before this call can write anything more, and the gate audit, which reads the
            // records once the app is closed, must still find the approval (spec §4.3).
            Record(call, new Result(null, CancelledOutcome, CutOffMessage, screen.Id, true));
        }

        return await ActAsync(ActionRequest.Press(buttonId), ct).ConfigureAwait(false) with { Approved = call.Approved };
    }

    /// <summary>Records the call, or, once it has a record, updates that record in place: one record per call.</summary>
    private void Record(Call call, Result result)
    {
        lock (recordsLock)
        {
            if (call.Slot is { } slot)
            {
                records[slot] = NewRecord(records[slot].Index);
            }
            else
            {
                call.Slot = records.Count;
                records.Add(NewRecord(records.Count + 1));
            }
        }

        ToolRecord NewRecord(int index) =>
            new(index, call.ModelCallIndex, call.Tool, call.Arguments, result.Outcome, result.Message, result.ScreenId, call.Timer.Elapsed.TotalMilliseconds, result.Approved, result.Json);
    }

    private int? CurrentModelCall()
    {
        lock (recordsLock)
        {
            return modelCall;
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
    /// <param name="tool">The tool's name.</param>
    /// <param name="arguments">The arguments as the model sent them.</param>
    /// <param name="modelCallIndex">Taken as the call starts: the model call that asked for it.</param>
    private sealed class Call(string tool, JsonElement arguments, int? modelCallIndex)
    {
        public string Tool { get; } = tool;

        public JsonElement Arguments { get; } = arguments;

        public int? ModelCallIndex { get; } = modelCallIndex;

        public Stopwatch Timer { get; } = Stopwatch.StartNew();

        public string? ScreenId { get; set; }

        public bool? Approved { get; set; }

        /// <summary>The call's place in the records once it has one; read and written under the records' lock.</summary>
        public int? Slot { get; set; }
    }

    /// <summary>What a call did: the text the model sees, null when it sees none, and what the record and the span keep.</summary>
    private sealed record Result(string? Json, string Outcome, string? Message, string? ScreenId, bool? Approved = null);

    /// <summary>The tool's own answer, when the app was not asked or could not answer: denied, not_found, bad_arguments or tool_limit.</summary>
    private sealed record ToolMessage(string Outcome, string Message);
}
