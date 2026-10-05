using Workshop.Agent.Engines.Foundry;

namespace Workshop.Agent.Tests.RealEngines;

/// <summary>Where the engines find Foundry: the FWA_* variables, or the command line over them.</summary>
public sealed class FoundryOptionsTests
{
    private static readonly Dictionary<string, string> Environment = new(StringComparer.Ordinal)
    {
        ["FWA_PROJECT_ENDPOINT"] = "https://example.services.ai.azure.com/api/projects/example",
        ["FWA_RESOURCE_ENDPOINT"] = "https://example.services.ai.azure.com/",
        ["FWA_GPT_DEPLOYMENT"] = "gpt-5.6-luna",
        ["FWA_CLAUDE_DEPLOYMENT"] = "claude-haiku-4-5",
        ["FWA_AGENT_NAME"] = "fwa-test-agent",
    };

    [Fact]
    public void Settings_come_from_the_FWA_variables()
    {
        var options = FoundryOptions.Read(new Dictionary<string, string>(), Lookup(Environment));

        Assert.Equal(new Uri("https://example.services.ai.azure.com/api/projects/example"), options.ProjectEndpoint);
        Assert.Equal(new Uri("https://example.services.ai.azure.com/"), options.ResourceEndpoint);
        Assert.Equal("gpt-5.6-luna", options.GptDeployment);
        Assert.Equal("claude-haiku-4-5", options.ClaudeDeployment);
        Assert.Equal("fwa-test-agent", options.AgentName);
    }

    [Fact]
    public void The_command_line_wins_over_the_environment()
    {
        var given = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--project-endpoint"] = "https://other.services.ai.azure.com/api/projects/other",
            ["--resource-endpoint"] = "https://other.services.ai.azure.com/",
            ["--gpt-deployment"] = "gpt-other",
            ["--claude-deployment"] = "claude-other",
            ["--agent-name"] = "other-agent",
        };

        var options = FoundryOptions.Read(given, Lookup(Environment));

        Assert.Equal(new Uri("https://other.services.ai.azure.com/api/projects/other"), options.ProjectEndpoint);
        Assert.Equal(new Uri("https://other.services.ai.azure.com/"), options.ResourceEndpoint);
        Assert.Equal("gpt-other", options.GptDeployment);
        Assert.Equal("claude-other", options.ClaudeDeployment);
        Assert.Equal("other-agent", options.AgentName);
    }

    [Fact]
    public void The_agent_name_defaults_to_fwa_workshop_agent()
    {
        var environment = new Dictionary<string, string>(Environment, StringComparer.Ordinal);
        environment.Remove("FWA_AGENT_NAME");

        Assert.Equal("fwa-workshop-agent", FoundryOptions.DefaultAgentName);
        Assert.Equal("fwa-workshop-agent", FoundryOptions.Read(new Dictionary<string, string>(), Lookup(environment)).AgentName);
    }

    [Theory]
    [InlineData("FWA_PROJECT_ENDPOINT", "--project-endpoint")]
    [InlineData("FWA_RESOURCE_ENDPOINT", "--resource-endpoint")]
    [InlineData("FWA_GPT_DEPLOYMENT", "--gpt-deployment")]
    [InlineData("FWA_CLAUDE_DEPLOYMENT", "--claude-deployment")]
    public void A_missing_setting_is_named(string variable, string option)
    {
        var environment = new Dictionary<string, string>(Environment, StringComparer.Ordinal);
        environment[variable] = " ";

        var e = Assert.Throws<FoundrySettingsException>(() => FoundryOptions.Read(new Dictionary<string, string>(), Lookup(environment)));

        Assert.Equal($"{variable} is not set: set it, or pass {option}.", e.Message);
    }

    [Theory]
    [InlineData("http://example.services.ai.azure.com/api/projects/example")]
    [InlineData("example.services.ai.azure.com")]
    public void An_endpoint_that_is_not_https_is_refused_without_echoing_it(string value)
    {
        var environment = new Dictionary<string, string>(Environment, StringComparer.Ordinal) { ["FWA_PROJECT_ENDPOINT"] = value };

        var e = Assert.Throws<FoundrySettingsException>(() => FoundryOptions.Read(new Dictionary<string, string>(), Lookup(environment)));

        // Endpoints are identifiers: an error that reaches a log never repeats one.
        Assert.Equal("FWA_PROJECT_ENDPOINT is not an absolute https URL.", e.Message);
        Assert.DoesNotContain("example", e.Message, StringComparison.Ordinal);
    }

    private static Func<string, string?> Lookup(Dictionary<string, string> environment) =>
        name => environment.TryGetValue(name, out var value) ? value : null;
}
