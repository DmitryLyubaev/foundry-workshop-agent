using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Workshop.Agent.Surface;
using Workshop.Agent.Telemetry;
using Workshop.Agent.Tools;
using Workshop.Core;

namespace Workshop.Agent.Tests;

/// <summary>The six tools, the approval gate and the budget, against the real built app.</summary>
public sealed class WorkshopToolsTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Six_functions_with_exact_names_and_parameters()
    {
        // No app: building the functions never calls the endpoint.
        using var client = new SurfaceClient(1, "made-up-token");
        var tools = new WorkshopTools(client, new ScriptedGate(approve: false), new ToolBudget());

        Assert.Equal(
            ["list_screens", "describe_screen", "open_screen", "set_field", "select_row", "press_button"],
            tools.Functions.Select(f => f.Name));
        Assert.Equal(
            [
                ToolDescriptions.ListScreens,
                ToolDescriptions.DescribeScreen,
                ToolDescriptions.OpenScreen,
                ToolDescriptions.SetField,
                ToolDescriptions.SelectRow,
                ToolDescriptions.PressButton,
            ],
            tools.Functions.Select(f => f.Description));

        string[][] parameters = [[], [], ["screen"], ["field", "value"], ["list", "row"], ["button"]];
        foreach (var (function, expected) in tools.Functions.Zip(parameters))
        {
            var schema = function.JsonSchema;
            var properties = schema.TryGetProperty("properties", out var p) ? p.EnumerateObject().ToArray() : [];
            Assert.Equal(expected, properties.Select(q => q.Name));
            Assert.All(properties, q => Assert.Equal("string", q.Value.GetProperty("type").GetString()));
            var required = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];
            Assert.Equal(expected, required);
        }

        // One sentence each, about the current screen.
        Assert.All(
            [ToolDescriptions.DescribeScreen, ToolDescriptions.OpenScreen, ToolDescriptions.SetField, ToolDescriptions.SelectRow, ToolDescriptions.PressButton],
            d => Assert.Contains("current screen", d, StringComparison.Ordinal));
        Assert.All(tools.Functions, f => Assert.Single(f.Description.Split(". ")));
    }

    [Fact]
    public async Task Press_ordinary_button_needs_no_gate()
    {
        using var app = RunningApp.Start();
        var gate = new ScriptedGate(approve: false);
        var tools = new WorkshopTools(app.App.Client, gate, new ToolBudget());

        await Invoke(tools, "select_row", new { list = "jobs", row = "J-1008" });
        var pressed = await Invoke(tools, "press_button", new { button = "open-job" });

        Assert.Equal("ok", pressed.GetProperty("outcome").GetString());
        Assert.Equal("job-detail", pressed.GetProperty("screen").GetProperty("id").GetString());
        Assert.Empty(gate.Asked);
        var record = tools.Records[^1];
        Assert.Equal("press_button", record.Tool);
        Assert.Equal("ok", record.Outcome);
        Assert.Null(record.Approved);
    }

    [Fact]
    public async Task Press_cancel_job_asks_the_gate_and_approved_cancels()
    {
        using var app = RunningApp.Start();
        var gate = new ScriptedGate(approve: true);
        var tools = new WorkshopTools(app.App.Client, gate, new ToolBudget());
        await OpenJob(tools, "J-1008");

        var pressed = await Invoke(tools, "press_button", new { button = "cancel-job" });

        Assert.Equal("ok", pressed.GetProperty("outcome").GetString());
        Assert.Equal([new GateRequest("cancel-job", "Cancel job", "job-detail")], gate.Asked);
        var record = tools.Records[^1];
        Assert.Equal("ok", record.Outcome);
        Assert.True(record.Approved);

        app.App.Close();
        Assert.Equal(JobStatus.Cancelled, StatusOf(app, "J-1008"));
        Assert.Contains(AuditLines(app), line => line is { Type: "press", Target: "cancel-job", Outcome: "ok" });
    }

    [Fact]
    public async Task Denied_press_never_reaches_the_app()
    {
        using var app = RunningApp.Start();
        var gate = new ScriptedGate(approve: false);
        var tools = new WorkshopTools(app.App.Client, gate, new ToolBudget());
        await OpenJob(tools, "J-1008");

        var reply = await InvokeText(tools, "press_button", new { button = "cancel-job" });

        Assert.Equal("""{"outcome":"denied","message":"The user did not approve pressing Cancel job."}""", reply);
        Assert.Single(gate.Asked);
        var record = tools.Records[^1];
        Assert.Equal("denied", record.Outcome);
        Assert.Equal("The user did not approve pressing Cancel job.", record.Message);
        Assert.Equal("job-detail", record.ScreenId);
        Assert.False(record.Approved);
        Assert.Equal(reply, record.Result);

        app.App.Close();
        var audit = AuditLines(app);
        // The log is written: the select and the open-job press before the denial are there.
        Assert.Contains(audit, line => line is { Type: "press", Target: "open-job" });
        Assert.DoesNotContain(audit, line => line is { Type: "press", Target: "cancel-job" });
        Assert.Equal(JobStatus.Diagnosing, StatusOf(app, "J-1008"));
    }

    [Fact]
    public async Task Gate_uses_a_fresh_description()
    {
        using var app = RunningApp.Start();
        var gate = new ScriptedGate(approve: false);
        var tools = new WorkshopTools(app.App.Client, gate, new ToolBudget());

        // The model's last look: the job list, which has no destructive button.
        var described = await Invoke(tools, "describe_screen", new { });
        Assert.Equal("job-list", described.GetProperty("id").GetString());
        Assert.DoesNotContain(described.GetProperty("buttons").EnumerateArray(), b => b.GetProperty("destructive").GetBoolean());

        // The screen changes behind the model's back: the job's detail, with Cancel job, is now showing.
        await app.App.Client.ActAsync(ActionRequest.Select("jobs", "J-1008"), Cancel);
        Assert.Equal("job-detail", (await app.App.Client.ActAsync(ActionRequest.Press("open-job"), Cancel)).Screen.Id);

        var reply = await Invoke(tools, "press_button", new { button = "cancel-job" });

        Assert.Equal("denied", reply.GetProperty("outcome").GetString());
        Assert.Equal([new GateRequest("cancel-job", "Cancel job", "job-detail")], gate.Asked);
        app.App.Close();
        Assert.DoesNotContain(AuditLines(app), line => line is { Type: "press", Target: "cancel-job" });
        Assert.Equal(JobStatus.Diagnosing, StatusOf(app, "J-1008"));
    }

    [Fact]
    public async Task Press_unknown_button_is_not_found_and_never_reaches_the_app()
    {
        using var app = RunningApp.Start();
        var gate = new ScriptedGate(approve: true);
        var tools = new WorkshopTools(app.App.Client, gate, new ToolBudget());

        // The job list has no Cancel job: the fresh description says so, and the tool answers itself.
        var reply = await InvokeText(tools, "press_button", new { button = "cancel-job" });

        Assert.Equal("""{"outcome":"not_found","message":"There is no button 'cancel-job' on this screen."}""", reply);
        Assert.Empty(gate.Asked);
        var record = Assert.Single(tools.Records);
        Assert.Equal("not_found", record.Outcome);
        Assert.Equal("There is no button 'cancel-job' on this screen.", record.Message);
        Assert.Equal("job-list", record.ScreenId);
        Assert.Null(record.Approved);
        Assert.Equal(reply, record.Result);

        // The same text the app gives for a button it does not have, so the model sees one message either way.
        var direct = await app.App.Client.ActAsync(ActionRequest.Press("no-such-button"), Cancel);
        Assert.Equal("There is no button 'no-such-button' on this screen.", direct.Message);

        app.App.Close();
        var audit = AuditLines(app);
        Assert.DoesNotContain(audit, line => line is { Type: "press", Target: "cancel-job" });
        Assert.Equal(["no-such-button"], audit.Select(line => line.Target));
    }

    [Fact]
    public async Task Approved_press_keeps_its_approval_when_the_app_refuses_or_the_call_fails()
    {
        using var app = RunningApp.Start();
        var tools = new WorkshopTools(app.App.Client, new ScriptedGate(approve: true), new ToolBudget());
        await OpenJob(tools, "J-1005");

        // A ready job cannot be cancelled: the app refuses, and the approval given stays on the record.
        var refused = await Invoke(tools, "press_button", new { button = "cancel-job" });

        Assert.Equal("validation_failed", refused.GetProperty("outcome").GetString());
        Assert.Equal("A job that is ready cannot become cancelled.", refused.GetProperty("message").GetString());
        Assert.True(tools.Records[^1].Approved);

        // The app goes away between the approval and the press: the call fails, and the record still says approved.
        var failing = new WorkshopTools(app.App.Client, new CallbackGate(app.App.Close), new ToolBudget());
        await Assert.ThrowsAnyAsync<Exception>(() => InvokeText(failing, "press_button", new { button = "cancel-job" }));

        var record = Assert.Single(failing.Records);
        Assert.Equal("error", record.Outcome);
        Assert.Equal("job-detail", record.ScreenId);
        Assert.True(record.Approved);
        Assert.Null(record.Result);
        Assert.Single(AuditLines(app), line => line is { Type: "press", Target: "cancel-job", Outcome: "validation_failed" });
    }

    [Fact]
    public async Task Budget_stops_at_25_without_calling_the_app()
    {
        using var app = RunningApp.Start();
        var gate = new ScriptedGate(approve: true);
        var budget = new ToolBudget();
        var tools = new WorkshopTools(app.App.Client, gate, budget);

        for (var i = 1; i <= 25; i++)
        {
            var set = await Invoke(tools, "set_field", new { field = "search", value = $"J-{i}" });
            Assert.Equal("ok", set.GetProperty("outcome").GetString());
        }

        Assert.Equal(25, budget.Used);
        const string limit = """{"outcome":"tool_limit","message":"The tool-call limit of 25 is reached."}""";
        Assert.Equal(limit, await InvokeText(tools, "open_screen", new { screen = "parts" }));
        Assert.Equal(limit, await InvokeText(tools, "press_button", new { button = "cancel-job" }));
        Assert.Equal(limit, await InvokeText(tools, "describe_screen", new { }));
        Assert.Equal(limit, await InvokeText(tools, "open_screen", new { }));

        Assert.Equal(25, budget.Used);
        Assert.Empty(gate.Asked);
        Assert.Equal(29, tools.Records.Count);
        Assert.All(tools.Records.Skip(25), r => Assert.Equal("tool_limit", r.Outcome));
        Assert.Equal("job-list", (await app.App.Client.DescribeAsync(Cancel)).Id);
        app.App.Close();
        var audit = AuditLines(app);
        Assert.Equal(25, audit.Count);
        Assert.All(audit, line => Assert.Equal("set", line.Type));
    }

    [Fact]
    public async Task Missing_argument_is_bad_arguments()
    {
        using var app = RunningApp.Start();
        var budget = new ToolBudget();
        var tools = new WorkshopTools(app.App.Client, new ScriptedGate(approve: true), budget);

        // As a model sends them: JSON values, some missing, empty, null or of the wrong type.
        (string Tool, string Arguments, string Message)[] cases =
        [
            ("open_screen", "{}", "The argument 'screen' is missing."),
            ("open_screen", """{"screen":""}""", "The argument 'screen' is empty."),
            ("open_screen", """{"screen":null}""", "The argument 'screen' is missing."),
            ("open_screen", """{"screen":5}""", "The argument 'screen' must be a string."),
            ("set_field", """{"field":"search"}""", "The argument 'value' is missing."),
            ("set_field", """{"value":"x"}""", "The argument 'field' is missing."),
            ("set_field", """{"field":"search","value":["x"]}""", "The argument 'value' must be a string, a number, true or false."),
            ("select_row", """{"list":"jobs","row":"  "}""", "The argument 'row' is empty."),
            ("press_button", """{"button":{"id":"cancel-job"}}""", "The argument 'button' must be a string."),
        ];

        foreach (var (tool, arguments, message) in cases)
        {
            var reply = JsonDocument.Parse(await InvokeText(tools, tool, Arguments(arguments))).RootElement;
            Assert.Equal(["outcome", "message"], reply.EnumerateObject().Select(p => p.Name));
            Assert.Equal("bad_arguments", reply.GetProperty("outcome").GetString());
            Assert.Equal(message, reply.GetProperty("message").GetString());
        }

        Assert.Equal(cases.Length, budget.Used);
        Assert.All(tools.Records, r => Assert.Equal("bad_arguments", r.Outcome));
        Assert.Equal("""{"screen":5}""", tools.Records[3].Arguments.GetRawText());

        // A number is a fine value: the endpoint takes it as its JSON text, as it would from any client.
        await Invoke(tools, "open_screen", new { screen = "parts" });
        var set = await Invoke(tools, "set_field", Arguments("""{"field":"quantity","value":7}"""));
        Assert.Equal("ok", set.GetProperty("outcome").GetString());
        Assert.Equal("7", set.GetProperty("screen").GetProperty("fields")[0].GetProperty("value").GetString());

        app.App.Close();
        Assert.Equal(["open", "set"], AuditLines(app).Select(line => line.Type));
    }

    [Fact]
    public async Task Unknown_screen_is_not_found_result()
    {
        using var app = RunningApp.Start();
        var tools = new WorkshopTools(app.App.Client, new ScriptedGate(approve: true), new ToolBudget());

        var reply = await Invoke(tools, "open_screen", new { screen = "invoices" });

        Assert.Equal("not_found", reply.GetProperty("outcome").GetString());
        Assert.Equal("There is no screen 'invoices'.", reply.GetProperty("message").GetString());
        Assert.Equal("job-list", reply.GetProperty("screen").GetProperty("id").GetString());
        var record = Assert.Single(tools.Records);
        Assert.Equal("not_found", record.Outcome);
        Assert.Equal("There is no screen 'invoices'.", record.Message);
    }

    [Fact]
    public async Task Records_carry_index_tool_outcome_and_ms()
    {
        using var app = RunningApp.Start();
        var tools = new WorkshopTools(app.App.Client, new ScriptedGate(approve: true), new ToolBudget());

        var screens = await Invoke(tools, "list_screens", new { });
        await Invoke(tools, "describe_screen", new { });
        await Invoke(tools, "open_screen", new { screen = "parts" });
        await Invoke(tools, "select_row", new { list = "parts", row = "P-99" });

        Assert.Equal(6, screens.GetArrayLength());
        Assert.Equal("""{"id":"job-list","title":"Jobs"}""", screens[0].GetRawText());
        var records = tools.Records;
        Assert.Equal([1, 2, 3, 4], records.Select(r => r.Index));
        Assert.Equal(["list_screens", "describe_screen", "open_screen", "select_row"], records.Select(r => r.Tool));
        Assert.Equal(["ok", "ok", "ok", "not_found"], records.Select(r => r.Outcome));
        Assert.Equal([null, null, null, "Parts has no row 'P-99'."], records.Select(r => r.Message));
        Assert.Equal([null, "job-list", "parts", "parts"], records.Select(r => r.ScreenId));
        Assert.Equal(["{}", "{}", """{"screen":"parts"}""", """{"list":"parts","row":"P-99"}"""], records.Select(r => r.Arguments.GetRawText()));
        Assert.All(records, r => Assert.True(r.Ms > 0, $"{r.Tool} took {r.Ms} ms."));
        Assert.All(records, r => Assert.Null(r.Approved));
        Assert.Equal(screens.GetRawText(), records[0].Result);
        Assert.StartsWith("""{"outcome":"not_found","message":"Parts has no row 'P-99'.","screen":{"id":"parts",""", records[3].Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_call_cut_off_by_its_token_is_recorded_cancelled_and_rethrown()
    {
        using var app = RunningApp.Start();
        var tools = new WorkshopTools(app.App.Client, new HangingGate(), new ToolBudget());
        await OpenJob(tools, "J-1008");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        limit.CancelAfter(TimeSpan.FromMilliseconds(300));

        // The gate never answers, so the run's limit fires while the press is waiting on it.
        var press = tools.Functions.Single(f => f.Name == "press_button");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await press.InvokeAsync(Arguments("""{"button":"cancel-job"}"""), limit.Token));

        var record = tools.Records[^1];
        Assert.Equal("press_button", record.Tool);
        Assert.Equal("cancelled", record.Outcome);
        Assert.Equal("The run ended before the call finished.", record.Message);
        Assert.Equal("job-detail", record.ScreenId);
        Assert.Null(record.Approved);
        Assert.Null(record.Result);
    }

    [Fact]
    public async Task Each_call_is_a_tool_execute_span_with_name_outcome_and_approval()
    {
        using var app = RunningApp.Start();
        var tools = new WorkshopTools(app.App.Client, new ScriptedGate(approve: false), new ToolBudget());
        await OpenJob(tools, "J-1008");

        // Other tests run tools in parallel, so only the spans under this test's own root are kept.
        using var testSource = new ActivitySource("Workshop.Agent.Tests.Spans");
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is AgentTelemetry.SourceName or "Workshop.Agent.Tests.Spans",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using (var root = testSource.StartActivity("test")!)
        {
            listener.ActivityStopped = activity =>
            {
                if (activity.TraceId == root.TraceId && activity.Source.Name == AgentTelemetry.SourceName)
                {
                    lock (spans)
                    {
                        spans.Add(activity);
                    }
                }
            };
            await Invoke(tools, "describe_screen", new { });
            await Invoke(tools, "press_button", new { button = "cancel-job" });
        }

        Assert.Equal(2, spans.Count);
        Assert.All(spans, s => Assert.Equal("tool.execute", s.OperationName));
        Assert.Equal(["describe_screen", "press_button"], spans.Select(s => s.GetTagItem("tool.name") as string));
        Assert.Equal(["ok", "denied"], spans.Select(s => s.GetTagItem("tool.outcome") as string));
        Assert.Equal(new object?[] { null, false }, spans.Select(s => s.GetTagItem("tool.approved")));
    }

    [Fact]
    public async Task Console_gate_approves_only_y()
    {
        Assert.True(await AskConsole("y\n"));
        Assert.True(await AskConsole(" Y \n"));
        Assert.False(await AskConsole("yes\n"));
        Assert.False(await AskConsole("n\n"));
        Assert.False(await AskConsole("\n"));
        Assert.False(await AskConsole(""));

        using var output = new StringWriter();
        await new ConsoleGate(new StringReader("n\n"), output).ApproveAsync("cancel-job", "Cancel job", "job-detail", Cancel);
        Assert.Equal("Approve pressing \"Cancel job\" on job-detail? [y/N] ", output.ToString());

        static async Task<bool> AskConsole(string typed) =>
            await new ConsoleGate(new StringReader(typed), TextWriter.Null).ApproveAsync("cancel-job", "Cancel job", "job-detail", Cancel);
    }

    private static async Task OpenJob(WorkshopTools tools, string job)
    {
        await Invoke(tools, "select_row", new { list = "jobs", row = job });
        var opened = await Invoke(tools, "press_button", new { button = "open-job" });
        Assert.Equal("job-detail", opened.GetProperty("screen").GetProperty("id").GetString());
    }

    private static async Task<JsonElement> Invoke(WorkshopTools tools, string name, object arguments) =>
        JsonDocument.Parse(await InvokeText(tools, name, arguments)).RootElement.Clone();

    private static Task<string> InvokeText(WorkshopTools tools, string name, object arguments) =>
        InvokeText(tools, name, Arguments(JsonSerializer.Serialize(arguments)));

    /// <summary>Calls a tool as the function-invoking client does: by name, with the model's arguments as JSON values.</summary>
    private static async Task<string> InvokeText(WorkshopTools tools, string name, AIFunctionArguments arguments)
    {
        var function = tools.Functions.Single(f => f.Name == name);
        var result = await function.InvokeAsync(arguments, Cancel);
        return Assert.IsType<string>(result);
    }

    private static AIFunctionArguments Arguments(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!);

    private static JobStatus StatusOf(RunningApp app, string job) =>
        new JobService(WorkshopDb.OpenExisting(app.Run.DatabasePath)).Job(job)!.Status;

    /// <summary>The app's own record of every action it received, read after it has closed.</summary>
    private static List<AuditLine> AuditLines(RunningApp app)
    {
        var path = Path.Combine(app.Run.Directory, "audit.jsonl");
        return File.Exists(path)
            ? [.. File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<AuditLine>(line, JsonSerializerOptions.Web)!)]
            : [];
    }

    /// <summary>A gate that runs something when it is asked, then approves.</summary>
    private sealed class CallbackGate(Action onAsk) : IApprovalGate
    {
        public Task<bool> ApproveAsync(string buttonId, string label, string screenId, CancellationToken ct)
        {
            onAsk();
            return Task.FromResult(true);
        }
    }

    private sealed record AuditLine(string Type, string? Target, string? Value, string Outcome);
}
