using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Workshop.Agent.Engines;
using Workshop.Agent.Surface;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests;

/// <summary>
/// The engine over the scripted model, with stub tools in place of the app: each outcome, the
/// model calls it records, and what the tools' failures do (Review Focus 3 and 4).
/// </summary>
public sealed class ChatClientEngineTests
{
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Engine_returns_completed_with_reply_and_calls()
    {
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "call": "describe_screen", "args": {} },
              { "call": "open_screen", "args": { "screen": "parts" } },
              { "reply": "There are 4 laptop batteries." }
            ]
            """));
        var tools = new StubTools();
        var engine = new ChatClientEngine("fake", "scripted", model, new ToolBudget(), FiveMinutes);

        var result = await engine.RunAsync("How many laptop batteries do we have in stock?", tools.Functions, Cancel);

        Assert.Equal("fake", engine.Name);
        Assert.Equal("scripted", engine.Model);
        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        Assert.Equal("There are 4 laptop batteries.", result.FinalReply);
        Assert.Equal(["describe_screen", "open_screen parts"], tools.Calls);
        Assert.Equal([1, 2, 3], result.Calls.Select(c => c.Index));
        Assert.All(result.Calls, c =>
        {
            Assert.Equal(1000, c.InputTokens);
            Assert.Equal(50, c.OutputTokens);
            Assert.True(c.Ms >= 0);
        });
        Assert.Equal(["tool_calls", "tool_calls", "stop"], result.Calls.Select(c => c.FinishReason));
        Assert.Null(result.Error);

        // The agent's instructions and the task reach the model.
        var first = model.Requests[0];
        Assert.Equal(AgentInstructions.Text, first.Options?.Instructions);
        Assert.Contains(first.Messages, m => m.Role == ChatRole.User && m.Text == "How many laptop batteries do we have in stock?");
        Assert.Equal(["describe_screen", "open_screen", "set_field"], first.Options!.Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task Final_reply_is_the_last_answer_not_text_beside_a_tool_call()
    {
        // A real model may write a few words beside a tool call; they are not its reply.
        var model = new TalkativeModel();
        var engine = new ChatClientEngine("fake", "talkative", model, new ToolBudget(), FiveMinutes);

        var result = await engine.RunAsync("Look.", new StubTools().Functions, Cancel);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        Assert.Equal("Done.", result.FinalReply);
    }

    [Fact]
    public async Task The_model_client_stays_the_callers_and_is_not_disposed()
    {
        var model = new TalkativeModel();
        var engine = new ChatClientEngine("fake", "talkative", model, new ToolBudget(), FiveMinutes);

        await engine.RunAsync("Look.", new StubTools().Functions, Cancel);

        Assert.False(model.Disposed);
    }

    [Fact]
    public async Task Throttle_waits_retry_after_then_continues()
    {
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "throttle": 2 },
              { "call": "describe_screen", "args": {} },
              { "throttle": 7.5 },
              { "reply": "Done." }
            ]
            """));
        var waits = new List<TimeSpan>();
        var engine = new ChatClientEngine("fake", "scripted", model, new ToolBudget(), FiveMinutes, TimeSpan.FromSeconds(60), Record(waits));

        var result = await engine.RunAsync("Look.", new StubTools().Functions, Cancel);

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(7.5)], waits);
        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        Assert.Equal("Done.", result.FinalReply);
        // A throttled attempt has no answer: the two calls are the ones the model answered.
        Assert.Equal(["tool_calls", "stop"], result.Calls.Select(c => c.FinishReason));
    }

    [Fact]
    public async Task Throttle_budget_spent_is_throttled()
    {
        // 40 s is waited; 30 s more would pass the 60 s budget, so the run stops there.
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "call": "describe_screen", "args": {} },
              { "throttle": 40 },
              { "throttle": 30 },
              { "reply": "Never sent." }
            ]
            """));
        var waits = new List<TimeSpan>();
        var engine = new ChatClientEngine("fake", "scripted", model, new ToolBudget(), FiveMinutes, TimeSpan.FromSeconds(60), Record(waits));

        var result = await engine.RunAsync("Look.", new StubTools().Functions, Cancel);

        Assert.Equal(EngineOutcome.Throttled, result.Outcome);
        Assert.Null(result.FinalReply);
        Assert.Equal([TimeSpan.FromSeconds(40)], waits);
        Assert.Single(result.Calls);

        // One wait longer than the whole budget is not waited at all, with the public constructor's defaults.
        var once = new ChatClientEngine("fake", "scripted", new ScriptedChatClient(Script.Parse("""[ { "throttle": 61 } ]""")), new ToolBudget(), FiveMinutes);
        Assert.Equal(EngineOutcome.Throttled, (await once.RunAsync("Look.", new StubTools().Functions, Cancel)).Outcome);
    }

    [Fact]
    public async Task Content_filter_is_content_filtered()
    {
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "call": "describe_screen", "args": {} },
              { "filter": true }
            ]
            """));
        var engine = new ChatClientEngine("fake", "scripted", model, new ToolBudget(), FiveMinutes);

        var result = await engine.RunAsync("Look.", new StubTools().Functions, Cancel);

        Assert.Equal(EngineOutcome.ContentFiltered, result.Outcome);
        Assert.Null(result.FinalReply);
        Assert.Equal(["tool_calls", "content_filter"], result.Calls.Select(c => c.FinishReason));
    }

    [Fact]
    public async Task Time_limit_is_time_limit()
    {
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "call": "describe_screen", "args": {} },
              { "call": "wait_forever", "args": {} },
              { "reply": "Never sent." }
            ]
            """));
        var tools = new StubTools();
        var engine = new ChatClientEngine("fake", "scripted", model, new ToolBudget(), TimeSpan.FromMilliseconds(200));

        var result = await engine.RunAsync("Look.", tools.WithWait, Cancel);

        Assert.Equal(EngineOutcome.TimeLimit, result.Outcome);
        Assert.Null(result.FinalReply);
        Assert.Equal(2, result.Calls.Count);
        Assert.Equal(["describe_screen", "wait_forever"], tools.Calls);
    }

    [Fact]
    public async Task Time_limit_during_a_throttle_wait_is_time_limit()
    {
        var model = new ScriptedChatClient(Script.Parse("""[ { "throttle": 30 }, { "reply": "Never sent." } ]"""));
        var engine = new ChatClientEngine("fake", "scripted", model, new ToolBudget(), TimeSpan.FromMilliseconds(200));

        var result = await engine.RunAsync("Look.", new StubTools().Functions, Cancel);

        Assert.Equal(EngineOutcome.TimeLimit, result.Outcome);
    }

    [Fact]
    public async Task Tool_limit_is_tool_limit()
    {
        // A budget of 2: the third call is refused by the tool, and that ends the run, so the
        // script's later steps are never asked for.
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "call": "describe_screen", "args": {} },
              { "call": "open_screen", "args": { "screen": "parts" } },
              { "call": "describe_screen", "args": {} },
              { "call": "describe_screen", "args": {} },
              { "reply": "Never sent." }
            ]
            """));
        var budget = new ToolBudget(2);
        var tools = new StubTools(budget);
        var engine = new ChatClientEngine("fake", "scripted", model, budget, FiveMinutes);

        var result = await engine.RunAsync("Look.", tools.Functions, Cancel);

        Assert.Equal(EngineOutcome.ToolLimit, result.Outcome);
        Assert.Null(result.FinalReply);
        Assert.Equal(["describe_screen", "open_screen parts", "describe_screen refused"], tools.Calls);
        Assert.Equal(3, result.Calls.Count);
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal(2, budget.Used);
    }

    [Fact]
    public async Task Tool_limit_from_the_real_tools_answer_is_tool_limit()
    {
        // The engine reads the tools' answer, whatever its form: here the library's default
        // marshalling, a JSON string holding the tool's JSON text.
        var model = new ScriptedChatClient(Script.Parse("""[ { "call": "limited", "args": {} }, { "reply": "Never sent." } ]"""));
        var limited = AIFunctionFactory.Create(() => """{"outcome":"tool_limit","message":"The tool-call limit of 25 is reached."}""", "limited");
        var engine = new ChatClientEngine("fake", "scripted", model, new ToolBudget(), FiveMinutes);

        var result = await engine.RunAsync("Look.", [limited], Cancel);

        Assert.Equal(EngineOutcome.ToolLimit, result.Outcome);
    }

    [Fact]
    public async Task Unknown_tool_is_answered_bad_arguments_and_the_run_carries_on()
    {
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "call": "delete_everything", "args": { "really": "yes" } },
              { "call": "describe_screen", "args": {} },
              { "reply": "Done." }
            ]
            """));
        var budget = new ToolBudget();
        var tools = new StubTools(budget);
        var engine = new ChatClientEngine("fake", "scripted", model, budget, FiveMinutes);

        var result = await engine.RunAsync("Look.", tools.Functions, Cancel);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        Assert.Equal(["describe_screen"], tools.Calls);
        // The unknown call took one from the budget, as every call does.
        Assert.Equal(2, budget.Used);

        var answer = Assert.Single(model.Requests[1].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>());
        using var json = JsonDocument.Parse(Assert.IsType<string>(answer.Result));
        Assert.Equal("bad_arguments", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "There is no tool 'delete_everything'. The tools are describe_screen, open_screen, set_field.",
            json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Unknown_tool_past_the_budget_is_tool_limit()
    {
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "call": "describe_screen", "args": {} },
              { "call": "no_such_tool", "args": {} },
              { "reply": "Never sent." }
            ]
            """));
        var budget = new ToolBudget(1);
        var engine = new ChatClientEngine("fake", "scripted", model, budget, FiveMinutes);

        var result = await engine.RunAsync("Look.", new StubTools(budget).Functions, Cancel);

        Assert.Equal(EngineOutcome.ToolLimit, result.Outcome);
        Assert.Equal(2, model.Requests.Count);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    public async Task Endpoint_failure_in_a_tool_propagates_untouched(int status)
    {
        var failure = new SurfaceHttpException(status, """{"error":"made-up"}""");
        await AssertPropagates(failure);
    }

    [Fact]
    public async Task Connection_failure_in_a_tool_propagates_untouched() =>
        await AssertPropagates(new HttpRequestException("No connection could be made."));

    [Fact]
    public async Task Model_failure_is_engine_error()
    {
        // The script ends before the model replies: the model, not a tool, failed.
        var model = new ScriptedChatClient(Script.Parse("""[ { "call": "describe_screen", "args": {} } ]"""));
        var engine = new ChatClientEngine("fake", "scripted", model, new ToolBudget(), FiveMinutes);

        var result = await engine.RunAsync("Look.", new StubTools().Functions, Cancel);

        Assert.Equal(EngineOutcome.EngineError, result.Outcome);
        Assert.Null(result.FinalReply);
        Assert.Single(result.Calls);
        Assert.Equal("System.InvalidOperationException: The script has no step left: all 1 are used.", result.Error);
    }

    [Fact]
    public async Task Cancelled_by_the_caller_is_not_an_outcome()
    {
        var model = new ScriptedChatClient(Script.Parse("""[ { "call": "wait_forever", "args": {} } ]"""));
        var engine = new ChatClientEngine("fake", "scripted", model, new ToolBudget(), FiveMinutes);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunAsync("Look.", new StubTools().WithWait, cts.Token));
    }

    [Fact]
    public async Task Time_limit_with_a_model_that_ignores_cancellation_is_time_limit()
    {
        var engine = new ChatClientEngine("fake", "hung", new HungModel(throwWhenCancelled: false), new ToolBudget(), TimeSpan.FromMilliseconds(200));

        var result = await engine.RunAsync("Look.", new StubTools().Functions, Cancel).WaitAsync(TimeSpan.FromSeconds(10), Cancel);

        Assert.Equal(EngineOutcome.TimeLimit, result.Outcome);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task A_failure_after_the_time_limit_is_time_limit()
    {
        // A model client that turns cancellation into its own exception, not an OperationCanceledException.
        var engine = new ChatClientEngine("fake", "hung", new HungModel(throwWhenCancelled: true), new ToolBudget(), TimeSpan.FromMilliseconds(200));

        var result = await engine.RunAsync("Look.", new StubTools().Functions, Cancel).WaitAsync(TimeSpan.FromSeconds(10), Cancel);

        Assert.Equal(EngineOutcome.TimeLimit, result.Outcome);
    }

    [Fact]
    public async Task Throttle_with_no_wait_still_waits_a_second_and_spends_the_budget()
    {
        // 61 throttles asking for no wait: each wait is 1 s at least, so 60 are waited and the 61st is throttled.
        var steps = string.Join(", ", Enumerable.Repeat("""{ "throttle": 0 }""", 61));
        var model = new ScriptedChatClient(Script.Parse("[ " + steps + """, { "reply": "Never sent." } ]"""));
        var waits = new List<TimeSpan>();
        var engine = new ChatClientEngine("fake", "scripted", model, new ToolBudget(), FiveMinutes, TimeSpan.FromSeconds(60), Record(waits));

        var result = await engine.RunAsync("Look.", new StubTools().Functions, Cancel);

        Assert.Equal(EngineOutcome.Throttled, result.Outcome);
        Assert.Equal(60, waits.Count);
        Assert.All(waits, w => Assert.Equal(TimeSpan.FromSeconds(1), w));
        Assert.Equal(61, model.Requests.Count);
    }

    [Fact]
    public async Task A_batch_of_calls_runs_in_the_models_order_unknown_tools_included()
    {
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "calls": [
                  { "call": "describe_screen", "args": {} },
                  { "call": "no_such_tool", "args": { "x": 1 } },
                  { "call": "open_screen", "args": { "screen": "parts" } } ] },
              { "reply": "Done." }
            ]
            """));
        var budget = new ToolBudget();
        var tools = new StubTools(budget);
        var recorder = new ListRecorder(tools);
        var engine = new ChatClientEngine("fake", "scripted", model, budget, FiveMinutes, recorder);

        var result = await engine.RunAsync("Look.", tools.Functions, Cancel);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        Assert.Equal(["describe_screen", "no_such_tool bad_arguments", "open_screen parts"], tools.Calls);
        Assert.Equal(3, budget.Used);
        Assert.Equal(2, result.Calls.Count);

        // The model gets the three answers in its own order.
        var answers = model.Requests[1].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToArray();
        Assert.Equal(["call-1-1", "call-1-2", "call-1-3"], answers.Select(a => a.CallId));
        Assert.Equal(["ok", "bad_arguments", "ok"], answers.Select(a => OutcomeOf(a.Result)));
        Assert.Equal(["describe_screen", "open_screen", "set_field"], model.Requests[1].Options!.Tools!.Select(t => t.Name));

        var unknown = Assert.Single(recorder.Recorded);
        Assert.Equal("no_such_tool", unknown.Tool);
        Assert.Equal("""{"x":1}""", unknown.Arguments.GetRawText());
        Assert.Equal("bad_arguments", unknown.Outcome);
        Assert.True(unknown.Ms >= 0);
    }

    [Fact]
    public async Task A_batch_at_the_budget_edge_is_budgeted_in_the_models_order()
    {
        // Two calls left: the unknown call takes the first, the first describe the second, and the
        // second describe is refused, which ends the run.
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "calls": [
                  { "call": "no_such_tool", "args": {} },
                  { "call": "describe_screen", "args": {} },
                  { "call": "describe_screen", "args": {} } ] },
              { "reply": "Never sent." }
            ]
            """));
        var budget = new ToolBudget(2);
        var tools = new StubTools(budget);
        var recorder = new ListRecorder(tools);
        var engine = new ChatClientEngine("fake", "scripted", model, budget, FiveMinutes, recorder);

        var result = await engine.RunAsync("Look.", tools.Functions, Cancel);

        Assert.Equal(EngineOutcome.ToolLimit, result.Outcome);
        Assert.Equal(["no_such_tool bad_arguments", "describe_screen", "describe_screen refused"], tools.Calls);
        Assert.Equal("bad_arguments", Assert.Single(recorder.Recorded).Outcome);
    }

    [Fact]
    public async Task Unknown_tool_call_is_in_the_tools_records_in_order()
    {
        using var app = RunningApp.Start();
        var budget = new ToolBudget();
        var tools = new WorkshopTools(app.App.Client, new ScriptedGate(approve: false), budget);
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "call": "describe_screen", "args": {} },
              { "call": "delete_everything", "args": { "really": "yes" } },
              { "call": "list_screens", "args": {} },
              { "reply": "Done." }
            ]
            """));
        var engine = new ChatClientEngine("fake", "scripted", model, budget, FiveMinutes, tools);

        var result = await engine.RunAsync("Look.", tools.Functions, Cancel);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        Assert.Equal([1, 2, 3], tools.Records.Select(r => r.Index));
        Assert.Equal(["describe_screen", "delete_everything", "list_screens"], tools.Records.Select(r => r.Tool));
        Assert.Equal(["ok", "bad_arguments", "ok"], tools.Records.Select(r => r.Outcome));
        var unknown = tools.Records[1];
        Assert.Equal("""{"really":"yes"}""", unknown.Arguments.GetRawText());
        Assert.Equal(
            "There is no tool 'delete_everything'. The tools are list_screens, describe_screen, open_screen, set_field, select_row, press_button.",
            unknown.Message);
        Assert.Null(unknown.ScreenId);
        Assert.Null(unknown.Approved);
        Assert.Equal(3, budget.Used);
    }

    [Fact]
    public void Instructions_cover_the_rules()
    {
        var text = AgentInstructions.Text;

        Assert.All(
            [
                "only the tools",
                "describe the current screen",
                "every outcome and message",
                "rejects",
                "never guess",
                "cannot be done",
                "facts",
            ],
            phrase => Assert.Contains(phrase, text, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain('\n', text);
    }

    private static async Task AssertPropagates(Exception failure)
    {
        var model = new ScriptedChatClient(Script.Parse("""
            [
              { "call": "fail", "args": {} },
              { "reply": "Never sent." }
            ]
            """));
        var fail = AIFunctionFactory.Create(string () => throw failure, "fail");
        var engine = new ChatClientEngine("fake", "scripted", model, new ToolBudget(), FiveMinutes);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => engine.RunAsync("Look.", [fail], Cancel));

        Assert.Same(failure, thrown);
        Assert.Single(model.Requests);
    }

    private static string? OutcomeOf(object? result)
    {
        var text = result switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            _ => null,
        };
        using var json = JsonDocument.Parse(text!);
        return json.RootElement.GetProperty("outcome").GetString();
    }

    private static Func<TimeSpan, CancellationToken, Task> Record(List<TimeSpan> waits) => (wait, _) =>
    {
        waits.Add(wait);
        return Task.CompletedTask;
    };

    /// <summary>A model call that never answers: it ignores cancellation, or turns it into its own exception.</summary>
    private sealed class HungModel(bool throwWhenCancelled) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (!throwWhenCancelled)
            {
                return await new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("The model's connection was closed.");
            }

            throw new UnreachableException();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Keeps what the engine reports of the calls it answers itself, and notes them among the stub tools' calls.</summary>
    private sealed class ListRecorder(StubTools tools) : IToolCallRecorder
    {
        private readonly List<(string Tool, JsonElement Arguments, string Outcome, string Message, double Ms)> recorded = [];

        public IReadOnlyList<(string Tool, JsonElement Arguments, string Outcome, string Message, double Ms)> Recorded => recorded;

        public void RecordUnknown(string tool, JsonElement arguments, string outcome, string message, double ms)
        {
            recorded.Add((tool, arguments, outcome, message, ms));
            tools.Note($"{tool} {outcome}");
        }

        public void ModelCallAnswered(int index)
        {
            // The engine's own tests follow the calls through the stub tools; the turn index is tested end to end.
        }
    }

    /// <summary>A model that says "Let me look." beside its one tool call, then replies "Done.".</summary>
    private sealed class TalkativeModel : IChatClient
    {
        private int calls;

        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new TextContent("Let me look."), new FunctionCallContent("call-1", "describe_screen")]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Stand-ins for the app's tools: they take from the budget as the real ones do, and answer
    /// <c>tool_limit</c> when it is spent. <see cref="WithWait"/> adds <c>wait_forever</c>, which waits until cancelled.
    /// </summary>
    private sealed class StubTools
    {
        private readonly ToolBudget budget;
        private readonly List<string> calls = [];

        public StubTools(ToolBudget? budget = null)
        {
            this.budget = budget ?? new ToolBudget();
            Functions =
            [
                AIFunctionFactory.Create(() => Take("describe_screen"), "describe_screen"),
                AIFunctionFactory.Create((string screen) => Take($"open_screen {screen}"), "open_screen"),
                AIFunctionFactory.Create((string field, string value) => Take($"set_field {field}={value}"), "set_field"),
            ];
            WithWait = [.. Functions, AIFunctionFactory.Create(WaitForeverAsync, "wait_forever")];
        }

        public IReadOnlyList<AIFunction> Functions { get; }

        public IReadOnlyList<AIFunction> WithWait { get; }

        public IReadOnlyList<string> Calls => calls;

        public void Note(string call) => calls.Add(call);

        private string Take(string call)
        {
            if (!budget.TryTake())
            {
                calls.Add($"{call} refused");
                return """{"outcome":"tool_limit","message":"The tool-call limit is reached."}""";
            }

            calls.Add(call);
            return """{"outcome":"ok"}""";
        }

        private async Task<string> WaitForeverAsync(CancellationToken ct)
        {
            calls.Add("wait_forever");
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        }
    }
}
