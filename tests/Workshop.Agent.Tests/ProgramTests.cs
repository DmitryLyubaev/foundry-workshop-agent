using System.Text.Json;

namespace Workshop.Agent.Tests;

/// <summary>The command line: <c>run</c> and <c>scenarios check</c>.</summary>
public sealed class ProgramTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("gpt")]
    [InlineData("claude")]
    public async Task Real_engines_without_their_Foundry_settings_name_the_first_missing(string engine)
    {
        // No FWA_* variable and no option: nothing is read, started or called.
        var (code, output, error) = await Run(_ => null, "run", "--engine", engine, "--scenarios", RepoPaths.Scenarios);

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Equal("FWA_PROJECT_ENDPOINT is not set: set it, or pass --project-endpoint.", error.Trim());
    }

    [Fact]
    public async Task Real_engines_take_their_settings_from_the_command_line_too()
    {
        // Every setting but the GPT deployment comes from an option: the error names the one still missing.
        var (code, _, error) = await Run(
            _ => null,
            "run", "--engine", "gpt", "--scenarios", RepoPaths.Scenarios,
            "--project-endpoint", "https://example.services.ai.azure.com/api/projects/example",
            "--resource-endpoint", "https://example.services.ai.azure.com/",
            "--claude-deployment", "claude-haiku-4-5");

        Assert.Equal(2, code);
        Assert.Equal("FWA_GPT_DEPLOYMENT is not set: set it, or pass --gpt-deployment.", error.Trim());
    }

    [Theory]
    [InlineData("gpt")]
    [InlineData("claude")]
    public async Task Real_engines_without_scenarios_print_the_usage(string engine)
    {
        var (code, output, error) = await Run(_ => null, "run", "--engine", engine);

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Contains("Workshop.Agent run --engine fake|gpt|claude --scenarios <dir>", error, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("--out", "--study")]
    [InlineData("--out", "--frozen")]
    [InlineData("--script-dir", "--trace")]
    [InlineData("--only", "--x")]
    public async Task An_option_never_takes_a_flag_or_an_option_as_its_value(string option, string value)
    {
        // `--out --study` would otherwise run one unverified pass into a directory named --study.
        string[] args = ["run", "--engine", "fake", "--scenarios", RepoPaths.Scenarios, "--only", "s05", "--script-dir", RepoPaths.Scripts];
        args = option == "--only"
            ? ["run", "--engine", "fake", "--scenarios", RepoPaths.Scenarios, "--script-dir", RepoPaths.Scripts, option, value]
            : option == "--script-dir"
                ? ["run", "--engine", "fake", "--scenarios", RepoPaths.Scenarios, "--only", "s05", option, value]
                : [.. args, option, value];

        var (code, output, error) = await Run(args);

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Contains("Workshop.Agent run --engine fake|gpt|claude --scenarios <dir>", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(value));
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

    /// <summary>Runs the command line with <paramref name="environment"/> in place of the process's variables.</summary>
    private static async Task<(int Code, string Output, string Error)> Run(Func<string, string?> environment, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Program.RunAsync(args, output, error, environment, Cancel);
        return (code, output.ToString(), error.ToString());
    }
}
