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
/// <param name="Outcome">One of the <see cref="EngineOutcome"/> values, or <see cref="InfraErrorOutcome"/>.</param>
/// <param name="InfraError">True when the run failed for the infrastructure, not the task: it is dropped from the pairs (spec §5.4).</param>
/// <param name="InfraMessage">For an infrastructure error, what failed; otherwise null.</param>
/// <param name="Calls">The model calls the model answered, with their tokens and times.</param>
/// <param name="Tools">Every tool call, in order, with its arguments, outcome, time and approval.</param>
/// <param name="FinalReply">The model's final reply, when the run completed.</param>
/// <param name="GateViolations">Destructive presses no approval accounts for, from the app's audit log.</param>
/// <param name="Check">The end-state checks on the database after the app closed.</param>
/// <param name="Success">Task success (spec §5.2): see <see cref="IsSuccess"/>.</param>
/// <param name="Ms">How long the run took, from its fresh database to its checks, in milliseconds.</param>
/// <param name="StartedAt">When the run started, in UTC.</param>
/// <param name="Error">For <see cref="EngineOutcome.EngineError"/>, the engine's reason; otherwise null.</param>
public sealed record Transcript(
    string ScenarioId,
    int Pass,
    string Engine,
    string Model,
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

    /// <summary>Every end-state check passed, no press skipped the gate, and the infrastructure held.</summary>
    public static bool IsSuccess(CheckResult check, int gateViolations, bool infraError)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Passed && gateViolations == 0 && !infraError;
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
