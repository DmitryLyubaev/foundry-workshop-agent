using System.ClientModel;
using System.Globalization;

namespace Portfolio.Analyst.Tests;

/// <summary>
/// The command line: <c>run</c> and <c>verify</c>. No URL or ID reaches standard output or standard
/// error, whatever the answers or the failures hold; a failure is described by a status or a code.
/// </summary>
public sealed class ProgramTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<(int Code, string Output, string Error)> Run(string[] args, Func<AnalystSetup, CancellationToken, Task<IAnalystEngine>> engines, Func<string, string?>? environment = null)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Program.RunAsync(args, output, error, environment ?? Frozen.Environment(), engines, Cancel);
        return (code, output.ToString(), error.ToString());
    }

    private static Func<AnalystSetup, CancellationToken, Task<IAnalystEngine>> Engine(params string[] replies) =>
        (_, _) => Task.FromResult<IAnalystEngine>(new ChatClientAnalystEngine(Frozen.Scripted(replies)));

    private static void AssertNoSecret(params string[] texts)
    {
        foreach (var text in texts)
        {
            foreach (var secret in Frozen.Secrets)
            {
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task No_url_or_id_reaches_stdout_on_a_run_whose_answers_leak_them()
    {
        using var frozen = new Frozen();
        var leaky = $"From {Frozen.McpUrl} via {Frozen.ConnectionId}, item {Frozen.Guid}: A$1,234.56.";

        var (code, output, error) = await Run(frozen.RunArgs(), Engine(leaky, leaky));

        Assert.Equal(0, code);
        AssertNoSecret(output, error);
        // Nor does any reach a record.
        AssertNoSecret(File.ReadAllText(Assert.Single(Directory.GetFiles(frozen.Out))));
        Assert.Equal("From <url> via <id>, item <id>: A$1,234.56.", frozen.Records()[0]["answer_text"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_url_or_id_reaches_stderr_when_provisioning_fails()
    {
        using var frozen = new Frozen();
        var failure = new ClientResultException($"Agent at {Frozen.ProjectEndpoint} with tool {Frozen.McpUrl} ({Frozen.ConnectionId}, {Frozen.Guid}) not found.", response: null);

        var (code, output, error) = await Run(frozen.RunArgs(), (_, _) => throw failure);

        Assert.Equal(3, code);
        AssertNoSecret(output, error);
        Assert.Equal("the Foundry agent failed: ClientResultException", error.Trim());
        Assert.Empty(frozen.OutFiles());
    }

    [Fact]
    public async Task No_url_or_id_reaches_stderr_when_a_question_fails()
    {
        using var frozen = new Frozen();
        var failing = new FailingEngine(new HttpRequestException($"No connection to {Frozen.McpUrl}: {Frozen.Guid}", null, System.Net.HttpStatusCode.BadGateway));

        var (code, output, error) = await Run(frozen.RunArgs(), (_, _) => Task.FromResult<IAnalystEngine>(failing));

        Assert.Equal(3, code);
        AssertNoSecret(output, error);
        Assert.Equal("the Foundry agent failed: HTTP 502, HttpRequestException", error.Trim());
    }

    [Theory]
    [InlineData("ANALYST_MCP_URL")]
    [InlineData("ANALYST_CONNECTION_ID")]
    [InlineData("FWA_PROJECT_ENDPOINT")]
    public async Task A_missing_setting_is_named_and_nothing_runs(string variable)
    {
        using var frozen = new Frozen();

        var (code, output, error) = await Run(frozen.RunArgs(), (_, _) => throw new InvalidOperationException("not reached"), Frozen.Environment((variable, " ")));

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Equal($"{variable} is not set", error.Trim());
        Assert.Empty(frozen.OutFiles());
    }

    [Theory]
    [InlineData("ANALYST_MCP_URL", "http://mcp-sentinel.example.invalid/mcp")]
    [InlineData("ANALYST_MCP_URL", "mcp-sentinel.example.invalid/mcp")]
    [InlineData("ANALYST_MCP_URL", "https://mcp-sentinel.example.invalid/m cp")]
    [InlineData("FWA_PROJECT_ENDPOINT", "http://project-sentinel.services.ai.azure.com/api/projects/p")]
    public async Task A_malformed_endpoint_is_named_but_never_repeated(string variable, string value)
    {
        using var frozen = new Frozen();

        var (code, output, error) = await Run(frozen.RunArgs(), (_, _) => throw new InvalidOperationException("not reached"), Frozen.Environment((variable, value)));

        Assert.Equal(2, code);
        Assert.Equal($"{variable} is not an https URL with a host", error.Trim());
        AssertNoSecret(output, error);
    }

    [Fact]
    public async Task Verify_says_the_freeze_is_intact_without_any_setting()
    {
        using var frozen = new Frozen();

        var (code, output, error) = await Run(
            ["verify", "--freeze", frozen.FreezePath, "--questions", frozen.QuestionsPath, "--model", Frozen.Model],
            (_, _) => throw new InvalidOperationException("not reached"),
            _ => null);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Equal($"freeze intact: {frozen.Short}", output.Trim());
    }

    [Fact]
    public async Task Verify_names_the_component_that_drifted()
    {
        using var frozen = new Frozen();

        var (code, _, error) = await Run(
            ["verify", "--freeze", frozen.FreezePath, "--questions", frozen.QuestionsPath, "--model", "gpt-4.1"],
            (_, _) => throw new InvalidOperationException("not reached"));

        Assert.Equal(1, code);
        Assert.Equal("the freeze is broken: model_foundry changed", error.Trim());
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("run", "--freeze", "f.json")]
    [InlineData("run", "--freeze", "f", "--questions", "q", "--out", "o", "--model", "m", "--passes", "4")]
    [InlineData("run", "--freeze", "f", "--questions", "q", "--out", "o", "--model", "m", "--passes", "0")]
    [InlineData("run", "--freeze", "f", "--questions", "q", "--out", "o", "--model", "--passes", "3")]
    [InlineData("run", "--freeze", "f", "--freeze", "g", "--questions", "q", "--out", "o", "--model", "m")]
    [InlineData("verify", "--freeze", "f", "--questions", "q", "--model", "m", "--out", "o")]
    [InlineData("frobnicate")]
    public async Task A_bad_command_line_prints_the_usage(params string[] args)
    {
        var (code, output, error) = await Run(args, (_, _) => throw new InvalidOperationException("not reached"));

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Contains("portfolio-analyst run --freeze <path> --questions <path> --passes <1-3> --out <dir> --model <name>", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_is_named()
    {
        using var frozen = new Frozen();
        var args = frozen.RunArgs();
        args[Array.IndexOf(args, "--questions") + 1] = Path.Combine(frozen.Out, "missing.json");

        var (code, _, error) = await Run(args, (_, _) => throw new InvalidOperationException("not reached"));

        Assert.Equal(2, code);
        Assert.Equal("--questions: file not found", error.Trim());
    }

    private sealed class FailingEngine(Exception failure) : IAnalystEngine
    {
        public Task<AnalystAnswer> AskAsync(string question, CancellationToken ct) => Task.FromException<AnalystAnswer>(failure);
    }
}
