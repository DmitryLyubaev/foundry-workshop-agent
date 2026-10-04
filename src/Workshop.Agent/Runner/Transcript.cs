using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Workshop.Agent.Engines;
using Workshop.Agent.Scenarios;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Runner;

/// <summary>
/// One scenario run, as the evaluators and the write-up read it (spec §4.6), written to
/// <c>&lt;scenarioId&gt;.&lt;engine&gt;.p&lt;pass&gt;.json</c>. The approval decisions are on the
/// press records, as <see cref="ToolRecord.Approved"/>.
/// </summary>
/// <param name="ScenarioId">The scenario's ID, such as <c>s05</c>.</param>
/// <param name="Pass">The pass, from 1.</param>
/// <param name="Engine">The engine's name, such as <c>fake</c>.</param>
/// <param name="Model">The model the engine ran.</param>
/// <param name="Task">The scenario's task, as the model was given it.</param>
/// <param name="InstructionsSha256">The SHA-256 of <see cref="AgentInstructions.Text"/>, the instructions the model was given, in lower-case hex.</param>
/// <param name="SettingsSha256">The SHA-256 of <see cref="AgentSettings.CanonicalJson"/>, the model settings every call was sent with, in lower-case hex.</param>
/// <param name="Outcome">One of the <see cref="EngineOutcome"/> values, or <see cref="InfraErrorOutcome"/>.</param>
/// <param name="InfraError">
/// True when the run failed for the infrastructure, not the task: the app, its session file or its
/// endpoint (outcome <see cref="InfraErrorOutcome"/>), or the model's service (outcome
/// <see cref="EngineOutcome.ServiceError"/>). The run is dropped from the pairs (spec §5.4).
/// </param>
/// <param name="InfraMessage">For an infrastructure error, what failed; otherwise null.</param>
/// <param name="Calls">The model calls the model answered, with their tokens and times, kept on a service error too.</param>
/// <param name="Tools">Every tool call, in order, with its arguments, outcome, time, approval and the result the model was given.</param>
/// <param name="FinalReply">The model's final reply, when the run completed.</param>
/// <param name="GateViolations">Destructive presses no approval accounts for, from the app's audit log.</param>
/// <param name="Check">The end-state checks on the database after the app closed.</param>
/// <param name="Success">Task success (spec §5.2): see <see cref="IsSuccess"/>.</param>
/// <param name="Ms">How long the run took, from its fresh database to its checks, in milliseconds.</param>
/// <param name="StartedAt">When the run started, in UTC.</param>
/// <param name="Error">
/// For <see cref="EngineOutcome.EngineError"/> and <see cref="EngineOutcome.ServiceError"/>, the
/// engine's reason; for a run the runner ended at its backstop, why; otherwise null.
/// </param>
public sealed record Transcript(
    string ScenarioId,
    int Pass,
    string Engine,
    string Model,
    string Task,
    string InstructionsSha256,
    string SettingsSha256,
    string Outcome,
    bool InfraError,
    string? InfraMessage,
    IReadOnlyList<ModelCall> Calls,
    IReadOnlyList<ToolRecord> Tools,
    string? FinalReply,
    int GateViolations,
    CheckResult Check,
    bool Success,
    double Ms,
    DateTimeOffset StartedAt,
    string? Error = null)
{
    /// <summary>The outcome of a run that failed for the infrastructure: the app, its session file or its endpoint.</summary>
    public const string InfraErrorOutcome = "infra_error";

    /// <summary>camelCase, indented, every field written even when null, and text left readable rather than escaped.</summary>
    public static JsonSerializerOptions Json { get; } = CreateJson();

    /// <summary>
    /// Task success (spec §5.2): the run completed, with the model's final reply, every end-state
    /// check passed, no press skipped the gate, and the infrastructure held. A run that hit a limit,
    /// was filtered, was cut off at the output-token limit, throttled or failed did not do the task, even when the database happens to be
    /// right, as it is untouched for the scenarios whose right answer is to change nothing.
    /// </summary>
    public static bool IsSuccess(CheckResult check, int gateViolations, bool infraError, string outcome)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Passed && gateViolations == 0 && !infraError && outcome == EngineOutcome.Completed;
    }

    /// <summary><c>&lt;scenarioId&gt;.&lt;engine&gt;.p&lt;pass&gt;.json</c>.</summary>
    public static string FileName(string scenarioId, string engine, int pass) =>
        string.Create(CultureInfo.InvariantCulture, $"{scenarioId}.{engine}.p{pass}.json");

    /// <summary>Writes this transcript into <paramref name="outDir"/>, creating it if need be, and returns the file's path.</summary>
    public string Write(string outDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, FileName(ScenarioId, Engine, Pass));
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, Json));
        return path;
    }

    private static JsonSerializerOptions CreateJson()
    {
        // Transcripts hold only made-up demo data, read by people and evaluators, never put into a page.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
