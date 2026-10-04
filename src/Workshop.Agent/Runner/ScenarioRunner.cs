using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Workshop.Agent.Engines;
using Workshop.Agent.Scenarios;
using Workshop.Agent.Surface;
using Workshop.Agent.Telemetry;
using Workshop.Agent.Tools;
using Workshop.Core;

namespace Workshop.Agent.Runner;

/// <summary>
/// Runs one scenario pass at a time (spec §5.4). Each run gets a fresh temporary directory with a
/// fresh seeded database, the scenario's setup applied, a fresh port and its own app, which is
/// always closed before the gate audit and the end-state checks read what it left. Nothing of a run
/// outlives it but its transcript.
/// </summary>
public sealed class ScenarioRunner
{
    /// <summary>The time limit of one run (spec §4.4).</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long past its time limit the runner waits for an engine before it ends the run itself,
    /// as <see cref="EngineOutcome.TimeLimit"/>: the limit holds even for an engine that ignores it.
    /// </summary>
    public static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    private const string DatabaseFileName = "workshop.db";

    // The app writes its audit log beside the database it is given (README, "Run the app").
    private const string AuditFileName = "audit.jsonl";

    private const string SessionDirectoryName = "session";

    private readonly string appExe;
    private readonly Func<Scenario, EngineRun, IAgentEngine> engineFor;
    private readonly Func<Scenario, IApprovalGate> gateFor;
    private readonly TimeSpan timeLimit;
    private readonly TimeSpan backstop;
    private readonly Action? backstopFired;
    private readonly Lock ports = new();
    private int lastPort;

    /// <param name="appExe">The built <c>Workshop.App.exe</c>.</param>
    /// <param name="engineFor">
    /// Builds the engine for one run. It is given the run's <see cref="EngineRun"/>: an engine on
    /// the study's tools takes its budget, and records the calls it answers itself there.
    /// </param>
    /// <param name="gateFor">The approval gate for one run, such as <see cref="GateFor"/>.</param>
    public ScenarioRunner(string appExe, Func<Scenario, EngineRun, IAgentEngine> engineFor, Func<Scenario, IApprovalGate> gateFor)
        : this(appExe, engineFor, gateFor, TimeLimit, Backstop)
    {
    }

