using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Workshop.Agent.Telemetry;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Engines;

/// <summary>
/// Stands in for a tool the model named but was never offered. Like the real tools, it takes one
/// call from the budget, is traced as <c>tool.execute</c> and is recorded, and it answers JSON the
/// model can read: <c>bad_arguments</c>, naming the tools there are, or <c>tool_limit</c> past the budget.
/// </summary>
internal sealed class UnknownTool : AIFunction
{
    // As the tools write their own answers: the text goes to a model, so it stays readable rather than escaped.
    private static readonly JsonSerializerOptions ReplyJson = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string[] offered;
    private readonly ToolBudget budget;
    private readonly IToolCallRecorder? recorder;

    public UnknownTool(string name, IReadOnlyList<string> offered, ToolBudget budget, IToolCallRecorder? recorder)
    {
        Name = name;
        this.offered = [.. offered];
        this.budget = budget;
        this.recorder = recorder;
    }

    public override string Name { get; }

    public override string Description => "Not a tool: answers a call to a tool that does not exist.";

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // A loop the run has abandoned, at its time limit, must not take from the budget or add a record.
        cancellationToken.ThrowIfCancellationRequested();
        using var span = AgentTelemetry.Source.StartActivity(AgentTelemetry.ToolExecute);
        span?.SetTag(AgentTelemetry.ToolName, Name);
        var timer = Stopwatch.StartNew();

        var (outcome, message) = budget.TryTake()
            ? ("bad_arguments", $"There is no tool '{Name}'. The tools are {(offered.Length > 0 ? string.Join(", ", offered) : "none")}.")
            : ("tool_limit", $"The tool-call limit of {budget.Max} is reached.");

        var reply = JsonSerializer.Serialize(new { outcome, message }, ReplyJson);
        recorder?.RecordUnknown(Name, ToolArguments.Snapshot(arguments), outcome, message, timer.Elapsed.TotalMilliseconds, reply);
        span?.SetTag(AgentTelemetry.ToolOutcome, outcome);
        return new ValueTask<object?>(reply);
    }
}
