using System.Text.Json;

namespace Workshop.Agent.Tests;

/// <summary>The command line: <c>run</c> and <c>scenarios check</c>.</summary>
public sealed class ProgramTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("gpt", true)]
    [InlineData("claude", true)]
    [InlineData("gpt", false)]
    [InlineData("claude", false)]
    public async Task Real_engines_arrive_in_plan_3(string engine, bool withScenarios)
    {
        var (code, output, error) = withScenarios
            ? await Run("run", "--engine", engine, "--scenarios", RepoPaths.Scenarios)
            : await Run("run", "--engine", engine);

        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Equal($"The {engine} engine arrives in plan 3.", error.Trim());
    }

    [Fact]
    public async Task Run_on_an_invalid_scenario_set_exits_1()
    {
        using var run = TempRun.Create();
        File.WriteAllText(Path.Combine(run.Directory, "s01.json"), "{}");

        var (code, output, error) = await Run("run", "--engine", "fake", "--scenarios", run.Directory, "--script-dir", RepoPaths.Scripts, "--out", run.Directory);

        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.StartsWith("The scenario file s01.json ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_with_a_missing_app_exits_1()
    {
        using var run = TempRun.Create();
        var missing = Path.Combine(run.Directory, "Workshop.App.exe");

        var (code, output, error) = await Run("run", "--engine", "fake", "--scenarios", RepoPaths.Scenarios, "--only", "s05", "--script-dir", RepoPaths.Scripts, "--out", run.Directory, "--app", missing);

        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Equal($"There is no app at '{missing}'.", error.Trim());
        Assert.Empty(Directory.GetFiles(run.Directory));
    }

    [Fact]
    public async Task Scenarios_check_reads_the_set()
    {
        var (code, output, error) = await Run("scenarios", "check", RepoPaths.Scenarios);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Equal("20 scenarios are valid.", output.Trim());
    }

    [Fact]
    public async Task Scenarios_check_names_a_bad_file()
    {
        using var run = TempRun.Create();
        File.WriteAllText(Path.Combine(run.Directory, "s01.json"), "{}");

        var (code, _, error) = await Run("scenarios", "check", run.Directory);

        Assert.Equal(1, code);
        Assert.StartsWith("The scenario file s01.json ", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("walk")]
    [InlineData("run", "--engine", "fake")]
    [InlineData("run", "--engine", "fake", "--scenarios", "x", "--passes", "0")]
    [InlineData("run", "--engine", "fake", "--scenarios", "x", "--color", "red")]
    [InlineData("run", "--engine", "slow", "--scenarios", "x")]
    public async Task A_bad_command_line_shows_the_usage(params string[] args)
    {
        var (code, _, error) = await Run(args);

        Assert.Equal(2, code);
        Assert.Contains("Workshop.Agent run --engine fake|gpt|claude --scenarios <dir>", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_needs_its_scripts()
    {
        using var run = TempRun.Create();

        var (code, _, error) = await Run("run", "--engine", "fake", "--scenarios", RepoPaths.Scenarios, "--only", "s05", "--script-dir", run.Directory, "--out", run.Directory);

        Assert.Equal(2, code);
        Assert.Contains(Path.Combine(run.Directory, "s05.correct.json"), error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_scenario_is_refused()
    {
        var (code, _, error) = await Run("run", "--engine", "fake", "--scenarios", RepoPaths.Scenarios, "--only", "s99", "--script-dir", RepoPaths.Scripts);

        Assert.Equal(2, code);
        Assert.Contains("There is no scenario 's99'", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_fake_writes_a_transcript_per_pass()
    {
        using var run = TempRun.Create();

        var (code, output, error) = await Run("run", "--engine", "fake", "--scenarios", RepoPaths.Scenarios, "--only", "s05", "--passes", "2", "--script-dir", RepoPaths.Scripts, "--out", run.Directory);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Contains("s05 p1: success", output, StringComparison.Ordinal);
        Assert.Contains("s05 p2: success", output, StringComparison.Ordinal);
        foreach (var pass in new[] { 1, 2 })
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run.Directory, $"s05.fake.p{pass}.json")));
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        }
    }

    private static async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Program.RunAsync(args, output, error, Cancel);
        return (code, output.ToString(), error.ToString());
    }
}