    /// <summary>
    /// For tests: a time limit and a backstop shorter than the study's, and
    /// <paramref name="backstopFired"/>, called as the backstop fires, before the engine is told to
    /// stop: the moment in which an engine can still finish on its own.
    /// </summary>
    internal ScenarioRunner(string appExe, Func<Scenario, EngineRun, IAgentEngine> engineFor, Func<Scenario, IApprovalGate> gateFor, TimeSpan timeLimit, TimeSpan backstop, Action? backstopFired = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appExe);
        ArgumentNullException.ThrowIfNull(engineFor);
        ArgumentNullException.ThrowIfNull(gateFor);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeLimit, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(backstop, TimeSpan.Zero);
        this.appExe = appExe;
        this.engineFor = engineFor;
        this.gateFor = gateFor;
        this.timeLimit = timeLimit;
        this.backstop = backstop;
        this.backstopFired = backstopFired;
    }

    /// <summary>
    /// The scenario's scripted gate: it approves every destructive press when the scenario says
    /// <c>approve</c>, and denies it otherwise. A scenario without a gate gets a denying one, so an
    /// unexpected destructive press is blocked and shows on the record.
    /// </summary>
    public static ScriptedGate GateFor(Scenario s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new ScriptedGate(s.Gate == GateAnswers.Approve);
    }

    /// <summary>
    /// Runs <paramref name="s"/> once and writes its transcript to
    /// <c>&lt;outDir&gt;/&lt;scenarioId&gt;.&lt;engine&gt;.p&lt;pass&gt;.json</c>. A limit is an
    /// outcome; the app failing to start, its endpoint failing, or the model's service failing
    /// (<see cref="EngineOutcome.ServiceError"/>) is an infrastructure error in the transcript.
    /// Cancellation, and any other exception the engine throws, propagate once the app is closed.
    /// </summary>
    public async Task<Transcript> RunAsync(Scenario s, int pass, string outDir, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentOutOfRangeException.ThrowIfLessThan(pass, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);

        var startedAt = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        var directory = Path.Combine(Path.GetTempPath(), "WorkshopAgentRuns", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var db = Path.Combine(directory, DatabaseFileName);
            CreateDatabase(db, s.Setup);
            var before = EndStateChecker.Fingerprint(db);

            var run = new EngineRun(directory, timeLimit);
            var engine = engineFor(s, run);
            var gate = gateFor(s);

            using var span = AgentTelemetry.StartScenarioRun(s.Id, pass, engine.Name, engine.Model);
            var driven = await DriveAsync(s, engine, gate, run, db, ct).ConfigureAwait(false);

            // The app is closed: only now are its audit log and database read.
            var violations = GateAudit.Violations(Path.Combine(directory, AuditFileName), driven.Tools, driven.Screens);
            var check = EndStateChecker.Check(s, db, before, driven.Result?.FinalReply);
            var outcome = driven.Result?.Outcome ?? Transcript.InfraErrorOutcome;

            // The model's service failing is the infrastructure's failure too; its calls are kept for their cost.
            var serviceError = outcome == EngineOutcome.ServiceError;
            var infra = driven.InfraMessage is not null || serviceError;
            var infraMessage = driven.InfraMessage ?? (serviceError ? driven.Result?.Error : null);
            var transcript = new Transcript(
                s.Id,
                pass,
                engine.Name,
                engine.Model,
                s.Task,
                AgentInstructions.Sha256,
                AgentSettings.Sha256,
                outcome,
                infra,
                infraMessage,
                driven.Result?.Calls ?? [],
                driven.Tools,
                driven.Result?.FinalReply,
                violations,
                check,
                Transcript.IsSuccess(check, violations, infra, outcome),
                timer.Elapsed.TotalMilliseconds,
                startedAt,
                driven.Result?.Error);

            Tag(span, transcript);
            transcript.Write(outDir);
            return transcript;
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    /// <summary>Starts the app, runs the engine on it, and always closes it.</summary>
    private async Task<Driven> DriveAsync(Scenario s, IAgentEngine engine, IApprovalGate gate, EngineRun run, string db, CancellationToken ct)
    {
        AppProcess? app = null;
        WorkshopTools? tools = null;
        EngineResult? result = null;
        string? infraMessage = null;
        try
        {
            app = AppProcess.Start(appExe, db, Path.Combine(run.Directory, SessionDirectoryName), NextPort());
            tools = new WorkshopTools(app.Client, gate, run.Budget);
            run.Bind(app, tools);
            result = await RunWithBackstopAsync(engine, s.Task, tools.Functions, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && IsInfrastructure(e))
        {
            Activity.Current?.AddException(e);
            infraMessage = $"{e.GetType().Name}: {e.Message}";
        }
        finally
        {
            app?.Close();
        }

        // Read once the app is closed: an engine may return at its time limit a moment before the
        // tool call the limit cut off has written its record, and closing the app gives it that moment.
        return new Driven(result, infraMessage, tools?.Records ?? [], [.. app?.Client.SeenScreens ?? []]);
    }

    /// <summary>
    /// Runs the engine. If it goes on past the run's time limit by the backstop
    /// (<see cref="Backstop"/> in a study run), it is told to stop and the run ends there as
    /// <see cref="EngineOutcome.TimeLimit"/>, without the model calls only the engine knew of. The app
    /// is closed after this either way, so an engine that ignores the limit cannot keep the run or
    /// the app alive.
    /// </summary>
    private async Task<EngineResult> RunWithBackstopAsync(IAgentEngine engine, string task, IReadOnlyList<AIFunction> tools, CancellationToken ct)
    {
        // Not disposed here: an engine past the backstop may still hold its token. It is disposed when the engine ends.
        var abandon = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<EngineResult> running;
        try
        {
            running = engine.RunAsync(task, tools, abandon.Token);
        }
        catch
        {
            abandon.Dispose();
            throw;
        }

        // Also observes the failure of an engine left behind, which nothing else awaits.
        _ = running.ContinueWith(
            t =>
            {
                _ = t.Exception;
                abandon.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var waited = Task.Delay(timeLimit + backstop, deadline.Token);
            if (await Task.WhenAny(running, waited).ConfigureAwait(false) == running)
            {
                await deadline.CancelAsync().ConfigureAwait(false);
                return await running.ConfigureAwait(false);
            }
        }

        // The caller's cancellation goes on as it is; only the clock is the backstop's.
        ct.ThrowIfCancellationRequested();
        backstopFired?.Invoke();
        try
        {
            await abandon.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The engine finished between the backstop firing and this: its continuation above has
            // disposed the source, and there is nothing left to stop. The run still ended here.
        }

        return new EngineResult(
            EngineOutcome.TimeLimit,
            null,
            [],
            string.Create(CultureInfo.InvariantCulture, $"The engine ran past its time limit of {timeLimit.TotalSeconds:0.###} s; the runner ended the run {backstop.TotalSeconds:0.###} s later."));
    }

    /// <summary>
    /// A failure of the app or of the way to it, which spec §5.4 makes an infrastructure error: the
    /// app not starting, or its endpoint failing, refusing, timing out or answering what cannot be read.
    /// </summary>
    private static bool IsInfrastructure(Exception e) => e is AppStartException
        or SurfaceHttpException
        or HttpRequestException
        or JsonException
        // With the caller's token not cancelled, a cancellation is the endpoint's request timing out.
        or OperationCanceledException;

    /// <summary>A fresh seeded database with the scenario's setup applied, all in one transaction.</summary>
    private static void CreateDatabase(string db, string[] setup)
    {
        var fresh = WorkshopDb.CreateFresh(db);
        using var conn = fresh.Open();
        using var tx = conn.BeginTransaction();
        foreach (var statement in setup)
        {
            using var command = conn.CreateCommand();
            command.Transaction = tx;
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>A free loopback port, never the one the last run used, so no run can meet the last one's app.</summary>
    private int NextPort()
    {
        lock (ports)
        {
            int port;
            do
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            while (port == lastPort);

            lastPort = port;
            return port;
        }
    }

    private static void Tag(Activity? span, Transcript transcript)
    {
        if (span is null)
        {
            return;
        }

        span.SetTag(AgentTelemetry.ScenarioOutcome, transcript.Outcome);
        span.SetTag(AgentTelemetry.ScenarioSuccess, transcript.Success);
        span.SetTag(AgentTelemetry.ScenarioGateViolations, transcript.GateViolations);
        span.SetTag(AgentTelemetry.ScenarioInfraError, transcript.InfraError);
        if (transcript.InfraError)
        {
            span.SetStatus(ActivityStatusCode.Error, transcript.InfraMessage);
        }
    }

    /// <summary>Deletes a run's directory; a killed app can hold its files a moment longer, so it tries a few times.</summary>
    private static void DeleteQuietly(string directory)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                {
                    Trace.TraceWarning($"The run directory '{directory}' could not be deleted: {e.Message}");
                    return;
                }

                Thread.Sleep(200 * attempt);
            }
        }
    }

    /// <summary>What a run did with the app: the engine's result, or the infrastructure's failure, and what the run saw.</summary>
    private sealed record Driven(EngineResult? Result, string? InfraMessage, IReadOnlyList<ToolRecord> Tools, Screen[] Screens);
}
