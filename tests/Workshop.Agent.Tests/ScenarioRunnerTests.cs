using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Workshop.Agent.Engines;
using Workshop.Agent.Runner;
using Workshop.Agent.Surface;
using Workshop.Agent.Telemetry;

namespace Workshop.Agent.Tests;

/// <summary>
/// One run through the real app: it is always closed (Review Focus 1), the limits are outcomes and
/// the checks still run (Review Focus 4), repeat runs start clean (Review Focus 5), and an app that
/// cannot start or an endpoint that fails is an infrastructure error, not a task failure.
/// </summary>
public sealed class ScenarioRunnerTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task App_is_closed_after_an_engine_exception()
    {
        using var output = TempRun.Create();
        var seen = new List<SeenRun>();
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (_, run) => new SpyEngine(run, new BodyEngine(_ => throw new InvalidOperationException("The engine broke.")), seen),
            ScenarioRunner.GateFor);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(ScenarioSet.Get("s05"), 1, output.Directory, Cancel));

        Assert.Equal("The engine broke.", thrown.Message);
        var app = Assert.Single(seen);
        Assert.True(app.App.HasExited);
        Assert.False(Directory.Exists(app.Directory));
        // An exception that is neither an outcome nor the infrastructure's is a bug: no transcript hides it.
        Assert.Empty(Directory.GetFiles(output.Directory));
    }

    [Fact]
    public async Task App_is_closed_when_the_run_is_cancelled()
    {
        using var output = TempRun.Create();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var seen = new List<SeenRun>();
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (_, run) => new SpyEngine(run, new BodyEngine(async ct =>
            {
                await cancel.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
                throw new UnreachableException();
            }), seen),
            ScenarioRunner.GateFor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(ScenarioSet.Get("s05"), 1, output.Directory, cancel.Token));

        var app = Assert.Single(seen);
        Assert.True(app.App.HasExited);
        Assert.False(Directory.Exists(app.Directory));
    }

    [Fact]
    public async Task App_is_closed_after_time_limit_and_checks_still_run()
    {
        using var output = TempRun.Create();
        var seen = new List<SeenRun>();
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (_, run) => new SpyEngine(run, new ChatClientEngine("fake", "hanging", new HangingModel(), run.Budget, TimeSpan.FromMilliseconds(500), run), seen),
            ScenarioRunner.GateFor);

        var transcript = await runner.RunAsync(ScenarioSet.Get("s05"), 1, output.Directory, Cancel);

        Assert.Equal(EngineOutcome.TimeLimit, transcript.Outcome);
        Assert.False(transcript.InfraError);
        Assert.True(Assert.Single(seen).App.HasExited);
        // The checks ran on the database the app left: s05 wanted a 16th job, and none was booked.
        Assert.False(transcript.Check.Passed);
        Assert.Contains("Check 1 (SELECT count(*) FROM jobs) gave '15', expected '16'.", transcript.Check.Failures);
        Assert.False(transcript.Success);
        Assert.True(File.Exists(Path.Combine(output.Directory, "s05.fake.p1.json")));
    }

    [Fact]
    public async Task App_is_closed_after_tool_limit_and_checks_still_run()
    {
        using var output = TempRun.Create();
        var seen = new List<SeenRun>();
        var describe = """{ "call": "describe_screen", "args": {} }""";
        var script = Script.Parse($$"""[{{string.Join(", ", Enumerable.Repeat(describe, 26))}}, { "reply": "Done." }]""");
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (_, run) => new SpyEngine(run, FakeEngine.Create(script, run), seen),
            ScenarioRunner.GateFor);

        var transcript = await runner.RunAsync(ScenarioSet.Get("s05"), 1, output.Directory, Cancel);

        Assert.Equal(EngineOutcome.ToolLimit, transcript.Outcome);
        Assert.Equal(26, transcript.Tools.Count);
        Assert.Equal("tool_limit", transcript.Tools[^1].Outcome);
        Assert.True(Assert.Single(seen).App.HasExited);
        Assert.Contains("Check 1 (SELECT count(*) FROM jobs) gave '15', expected '16'.", transcript.Check.Failures);
        Assert.False(transcript.Success);
    }

    [Fact]
    public async Task Time_limit_during_a_tool_call_is_time_limit_not_infra_error()
    {
        using var output = TempRun.Create();
        var seen = new List<SeenRun>();
        // s16's correct script presses cancel-job; the gate is asked and never answers, so the run's
        // time limit fires while that tool call is running.
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (s, run) => new SpyEngine(run, new ChatClientEngine("fake", "scripted", new ScriptedChatClient(ScenarioSet.ScriptFor(s.Id, "correct")), run.Budget, TimeSpan.FromSeconds(3), run), seen),
            _ => new HangingGate());

        var transcript = await runner.RunAsync(ScenarioSet.Get("s16"), 1, output.Directory, Cancel);

        Assert.Equal(EngineOutcome.TimeLimit, transcript.Outcome);
        Assert.False(transcript.InfraError, transcript.InfraMessage);
        Assert.Null(transcript.InfraMessage);
        Assert.True(Assert.Single(seen).App.HasExited);
        // The checks still ran: nothing was cancelled, so s16's end state holds.
        Assert.True(transcript.Check.Passed, string.Join(" ", transcript.Check.Failures));
        Assert.Equal(0, transcript.GateViolations);
        // The end state holds only because nothing was done: a run that hit its limit did not do the task.
        Assert.False(transcript.Success);
        // The press the limit cut off is recorded as cancelled, not as an error of the app.
        var press = transcript.Tools[^1];
        Assert.Equal("press_button", press.Tool);
        Assert.Equal("cancelled", press.Outcome);
        Assert.Null(press.Result);
    }

    [Fact]
    public async Task Engine_error_on_a_scenario_whose_end_state_is_the_seed_is_not_a_success()
    {
        using var output = TempRun.Create();
        // The script ends before the model replies: engine_error, with s18's database untouched.
        var script = Script.Parse("""[ { "call": "describe_screen", "args": {} } ]""");
        var runner = new ScenarioRunner(AppProcess.FindAppExe(), (_, run) => FakeEngine.Create(script, run), ScenarioRunner.GateFor);

        var transcript = await runner.RunAsync(ScenarioSet.Get("s18"), 1, output.Directory, Cancel);

        Assert.Equal(EngineOutcome.EngineError, transcript.Outcome);
        Assert.False(transcript.InfraError, transcript.InfraMessage);
        Assert.True(transcript.Check.Passed, string.Join(" ", transcript.Check.Failures));
        Assert.Equal(0, transcript.GateViolations);
        Assert.False(transcript.Success);
    }

    [Theory]
    [InlineData(EngineOutcome.Completed, true)]
    [InlineData(EngineOutcome.ToolLimit, false)]
    [InlineData(EngineOutcome.TimeLimit, false)]
    [InlineData(EngineOutcome.ContentFiltered, false)]
    [InlineData(EngineOutcome.Truncated, false)]
    [InlineData(EngineOutcome.Throttled, false)]
    [InlineData(EngineOutcome.EngineError, false)]
    [InlineData(EngineOutcome.ServiceError, false)]
    [InlineData(Transcript.InfraErrorOutcome, false)]
    public void Success_needs_a_completed_run(string outcome, bool success)
    {
        var passed = new Scenarios.CheckResult(true, []);

        Assert.Equal(success, Transcript.IsSuccess(passed, 0, false, outcome));
        Assert.False(Transcript.IsSuccess(passed, 1, false, outcome));
        Assert.False(Transcript.IsSuccess(passed, 0, true, outcome));
        Assert.False(Transcript.IsSuccess(new Scenarios.CheckResult(false, ["Check 1 failed."]), 0, false, outcome));
    }

    [Fact]
    public async Task Service_failure_of_the_model_is_an_infra_error_with_its_calls_kept()
    {
        using var output = TempRun.Create();
        // The model answers one tool call, then its service fails as a network fault would.
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (_, run) => new ChatClientEngine("fake", "failing", new FailingAfterOneCallModel(new HttpRequestException("No such host is known.")), run.Budget, run.TimeLimit, run),
            ScenarioRunner.GateFor);

        var transcript = await runner.RunAsync(ScenarioSet.Get("s16"), 1, output.Directory, Cancel);

        Assert.Equal(EngineOutcome.ServiceError, transcript.Outcome);
        Assert.True(transcript.InfraError);
        Assert.Equal("System.Net.Http.HttpRequestException: No such host is known.", transcript.InfraMessage);
        Assert.Equal(transcript.InfraMessage, transcript.Error);
        Assert.False(transcript.Success);
        // The call the model answered, and the tool call it asked for, are kept: their cost is not lost.
        Assert.Single(transcript.Calls);
        Assert.Equal(["describe_screen"], transcript.Tools.Select(t => t.Tool));
    }

    [Fact]
    public async Task Runner_ends_an_engine_that_ignores_its_time_limit_as_time_limit()
    {
        using var output = TempRun.Create();
        var seen = new List<SeenRun>();
        var limits = new List<TimeSpan>();
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (_, run) =>
            {
                limits.Add(run.TimeLimit);
                // An engine that never ends and never looks at its token or its limit.
                return new SpyEngine(run, new BodyEngine(_ => new TaskCompletionSource<EngineResult>().Task), seen);
            },
            ScenarioRunner.GateFor,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(500));

        var transcript = await runner.RunAsync(ScenarioSet.Get("s05"), 1, output.Directory, Cancel).WaitAsync(TimeSpan.FromSeconds(60), Cancel);

        Assert.Equal([TimeSpan.FromMilliseconds(500)], limits);
        Assert.Equal(EngineOutcome.TimeLimit, transcript.Outcome);
        Assert.False(transcript.InfraError, transcript.InfraMessage);
        Assert.Equal("The engine ran past its time limit of 0.5 s; the runner ended the run 0.5 s later.", transcript.Error);
        Assert.Empty(transcript.Calls);
        Assert.True(Assert.Single(seen).App.HasExited);
        Assert.Contains("Check 1 (SELECT count(*) FROM jobs) gave '15', expected '16'.", transcript.Check.Failures);
        Assert.False(transcript.Success);
    }

    [Fact]
    public async Task Backstop_firing_as_the_engine_finishes_still_writes_a_transcript()
    {
        using var output = TempRun.Create();
        // Its task is the run's own: an engine finishing completes it, and the runner's continuations on it run there and then.
        var finishing = new TaskCompletionSource<EngineResult>();
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (_, _) => new BodyEngine(_ => finishing.Task),
            ScenarioRunner.GateFor,
            TimeSpan.FromMilliseconds(200),
            TimeSpan.Zero,
            // The race, forced: the backstop has fired, and the engine finishes before the runner tells it to stop.
            () => Assert.True(finishing.TrySetResult(new EngineResult(EngineOutcome.Completed, "Done.", []))));

        var transcript = await runner.RunAsync(ScenarioSet.Get("s05"), 1, output.Directory, Cancel).WaitAsync(TimeSpan.FromSeconds(60), Cancel);

        // The runner had already ended the run: the engine's late answer does not count.
        Assert.Equal(EngineOutcome.TimeLimit, transcript.Outcome);
        Assert.False(transcript.InfraError, transcript.InfraMessage);
        Assert.False(transcript.Success);
        Assert.Equal("The engine ran past its time limit of 0.2 s; the runner ended the run 0 s later.", transcript.Error);
        Assert.True(File.Exists(Path.Combine(output.Directory, "s05.body.p1.json")));
    }

    [Fact]
    public void Run_time_limit_is_five_minutes_with_a_30_second_backstop()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), ScenarioRunner.TimeLimit);
        Assert.Equal(TimeSpan.FromSeconds(30), ScenarioRunner.Backstop);
    }

    [Fact]
    public async Task Tool_records_name_the_model_call_that_issued_them()
    {
        using var output = TempRun.Create();
        var script = Script.Parse("""
            [
              { "calls": [ { "call": "describe_screen", "args": {} }, { "call": "no_such_tool", "args": {} } ] },
              { "call": "list_screens", "args": {} },
              { "reply": "Done." }
            ]
            """);
        var runner = new ScenarioRunner(AppProcess.FindAppExe(), (_, run) => FakeEngine.Create(script, run), ScenarioRunner.GateFor);

        var transcript = await runner.RunAsync(ScenarioSet.Get("s16"), 1, output.Directory, Cancel);

        Assert.Equal(EngineOutcome.Completed, transcript.Outcome);
        Assert.Equal(3, transcript.Calls.Count);
        Assert.Equal(["describe_screen", "no_such_tool", "list_screens"], transcript.Tools.Select(t => t.Tool));
        Assert.Equal(["ok", "bad_arguments", "ok"], transcript.Tools.Select(t => t.Outcome));
        // The first model call asked for two calls at once, the unknown tool among them; the second for one.
        Assert.Equal([1, 1, 2], transcript.Tools.Select(t => t.ModelCallIndex));
    }

    [Fact]
    public async Task Two_passes_share_no_state()
    {
        using var output = TempRun.Create();
        var seen = new List<SeenRun>();
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (s, run) => new SpyEngine(run, FakeEngine.Create(ScenarioSet.ScriptFor(s.Id, "correct"), run), seen),
            ScenarioRunner.GateFor);
        var s05 = ScenarioSet.Get("s05");

        var first = await runner.RunAsync(s05, 1, output.Directory, Cancel);
        Assert.True(seen[0].App.HasExited);
        Assert.False(Directory.Exists(seen[0].Directory));
        var second = await runner.RunAsync(s05, 2, output.Directory, Cancel);

        // Each pass books job J-1016 and expects 16 jobs: a database the first pass left would hold 17.
        Assert.True(first.Success, string.Join(" ", first.Check.Failures));
        Assert.True(second.Success, string.Join(" ", second.Check.Failures));
        Assert.Contains("J-1016", second.FinalReply, StringComparison.Ordinal);
        Assert.Equal(2, seen.Count);
        Assert.NotEqual(seen[0].Directory, seen[1].Directory);
        Assert.NotEqual(seen[0].Port, seen[1].Port);
        Assert.NotEqual(seen[0].App.Id, seen[1].App.Id);
        Assert.True(seen[1].App.HasExited);
        Assert.Equal(["s05.fake.p1.json", "s05.fake.p2.json"], Directory.GetFiles(output.Directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Infra_error_when_app_cannot_start_is_reported_not_failed()
    {
        using var output = TempRun.Create();
        var missing = Path.Combine(output.Directory, "no-such-app", AppProcess.AppExeName);
        var runner = new ScenarioRunner(missing, (s, run) => FakeEngine.Create(ScenarioSet.ScriptFor(s.Id, "correct"), run), ScenarioRunner.GateFor);

        var transcript = await runner.RunAsync(ScenarioSet.Get("s05"), 1, output.Directory, Cancel);

        Assert.True(transcript.InfraError);
        Assert.Equal(Transcript.InfraErrorOutcome, transcript.Outcome);
        // The app's path, with the temp directory and the run's 32-hex name taken out.
        Assert.Contains(@"<temp>\WorkshopAgentTests\<run>\no-such-app\Workshop.App.exe", transcript.InfraMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(missing, transcript.InfraMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(AppStartException), transcript.InfraMessage, StringComparison.Ordinal);
        Assert.False(transcript.Success);
        Assert.Equal("fake", transcript.Engine);
        Assert.Empty(transcript.Calls);
        Assert.Empty(transcript.Tools);
        Assert.Null(transcript.FinalReply);
        Assert.True(File.Exists(Path.Combine(output.Directory, "s05.fake.p1.json")));
    }

    [Fact]
    public async Task Infra_error_when_the_endpoint_fails_mid_run_is_reported_not_failed()
    {
        using var output = TempRun.Create();
        var seen = new List<SeenRun>();
        var script = Script.Parse("""
            [
              { "call": "describe_screen", "args": {} },
              { "call": "describe_screen", "args": {} },
              { "reply": "Done." }
            ]
            """);
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (_, run) => new SpyEngine(run, new KillAppAfterFirstCall(run, FakeEngine.Create(script, run)), seen),
            ScenarioRunner.GateFor);

        var transcript = await runner.RunAsync(ScenarioSet.Get("s16"), 1, output.Directory, Cancel);

        Assert.True(transcript.InfraError);
        Assert.Equal(Transcript.InfraErrorOutcome, transcript.Outcome);
        Assert.Contains(nameof(HttpRequestException), transcript.InfraMessage, StringComparison.Ordinal);
        Assert.False(transcript.Success);
        // The call that met the dead endpoint is still on the record.
        Assert.Equal(["ok", "error"], transcript.Tools.Select(t => t.Outcome));
    }

    [Fact]
    public async Task Transcript_names_the_engines_deployment_and_agent_version()
    {
        // A real engine's transcript says which deployment it called and which prompt agent version it ran.
        using var output = TempRun.Create();
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (s, run) => new ChatClientEngine("gpt", "gpt-5.6-luna", new ScriptedChatClient(ScenarioSet.ScriptFor(s.Id, "correct")), run.Budget, run.TimeLimit, run)
            {
                Deployment = "gpt-5.6-luna",
                AgentVersion = "3",
            },
            ScenarioRunner.GateFor);

        var transcript = await runner.RunAsync(ScenarioSet.Get("s05"), 1, output.Directory, Cancel);

        Assert.Equal("gpt-5.6-luna", transcript.Deployment);
        Assert.Equal("3", transcript.AgentVersion);
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output.Directory, "s05.gpt.p1.json")));
        Assert.Equal("gpt-5.6-luna", json.RootElement.GetProperty("deployment").GetString());
        Assert.Equal("3", json.RootElement.GetProperty("agentVersion").GetString());
    }

    [Fact]
    public async Task Transcript_has_every_field()
    {
        using var output = TempRun.Create();
        var startedAfter = DateTimeOffset.UtcNow;
        var runner = new ScenarioRunner(AppProcess.FindAppExe(), (s, run) => FakeEngine.Create(ScenarioSet.ScriptFor(s.Id, "correct"), run), ScenarioRunner.GateFor);

        var transcript = await runner.RunAsync(ScenarioSet.Get("s05"), 3, output.Directory, Cancel);

        var file = Path.Combine(output.Directory, "s05.fake.p3.json");
        using var json = JsonDocument.Parse(File.ReadAllBytes(file));
        var root = json.RootElement;
        Assert.Equal(
            ["scenarioId", "pass", "engine", "model", "deployment", "agentVersion", "task", "instructionsSha256", "settingsSha256", "outcome", "infraError", "infraMessage", "calls", "tools", "finalReply", "gateViolations", "check", "success", "ms", "startedAt", "error"],
            root.EnumerateObject().Select(p => p.Name));

        Assert.Equal("s05", root.GetProperty("scenarioId").GetString());
        Assert.Equal(3, root.GetProperty("pass").GetInt32());
        Assert.Equal("fake", root.GetProperty("engine").GetString());
        Assert.Equal(FakeEngine.Model, root.GetProperty("model").GetString());
        // The fake engine has no deployment and no agent version.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("deployment").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("agentVersion").ValueKind);
        Assert.Equal(ScenarioSet.Get("s05").Task, root.GetProperty("task").GetString());
        Assert.Equal(AgentInstructions.Sha256, root.GetProperty("instructionsSha256").GetString());
        Assert.Matches("^[0-9a-f]{64}$", AgentInstructions.Sha256);
        Assert.Equal(AgentSettings.Sha256, root.GetProperty("settingsSha256").GetString());
        Assert.Matches("^[0-9a-f]{64}$", AgentSettings.Sha256);
        Assert.Equal("completed", root.GetProperty("outcome").GetString());
        Assert.False(root.GetProperty("infraError").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("infraMessage").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);

        // Six tool calls, then the reply: seven model calls, each with its tokens.
        var calls = root.GetProperty("calls").EnumerateArray().ToArray();
        Assert.Equal(7, calls.Length);
        Assert.Equal(Enumerable.Range(1, 7), calls.Select(c => c.GetProperty("index").GetInt32()));
        Assert.All(calls, c =>
        {
            Assert.Equal(ScriptedChatClient.InputTokensPerResponse, c.GetProperty("inputTokens").GetInt64());
            Assert.Equal(ScriptedChatClient.OutputTokensPerResponse, c.GetProperty("outputTokens").GetInt64());
            Assert.True(c.GetProperty("ms").GetDouble() >= 0);
        });
        Assert.Equal("stop", calls[^1].GetProperty("finishReason").GetString());

        var tools = root.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(6, tools.Length);
        Assert.Equal(
            ["index", "modelCallIndex", "tool", "arguments", "outcome", "message", "screenId", "ms", "approved", "result"],
            tools[0].EnumerateObject().Select(p => p.Name));
        // One tool call per model call here: each names the model call that asked for it.
        Assert.Equal(Enumerable.Range(1, 6), tools.Select(t => t.GetProperty("modelCallIndex").GetInt32()));
        Assert.Equal("open_screen", tools[0].GetProperty("tool").GetString());
        Assert.Equal("new-job", tools[0].GetProperty("arguments").GetProperty("screen").GetString());
        Assert.Equal("press_button", tools[^1].GetProperty("tool").GetString());
        Assert.Equal("ok", tools[^1].GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, tools[^1].GetProperty("approved").ValueKind);
        // Each record holds the JSON text the model was given: here the first call's reply, with its screen.
        using (var opened = JsonDocument.Parse(tools[0].GetProperty("result").GetString()!))
        {
            Assert.Equal("ok", opened.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("new-job", opened.RootElement.GetProperty("screen").GetProperty("id").GetString());
        }

        Assert.All(tools, t => Assert.Equal(JsonValueKind.String, t.GetProperty("result").ValueKind));

        Assert.StartsWith("Booked in Sam Rivera's Aster Book 14 laptop as job J-1016", root.GetProperty("finalReply").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, root.GetProperty("gateViolations").GetInt32());
        Assert.True(root.GetProperty("check").GetProperty("passed").GetBoolean());
        Assert.Equal(0, root.GetProperty("check").GetProperty("failures").GetArrayLength());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("ms").GetDouble() > 0);
        var startedAt = root.GetProperty("startedAt").GetDateTimeOffset();
        Assert.InRange(startedAt, startedAfter, DateTimeOffset.UtcNow);

        // The file is the transcript the run returned.
        Assert.True(transcript.Success);
        Assert.Equal(transcript.StartedAt, startedAt);
        Assert.Equal(transcript.Tools.Count, tools.Length);
    }

    [Fact]
    public async Task Scenario_run_span_holds_the_run_and_is_tagged_with_its_outcome()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        using var output = TempRun.Create();
        var runner = new ScenarioRunner(AppProcess.FindAppExe(), (s, run) => FakeEngine.Create(ScenarioSet.ScriptFor(s.Id, "correct"), run), ScenarioRunner.GateFor);

        // A pass no other test uses, so the span is this run's even while other runs are traced.
        await runner.RunAsync(ScenarioSet.Get("s16"), 41, output.Directory, Cancel);

        var run = Assert.Single(stopped, a => a.OperationName == AgentTelemetry.ScenarioRun && Equals(a.GetTagItem(AgentTelemetry.ScenarioPass), 41));
        Assert.Equal("s16", run.GetTagItem(AgentTelemetry.ScenarioId));
        Assert.Equal(EngineOutcome.Completed, run.GetTagItem(AgentTelemetry.ScenarioOutcome));
        Assert.Equal(true, run.GetTagItem(AgentTelemetry.ScenarioSuccess));
        Assert.Equal(0, run.GetTagItem(AgentTelemetry.ScenarioGateViolations));
        Assert.Equal(false, run.GetTagItem(AgentTelemetry.ScenarioInfraError));

        var inside = stopped.Where(a => a.TraceId == run.TraceId && a != run).ToArray();
        Assert.Equal(5, inside.Count(a => a.OperationName == AgentTelemetry.ModelCall));
        Assert.Equal(4, inside.Count(a => a.OperationName == AgentTelemetry.ToolExecute));
        Assert.Contains(inside, a => a.OperationName == AgentTelemetry.ToolExecute && Equals(a.GetTagItem(AgentTelemetry.ToolApproved), false));
    }

    /// <summary>Kills the app once the model's first tool call is answered, so the next call meets a dead endpoint.</summary>
    private sealed class KillAppAfterFirstCall(EngineRun run, IAgentEngine inner) : IAgentEngine
    {
        public string Name => inner.Name;

        public string Model => inner.Model;

        public Task<EngineResult> RunAsync(string task, IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools, CancellationToken ct)
        {
            var killing = tools.Select(tool => (Microsoft.Extensions.AI.AIFunction)new KillAfter(tool, run)).ToArray();
            return inner.RunAsync(task, killing, ct);
        }

        private sealed class KillAfter(Microsoft.Extensions.AI.AIFunction tool, EngineRun run) : Microsoft.Extensions.AI.DelegatingAIFunction(tool)
        {
            private int calls;

            protected override async ValueTask<object?> InvokeCoreAsync(Microsoft.Extensions.AI.AIFunctionArguments arguments, CancellationToken cancellationToken)
            {
                var result = await base.InvokeCoreAsync(arguments, cancellationToken);
                if (Interlocked.Increment(ref calls) == 1)
                {
                    using var app = Process.GetProcessById(run.App!.ProcessId);
                    app.Kill(entireProcessTree: true);
                    await app.WaitForExitAsync(cancellationToken);
                }

                return result;
            }
        }
    }
}
