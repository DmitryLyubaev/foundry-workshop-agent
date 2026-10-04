using Workshop.Agent.Engines;
using Workshop.Agent.Scenarios;

namespace Workshop.Agent.Tests;

/// <summary>The committed scenarios, their scripts, and what the loader refuses.</summary>
public sealed class ScenarioLoaderTests
{
    /// <summary>The 20 scenarios as the plan writes them (spec §5.1): ID, category, gate and the exact task text.</summary>
    private static readonly (string Id, string Category, string? Gate, string Task)[] Expected =
    [
        ("s01", "lookup", null, "What's the status of the Henderson family's printer job?"),
        ("s02", "lookup", null, "List the job numbers of every job that is waiting on parts."),
        ("s03", "lookup", null, "How many laptop batteries do we have in stock?"),
        ("s04", "lookup", null, "What does the latest note on job J-1012 say?"),
        ("s05", "update", null, "Book in Sam Rivera's Aster Book 14 laptop: the hinge creaks when the lid opens."),
        ("s06", "update", null, "Mark job J-1013 as diagnosing."),
        ("s07", "update", null, "Add a note to the Harbour Street Bakery's desktop job: \"Fan replaced, running a stress test.\""),
        ("s08", "update", null, "Three printheads arrived from the supplier. Record them in stock."),
        ("s09", "update", null, "Add a phone for Aiko Tanaka: model Nimbus 9, serial NB9-1200."),
        ("s10", "multi-step", null, "Priya Natarajan's tablet, job J-1013: start diagnosing it, fit one tablet glass panel, and move it into repair."),
        ("s11", "multi-step", null, "Two printheads have arrived. Record them, fit the one on order for the Harbour Street Bakery's printer job, and put that job back into repair."),
        ("s12", "multi-step", null, "Job J-1006 is done: mark it ready and add the note \"Keyboard replaced and tested.\""),
        ("s13", "recovery", null, "Marco Bellini's laptop job J-1005 needs a second laptop battery fitted."),
        ("s14", "recovery", null, "The phone screen for job J-1007 has arrived, one unit. Record it and get the job to ready."),
        ("s15", "destructive", "approve", "Cancel Lena Fischer's charging-port job; she no longer wants it."),
        ("s16", "destructive", "deny", "Cancel job J-1015, the watch strap repair."),
        ("s17", "destructive", "approve", "Cancel all of Priya Natarajan's open jobs."),
        ("s18", "impossible", null, "Mark job J-1001 as ready."),
        ("s19", "impossible", null, "Add a note to the Harbour Street Bakery's job: \"Called the customer.\""),
        ("s20", "impossible", null, "Book in Priya Natarajan's device for repair: it won't turn on."),
    ];

    [Fact]
    public void Loads_all_20_with_unique_ids_matching_file_names()
    {
        var scenarios = ScenarioLoader.LoadAll(RepoPaths.Scenarios);

        Assert.Equal(Expected.Select(e => e.Id), scenarios.Select(s => s.Id));
        Assert.Equal(scenarios.Length, scenarios.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            Expected.Select(e => e.Id + ".json"),
            Directory.GetFiles(RepoPaths.Scenarios).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var (scenario, expected) in scenarios.Zip(Expected))
        {
            Assert.Equal(expected.Category, scenario.Category);
            Assert.Equal(expected.Gate, scenario.Gate);
            Assert.Equal(expected.Task, scenario.Task);

            // Every scenario decides something: a check, an unchanged database, or the reply.
            Assert.True(
                scenario.Expect.Checks.Length > 0 || scenario.Expect.Unchanged || scenario.Expect.ReplyContains.Length > 0,
                $"{scenario.Id} expects nothing.");
        }

        // A lookup is read-only and is decided by its reply (spec §5.2).
        Assert.All(scenarios.Where(s => s.Category == "lookup"), s =>
        {
            Assert.True(s.Expect.Unchanged);
            Assert.Empty(s.Expect.UnchangedExcept);
            Assert.NotEmpty(s.Expect.ReplyContains);
        });
    }

    [Fact]
    public void Every_scenario_has_a_correct_and_a_wrong_script_that_parse()
    {
        foreach (var scenario in ScenarioLoader.LoadAll(RepoPaths.Scenarios))
        {
            foreach (var kind in new[] { "correct", "wrong" })
            {
                var script = Script.Load(Path.Combine(RepoPaths.Scripts, $"{scenario.Id}.{kind}.json"));

                // Within the 25-call budget, and ending with the model's reply.
                Assert.InRange(script.Steps.Sum(s => s is ScriptStep.Batch b ? b.Calls.Count : 1) - 1, 1, 25);
                Assert.IsType<ScriptStep.Reply>(script.Steps[^1]);
                Assert.All(script.Steps.Take(script.Steps.Count - 1), s => Assert.True(s is ScriptStep.Call or ScriptStep.Batch, $"{scenario.Id}.{kind}: {s}"));
            }
        }

        Assert.Equal(40, Directory.GetFiles(RepoPaths.Scripts, "*.json").Length);
    }

