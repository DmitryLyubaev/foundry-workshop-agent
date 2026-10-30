using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Workshop.Agent.Engines;

namespace Portfolio.Analyst.Tests;

/// <summary>
/// A run: each frozen question asked in a fresh conversation, once per pass, one record each; and no
/// run at all when anything drifted from the freeze.
/// </summary>
public sealed class AnalystRunTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static Task<(int Code, string Output, string Error)> Run(Frozen frozen, IAnalystEngine engine, int passes = 1) =>
        Run(frozen.RunArgs(passes), (_, _) => Task.FromResult(engine));

    private static async Task<(int Code, string Output, string Error)> Run(string[] args, Func<AnalystSetup, CancellationToken, Task<IAnalystEngine>> engines, Func<string, string?>? environment = null)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Program.RunAsync(args, output, error, environment ?? Frozen.Environment(), engines, Cancel);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task A_two_question_one_pass_run_writes_two_records()
    {
        using var frozen = new Frozen();
        var model = Frozen.Scripted("Project p2 spent A$1,234.56 in total.", "The data cannot answer that: it holds no model versions.");

        var (code, output, error) = await Run(frozen, new ChatClientAnalystEngine(model));

        Assert.Equal("", error);
        Assert.Equal(0, code);
        var records = frozen.Records();
        Assert.Equal(2, records.Length);
        Assert.Equal(["q01", "q15"], records.Select(r => r["question_id"]!.GetValue<string>()));
        Assert.All(records, r => SchemaValidator.Validate(r, TestFiles.RecordSchema()));
        Assert.All(records, r => Assert.Equal("foundry", r["route"]!.GetValue<string>()));
        Assert.All(records, r => Assert.Equal(frozen.Short, r["freeze"]!.GetValue<string>()));
        Assert.All(records, r => Assert.Equal(1, r["pass"]!.GetValue<int>()));
        Assert.Equal("Project p2 spent A$1,234.56 in total.", records[0]["answer_text"]!.GetValue<string>());
        Assert.All(records, r => Assert.Equal(ScriptedChatClient.InputTokensPerResponse, r["input_tokens"]!.GetValue<long>()));
        Assert.All(records, r => Assert.Equal(ScriptedChatClient.OutputTokensPerResponse, r["output_tokens"]!.GetValue<long>()));
        Assert.All(records, r => Assert.Equal(0, r["tool_calls"]!.GetValue<int>()));
        Assert.Contains("q01 pass 1: ", output, StringComparison.Ordinal);
        Assert.Contains($"wrote 2 records to foundry-{frozen.Short}-", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_question_is_a_fresh_conversation()
    {
        using var frozen = new Frozen();
        var model = Frozen.Scripted("A$1,234.56", "No answer.", "A$1,234.56", "No answer.");

        var (code, _, _) = await Run(frozen, new ChatClientAnalystEngine(model), passes: 2);

        Assert.Equal(0, code);
        Assert.Equal(4, model.Requests.Count);
        var texts = frozen.Questions.Select(q => q!["text"]!.GetValue<string>()).ToArray();
        Assert.Equal([texts[0], texts[1], texts[0], texts[1]], model.Requests.Select(r => Assert.Single(r.Messages).Text));
        Assert.All(model.Requests, r => Assert.Equal(ChatRole.User, Assert.Single(r.Messages).Role));
        Assert.All(model.Requests, r => Assert.Null(r.Options?.ConversationId));
        Assert.Equal([1, 1, 2, 2], frozen.Records().Select(r => r["pass"]!.GetValue<int>()));
    }

    [Fact]
    public async Task The_output_file_is_partial_until_every_pass_is_done()
    {
        using var frozen = new Frozen();
        // The second question's call fails: the run stops with what it has, never looking complete.
        var model = new ScriptedChatClient(new Script([new ScriptStep.Reply("A$1,234.56")]));

        var (code, _, error) = await Run(frozen, new ChatClientAnalystEngine(model));

        Assert.Equal(3, code);
        var file = Assert.Single(frozen.OutFiles());
        Assert.EndsWith(".jsonl.partial", file, StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(Path.Combine(frozen.Out, file)));
        Assert.StartsWith("the Foundry agent failed", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_optional_question_is_asked_as_frozen()
    {
        // q12 is optional and may have no expected answer: the Fabric side decides what is scored, so it is still asked.
        var questions = Frozen.TwoQuestions();
        questions.Insert(1, new JsonObject { ["id"] = "q12", ["text"] = "How many calls went through the gateway?", ["kind"] = "count", ["optional"] = true });
        using var frozen = new Frozen(questions);

        var (code, _, _) = await Run(frozen, new ChatClientAnalystEngine(Frozen.Scripted("A$1,234.56", "12", "No answer.")));

        Assert.Equal(0, code);
        Assert.Equal(["q01", "q12", "q15"], frozen.Records().Select(r => r["question_id"]!.GetValue<string>()));
    }

    public static TheoryData<string> Drifts => ["questions", "model_foundry", "freeze"];

    [Theory]
    [MemberData(nameof(Drifts))]
    public async Task Run_refuses_on_drift_before_anything_is_asked(string drift)
    {
        using var frozen = new Frozen();
        var args = frozen.RunArgs();
        switch (drift)
        {
            case "questions":
                File.AppendAllText(frozen.QuestionsPath, " ");
                break;
            case "model_foundry":
                args[^1] = Frozen.Model + "-mini";
                break;
            default:
                var freeze = JsonNode.Parse(File.ReadAllText(frozen.FreezePath))!;
                freeze["components"]!["analyst_instructions"] = new string('0', 64);
                File.WriteAllText(frozen.FreezePath, freeze.ToJsonString());
                break;
        }

        var started = false;
        var (code, output, error) = await Run(args, (_, _) =>
        {
            started = true;
            return Task.FromResult<IAnalystEngine>(new ChatClientAnalystEngine(Frozen.Scripted("A$1", "A$2")));
        });

        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Equal($"the freeze is broken: {(drift == "freeze" ? "analyst_instructions" : drift)} changed", error.Trim());
        Assert.False(started);
        Assert.Empty(frozen.OutFiles());
    }

    [Fact]
    public async Task Run_refuses_on_drift_even_without_the_environment()
    {
        using var frozen = new Frozen();
        File.AppendAllText(frozen.QuestionsPath, "\n ");

        var (code, _, error) = await Run(frozen.RunArgs(), (_, _) => throw new InvalidOperationException("not reached"), _ => null);

        Assert.Equal(1, code);
        Assert.Equal("the freeze is broken: questions changed", error.Trim());
    }

    [Fact]
    public async Task The_engine_gets_the_frozen_instructions_and_the_model()
    {
        using var frozen = new Frozen();
        AnalystSetup? seen = null;

        var (code, _, _) = await Run(frozen.RunArgs(), (setup, _) =>
        {
            seen = setup;
            return Task.FromResult<IAnalystEngine>(new ChatClientAnalystEngine(Frozen.Scripted("A$1", "A$2")));
        });

        Assert.Equal(0, code);
        Assert.NotNull(seen);
        Assert.Equal(Frozen.Model, seen.Model);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(FreezeHash.Lf(TestFiles.Instructions())), seen.Instructions);
        Assert.Equal(new Uri(Frozen.McpUrl), seen.McpUrl);
        Assert.Equal(Frozen.ConnectionId, seen.ConnectionId);
        Assert.Equal(new Uri(Frozen.ProjectEndpoint), seen.ProjectEndpoint);
        Assert.DoesNotContain("sentinel", seen.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_all_writes_one_record_per_question_per_pass()
    {
        // The loop on its own, with a fixed clock: wall time and start time come from it.
        var time = new SteppingTime(new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero), TimeSpan.FromMilliseconds(1_500));
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        AnalystQuestion[] questions = [new("q01", "How much?", Optional: false), new("q02", "How many?", Optional: false)];

        await AnalystRun.AskAllAsync(new ChatClientAnalystEngine(Frozen.Scripted("A$1", "2")), questions, 1, new RecordWriter(text, "1b18407a2310", []), output, time, Cancel);

        var records = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToArray();
        Assert.Equal(2, records.Length);
        Assert.All(records, r => Assert.Equal(1_500, r["wall_ms"]!.GetValue<long>()));
        Assert.Equal("2026-11-08T09:00:00.000Z", records[0]["started_utc"]!.GetValue<string>());
        Assert.Equal("q01 pass 1: 1.5 s\nq02 pass 1: 1.5 s\n", output.ToString().ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{\"id\": \"Q1\", \"text\": \"x\"}]")]
    [InlineData("[{\"id\": \"q01\"}]")]
    [InlineData("[{\"id\": \"q01\", \"text\": \"x\"}, {\"id\": \"q01\", \"text\": \"y\"}]")]
    public void Questions_that_cannot_be_asked_are_an_input_error(string json)
    {
        Assert.Throws<InvalidDataException>(() => AnalystRun.ReadQuestions(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>A clock whose every reading is <paramref name="step"/> after the last.</summary>
    private sealed class SteppingTime(DateTimeOffset start, TimeSpan step) : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(ticks);

        public override long GetTimestamp()
        {
            var now = ticks;
            ticks += step.Ticks;
            return now;
        }
    }
}
