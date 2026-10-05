using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workshop.Agent.Engines;
using Workshop.Agent.Runner;
using Workshop.Agent.Scenarios;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests;

/// <summary>The freeze: what <c>scenarios freeze</c> writes, and the runs that refuse when anything drifted.</summary>
public sealed class FreezeTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Freeze_writes_every_hash()
    {
        using var run = TempRun.Create();
        var scenarios = CopyScenarios(run);

        var (code, output, error) = await Run("scenarios", "freeze", scenarios);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Contains("20 scenarios", output, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(scenarios, "freeze.json")));
        var root = json.RootElement;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", root.GetProperty("frozenOn").GetString());

        // One SHA-256 per scenario file, over the file's bytes, and none for the freeze itself.
        var hashes = root.GetProperty("scenarios").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"s{i:00}.json"), hashes.Keys);
        foreach (var (name, hash) in hashes)
        {
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(scenarios, name)))), hash);
        }

        Assert.Equal(AgentInstructions.Sha256, root.GetProperty("instructionsSha256").GetString());
        Assert.Equal(AgentSettings.Sha256, root.GetProperty("settingsSha256").GetString());
        Assert.Equal(ToolSchemas.Sha256(WorkshopTools.Declarations), root.GetProperty("toolsSha256").GetString());

        var rule = root.GetProperty("decisionRule");
        Assert.Equal(0.10, rule.GetProperty("threshold").GetDouble());
        Assert.Equal(20261004, rule.GetProperty("seed").GetInt32());
        Assert.Equal(10000, rule.GetProperty("resamples").GetInt32());
        Assert.Equal(3, rule.GetProperty("passes").GetInt32());

        Assert.Null(Freeze.Verify(scenarios));
    }

    [Fact]
    public async Task Freeze_does_not_load_as_a_scenario_and_is_not_overwritten()
    {
        using var run = TempRun.Create();
        var scenarios = CopyScenarios(run);
        await Run("scenarios", "freeze", scenarios);

        Assert.Equal(20, ScenarioLoader.LoadAll(scenarios).Length);
        var before = File.ReadAllBytes(Path.Combine(scenarios, "freeze.json"));

        var (code, _, error) = await Run("scenarios", "freeze", scenarios);

        Assert.Equal(1, code);
        Assert.Contains("already exists", error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(scenarios, "freeze.json")));
    }

    [Fact]
    public async Task Freeze_refuses_an_invalid_scenario_set()
    {
        using var run = TempRun.Create();
        File.WriteAllText(Path.Combine(run.Directory, "s01.json"), "{}");

        var (code, _, error) = await Run("scenarios", "freeze", run.Directory);

        Assert.Equal(1, code);
        Assert.StartsWith("The scenario file s01.json ", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(run.Directory, "freeze.json")));
    }

    [Fact]
    public async Task Study_refuses_an_edited_scenario()
    {
        using var run = TempRun.Create();
        var scenarios = await Frozen(run);
        var file = Path.Combine(scenarios, "s07.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("stress test", "stress tests", StringComparison.Ordinal));
        var outDir = Path.Combine(run.Directory, "out");

        // The GPT engine with no Foundry setting at all: the refusal comes before the settings are read, so before any model call.
        var (code, output, error) = await Run(_ => null, "run", "--engine", "gpt", "--scenarios", scenarios, "--study", "--out", outDir);

        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.StartsWith("The freeze is broken: s07.json was edited", error, StringComparison.Ordinal);
        Assert.Contains("No run starts.", error, StringComparison.Ordinal);
        Assert.DoesNotContain("FWA_", error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(run.Directory, "out*"));
    }

    [Fact]
    public async Task Study_names_the_first_of_several_drifts_in_scenario_order()
    {
        using var run = TempRun.Create();
        var scenarios = await Frozen(run);
        foreach (var id in new[] { "s12", "s03" })
        {
            File.AppendAllText(Path.Combine(scenarios, $"{id}.json"), " ");
        }

        var (_, _, error) = await Run(_ => null, "run", "--engine", "gpt", "--scenarios", scenarios, "--study");

        Assert.StartsWith("The freeze is broken: s03.json was edited", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Study_refuses_a_scenario_added_or_removed_since_the_freeze()
    {
        using var run = TempRun.Create();
        var scenarios = await Frozen(run);
        File.Copy(Path.Combine(scenarios, "s01.json"), Path.Combine(scenarios, "s21.json"));

        var (_, _, added) = await Run(_ => null, "run", "--engine", "gpt", "--scenarios", scenarios, "--study");

        Assert.StartsWith("The freeze is broken: s21.json is not in the freeze", added, StringComparison.Ordinal);

        File.Delete(Path.Combine(scenarios, "s21.json"));
        File.Delete(Path.Combine(scenarios, "s20.json"));

        var (_, _, removed) = await Run(_ => null, "run", "--engine", "gpt", "--scenarios", scenarios, "--study");

        Assert.StartsWith("The freeze is broken: s20.json is in the freeze but not in", removed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Study_refuses_a_directory_with_no_freeze()
    {
        using var run = TempRun.Create();
        var scenarios = CopyScenarios(run);

        var (code, _, error) = await Run(_ => null, "run", "--engine", "gpt", "--scenarios", scenarios, "--study");

        Assert.Equal(1, code);
        Assert.StartsWith("There is no freeze.json in", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("instructionsSha256", "instructions")]
    [InlineData("settingsSha256", "settings")]
    [InlineData("toolsSha256", "tools")]
    public async Task Study_refuses_changed_instructions_settings_or_tools(string field, string name)
    {
        // The code's instructions, settings or tools differ from the frozen ones exactly when the freeze's hash differs from the code's.
        using var run = TempRun.Create();
        var scenarios = await Frozen(run);
        Edit(scenarios, root => root[field] = new string('0', 64));

        var (code, _, error) = await Run(_ => null, "run", "--engine", "gpt", "--scenarios", scenarios, "--study");

        Assert.Equal(1, code);
        Assert.StartsWith($"The freeze is broken: the {name} ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Study_refuses_a_changed_decision_rule()
    {
        using var run = TempRun.Create();
        var scenarios = await Frozen(run);
        Edit(scenarios, root => root["decisionRule"]!["threshold"] = 0.05);

        var (code, _, error) = await Run(_ => null, "run", "--engine", "gpt", "--scenarios", scenarios, "--study");

        Assert.Equal(1, code);
        Assert.StartsWith("The freeze is broken: the decision rule ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frozen_refuses_the_same_drift_with_any_pass_count()
    {
        using var run = TempRun.Create();
        var scenarios = await Frozen(run);
        File.AppendAllText(Path.Combine(scenarios, "s02.json"), " ");

        var (code, output, error) = await Run(_ => null, "run", "--engine", "gpt", "--scenarios", scenarios, "--frozen", "--passes", "1");

        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.StartsWith("The freeze is broken: s02.json was edited", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Study_runs_exactly_three_passes()
    {
        using var run = TempRun.Create();
        var scenarios = await Frozen(run);

        var (code, _, error) = await Run(_ => null, "run", "--engine", "gpt", "--scenarios", scenarios, "--study", "--passes", "1");

        Assert.Equal(2, code);
        Assert.Contains("--study runs exactly 3 passes", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Study_runs_when_everything_matches()
    {
        // A study runs every scenario of its set, so the set frozen here is s05 alone.
        using var run = TempRun.Create();
        var scenarios = await Frozen(run, only: "s05");
        var shortHash = Freeze.ShortHash(scenarios);
        var outDir = Path.Combine(run.Directory, "out");

        var (code, output, error) = await Run("run", "--engine", "fake", "--scenarios", scenarios, "--study", "--script-dir", RepoPaths.Scripts, "--out", outDir);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Equal(12, shortHash.Length);
        // Three passes though none was asked for, in a directory that names the freeze it ran under.
        var studyDir = $"{outDir}-{shortHash}";
        Assert.Contains($"Transcripts go to {studyDir}", output, StringComparison.Ordinal);
        Assert.Equal(["s05.fake.p1.json", "s05.fake.p2.json", "s05.fake.p3.json"], Directory.GetFiles(studyDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(outDir));
    }

    [Theory]
    [InlineData("\\")]
    [InlineData("/")]
    public async Task Study_names_its_directory_beside_an_out_given_with_a_trailing_separator(string separator)
    {
        using var run = TempRun.Create();
        var scenarios = await Frozen(run, only: "s05");
        var outDir = Path.Combine(run.Directory, "out");

        var (code, output, error) = await Run("run", "--engine", "fake", "--scenarios", scenarios, "--study", "--script-dir", RepoPaths.Scripts, "--out", outDir + separator);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        var studyDir = $"{outDir}-{Freeze.ShortHash(scenarios)}";
        Assert.Contains($"Transcripts go to {studyDir}{Environment.NewLine}", output, StringComparison.Ordinal);
        Assert.Equal(3, Directory.GetFiles(studyDir).Length);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public async Task Study_refuses_only_and_points_to_frozen()
    {
        // A directory named for the study must hold the whole study, never a part of it.
        using var run = TempRun.Create();
        var scenarios = await Frozen(run);
        var outDir = Path.Combine(run.Directory, "out");

        var (code, output, error) = await Run(_ => null, "run", "--engine", "gpt", "--scenarios", scenarios, "--study", "--only", "s05", "--out", outDir);

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Equal("--study runs every scenario; leave out --only, or use --frozen to run one.", error.Trim());
        Assert.Empty(Directory.GetDirectories(run.Directory, "out*"));
    }

    [Fact]
    public async Task A_freeze_that_cannot_be_read_is_broken_not_a_crash()
    {
        using var run = TempRun.Create();
        var scenarios = await Frozen(run);

        // Held open with no sharing: reading it fails as a locked or unreadable file would.
        using (new FileStream(Path.Combine(scenarios, Freeze.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.StartsWith("The freeze is broken: freeze.json cannot be read: ", Freeze.Verify(scenarios), StringComparison.Ordinal);

            var (code, output, error) = await Run(_ => null, "run", "--engine", "gpt", "--scenarios", scenarios, "--frozen");

            Assert.Equal(1, code);
            Assert.Equal("", output);
            Assert.StartsWith("The freeze is broken: freeze.json cannot be read: ", error, StringComparison.Ordinal);
            Assert.Contains("No run starts.", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Frozen_runs_when_everything_matches_with_any_pass_count_and_keeps_the_out_directory()
    {
        using var run = TempRun.Create();
        var scenarios = await Frozen(run);
        var outDir = Path.Combine(run.Directory, "out");

        var (code, _, error) = await Run("run", "--engine", "fake", "--scenarios", scenarios, "--only", "s05", "--frozen", "--passes", "1", "--script-dir", RepoPaths.Scripts, "--out", outDir);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Equal(["s05.fake.p1.json"], Directory.GetFiles(outDir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Non_study_runs_ignore_the_freeze()
    {
        using var run = TempRun.Create();
        var scenarios = await Frozen(run);
        // An edited scenario, and then a freeze that cannot be read: each would stop a study run.
        var file = Path.Combine(scenarios, "s05.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("hinge creaks", "hinge squeaks", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(scenarios, "freeze.json"), "not json");
        var outDir = Path.Combine(run.Directory, "out");

        var (code, _, error) = await Run("run", "--engine", "fake", "--scenarios", scenarios, "--only", "s05", "--script-dir", RepoPaths.Scripts, "--out", outDir);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Single(Directory.GetFiles(outDir));
    }

    [Fact]
    public async Task A_scenario_edited_after_the_run_began_stops_the_next_run()
    {
        // The runner checks the freeze again before every run, so an edit made after the first check cannot reach a later one.
        using var run = TempRun.Create();
        var scenarios = await Frozen(run);
        var scenario = ScenarioLoader.LoadAll(scenarios).Single(s => s.Id == "s05");
        File.AppendAllText(Path.Combine(scenarios, "s05.json"), " ");
        var runner = new ScenarioRunner(
            "unused.exe",
            (_, _) => throw new InvalidOperationException("The engine must not be built."),
            ScenarioRunner.GateFor)
        {
            FrozenScenarios = scenarios,
        };

        var e = await Assert.ThrowsAsync<FreezeBrokenException>(() => runner.RunAsync(scenario, 1, run.Directory, Cancel));

        Assert.StartsWith("The freeze is broken: s05.json was edited", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tests_copy_only_the_scenarios_when_the_committed_folder_holds_a_freeze()
    {
        // Runbook step 2 commits scenarios/freeze.json beside the scenarios and their README. The
        // copies the tests freeze must still be bare scenario sets, or every test that freezes one
        // finds a freeze already there and CI turns red on the freeze's own PR.
        using var run = TempRun.Create();
        var committed = Path.Combine(run.Directory, "committed");
        Directory.CreateDirectory(committed);
        foreach (var file in Directory.GetFiles(RepoPaths.Scenarios))
        {
            File.Copy(file, Path.Combine(committed, Path.GetFileName(file)));
        }

        File.WriteAllText(Path.Combine(committed, "README.md"), "# Scenarios\n");
        if (!File.Exists(Path.Combine(committed, Freeze.FileName)))
        {
            // Before step 2 there is no committed freeze to copy: make one, as the owner will.
            Assert.Equal(0, (await Run("scenarios", "freeze", committed)).Code);
        }

        var scenarios = CopyScenarios(run, committed, "copy");

        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"s{i:00}.json"), Directory.GetFiles(scenarios).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var (code, output, error) = await Run("scenarios", "freeze", scenarios);
        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Contains("20 scenarios", output, StringComparison.Ordinal);
        Assert.Null(Freeze.Verify(scenarios));
    }

    /// <summary>
    /// A copy of the scenario files of <paramref name="from"/> (the committed 20 by default) in a temp
    /// directory: the tests never touch the committed ones. Only the scenario files are copied, never
    /// a committed <c>freeze.json</c> or the README beside them.
    /// </summary>
    private static string CopyScenarios(TempRun run, string? from = null, string name = "scenarios", Func<string, bool>? only = null)
    {
        var scenarios = Path.Combine(run.Directory, name);
        Directory.CreateDirectory(scenarios);
        foreach (var file in ScenarioLoader.Files(from ?? RepoPaths.Scenarios).Where(f => only?.Invoke(Path.GetFileNameWithoutExtension(f)) ?? true))
        {
            File.Copy(file, Path.Combine(scenarios, Path.GetFileName(file)));
        }

        return scenarios;
    }

    private static async Task<string> Frozen(TempRun run, string? only = null)
    {
        var scenarios = CopyScenarios(run, only: only is null ? null : id => id == only);
        var (code, _, error) = await Run("scenarios", "freeze", scenarios);
        Assert.Equal(0, code);
        Assert.Equal("", error);
        return scenarios;
    }

    private static void Edit(string scenarios, Action<JsonNode> change)
    {
        var path = Path.Combine(scenarios, "freeze.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        change(root);
        File.WriteAllText(path, root.ToJsonString());
    }

    private static async Task<(int Code, string Output, string Error)> Run(params string[] args) =>
        await Run(Environment.GetEnvironmentVariable, args);

    private static async Task<(int Code, string Output, string Error)> Run(Func<string, string?> environment, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Program.RunAsync(args, output, error, environment, Cancel);
        return (code, output.ToString(), error.ToString());
    }
}