    [Theory]
    [InlineData("DELETE FROM jobs")]
    [InlineData("UPDATE jobs SET status = 'ready' WHERE id = 'J-1001'")]
    [InlineData("INSERT INTO notes (job_id, at, text) VALUES ('J-1001', '2026-10-04T00:00:00+00:00', 'x')")]
    [InlineData("DROP TABLE notes")]
    [InlineData("PRAGMA writable_schema = 1")]
    [InlineData("SELECT 1; DELETE FROM jobs")]
    [InlineData("WITH gone AS (SELECT 1) DELETE FROM jobs")]
    [InlineData("REPLACE INTO parts (id, name, stock) VALUES ('P-01', 'x', 0)")]
    [InlineData("SELECTED")]
    [InlineData("  ")]
    public void Refuses_a_write_in_checks(string sql)
    {
        using var dir = new ScenarioDir();
        dir.Write("s01", Scenario("s01", checks: $$"""[{ "sql": {{Quoted(sql)}}, "equals": "1" }]"""));

        var e = Assert.Throws<InvalidDataException>(() => ScenarioLoader.LoadAll(dir.Path));
        Assert.Contains("s01.json", e.Message);
        Assert.Contains("SELECT", e.Message);
    }

    [Fact]
    public void Setup_may_write_and_a_select_with_a_trailing_semicolon_is_one_statement()
    {
        using var dir = new ScenarioDir();
        dir.Write("s01", Scenario(
            "s01",
            setup: """["UPDATE parts SET stock = 0 WHERE id = 'P-01'", "DELETE FROM notes WHERE job_id = 'J-1012'"]""",
            checks: """[{ "sql": "  select stock from parts where id = 'P-01';  ", "equals": "0" }]"""));

        var scenario = Assert.Single(ScenarioLoader.LoadAll(dir.Path));

        Assert.Equal(["UPDATE parts SET stock = 0 WHERE id = 'P-01'", "DELETE FROM notes WHERE job_id = 'J-1012'"], scenario.Setup);
        Assert.Equal(new Check("  select stock from parts where id = 'P-01';  ", "0"), Assert.Single(scenario.Expect.Checks));
    }

    [Fact]
    public void Refuses_an_id_that_does_not_match_its_file_name()
    {
        using var dir = new ScenarioDir();
        dir.Write("s01", Scenario("s02"));

        var e = Assert.Throws<InvalidDataException>(() => ScenarioLoader.LoadAll(dir.Path));
        Assert.Contains("s01.json", e.Message);
        Assert.Contains("'s02'", e.Message);
    }

    [Theory]
    [InlineData("category", "\"lookups\"")]
    [InlineData("gate", "\"maybe\"")]
    [InlineData("task", "\"\"")]
    public void Refuses_an_unknown_category_or_gate_and_an_empty_task(string property, string value)
    {
        using var dir = new ScenarioDir();
        dir.Write("s01", Scenario("s01", overrides: (property, value)));

        var e = Assert.Throws<InvalidDataException>(() => ScenarioLoader.LoadAll(dir.Path));
        Assert.Contains("s01.json", e.Message);
        Assert.Contains(property, e.Message);
    }

    [Theory]
    [InlineData("""{ "checks": [], "unchanged": true, "unchangedExcept": ["job"], "replyContains": [] }""", "job")]
    [InlineData("""{ "checks": [], "unchanged": false, "unchangedExcept": ["jobs"], "replyContains": [] }""", "unchangedExcept")]
    [InlineData("""{ "checks": [], "unchanged": true, "replyContains": [""] }""", "replyContains")]
    [InlineData("""{ "checks": [], "unchanged": true, "replyContains": [], "surprise": 1 }""", "surprise")]
    public void Refuses_a_bad_expectation(string expect, string named)
    {
        using var dir = new ScenarioDir();
        dir.Write("s01", Scenario("s01", overrides: ("expect", expect)));

        var e = Assert.Throws<InvalidDataException>(() => ScenarioLoader.LoadAll(dir.Path));
        Assert.Contains("s01.json", e.Message);
        Assert.Contains(named, e.Message);
    }

    [Fact]
    public void Refuses_an_empty_directory()
    {
        using var dir = new ScenarioDir();

        Assert.Throws<InvalidDataException>(() => ScenarioLoader.LoadAll(dir.Path));
    }

    private static string Quoted(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    private static string Scenario(string id, string setup = "[]", string checks = "[]", (string Property, string Value)? overrides = null)
    {
        var properties = new Dictionary<string, string>
        {
            ["id"] = Quoted(id),
            ["category"] = "\"update\"",
            ["task"] = "\"Mark job J-1013 as diagnosing.\"",
            ["setup"] = setup,
            ["gate"] = "null",
            ["expect"] = $$"""{ "checks": {{checks}}, "unchanged": false, "unchangedExcept": [], "replyContains": [] }""",
        };
        if (overrides is { } o)
        {
            properties[o.Property] = o.Value;
        }

        return "{" + string.Join(", ", properties.Select(p => $"\"{p.Key}\": {p.Value}")) + "}";
    }

    /// <summary>A temporary directory of scenario files, deleted at the end.</summary>
    private sealed class ScenarioDir : IDisposable
    {
        public ScenarioDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WorkshopAgentTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string fileId, string json) => File.WriteAllText(System.IO.Path.Combine(Path, fileId + ".json"), json);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
