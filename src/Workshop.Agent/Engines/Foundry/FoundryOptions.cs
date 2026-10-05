namespace Workshop.Agent.Engines.Foundry;

/// <summary>
/// Where the real engines find Foundry. Each setting comes from its command-line option, or else
/// its environment variable; the values are the Foundry stack's outputs (infra/foundry), never
/// written into a committed file. Only the agent's name has a default.
/// </summary>
/// <param name="ProjectEndpoint">The Foundry project's endpoint, <c>https://&lt;resource&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c>: the GPT prompt agent and its Responses API.</param>
/// <param name="ResourceEndpoint">The Foundry resource's endpoint, <c>https://&lt;resource&gt;.services.ai.azure.com/</c>: Claude's Messages API is under it.</param>
/// <param name="GptDeployment">The GPT model's deployment, named after its model.</param>
/// <param name="ClaudeDeployment">The Claude model's deployment, named after its model.</param>
/// <param name="AgentName">The GPT prompt agent's name.</param>
public sealed record FoundryOptions(Uri ProjectEndpoint, Uri ResourceEndpoint, string GptDeployment, string ClaudeDeployment, string AgentName)
{
    /// <summary>The prompt agent's name when none is given.</summary>
    public const string DefaultAgentName = "fwa-workshop-agent";

    /// <summary>
    /// How long one model call may take before it fails as a service failure (a timeout), the same
    /// for both SDKs: well inside the run's 5 minutes, and well past a 4,096-token answer.
    /// </summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The command-line options the settings are read from, each with its environment variable.</summary>
    public static IReadOnlyList<(string Option, string Variable)> Settings { get; } =
    [
        ("--project-endpoint", "FWA_PROJECT_ENDPOINT"),
        ("--resource-endpoint", "FWA_RESOURCE_ENDPOINT"),
        ("--gpt-deployment", "FWA_GPT_DEPLOYMENT"),
        ("--claude-deployment", "FWA_CLAUDE_DEPLOYMENT"),
        ("--agent-name", "FWA_AGENT_NAME"),
    ];

    /// <summary>
    /// Reads the settings: each from <paramref name="options"/> (the command line), else from
    /// <paramref name="environment"/>. Throws <see cref="FoundrySettingsException"/> naming the first
    /// one missing or malformed; the message never repeats a value, as endpoints are identifiers.
    /// </summary>
    public static FoundryOptions Read(IReadOnlyDictionary<string, string> options, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        string? Value(int setting)
        {
            var (option, variable) = Settings[setting];
            var value = options.TryGetValue(option, out var given) ? given : environment(variable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        string Required(int setting) =>
            Value(setting) ?? throw new FoundrySettingsException($"{Settings[setting].Variable} is not set: set it, or pass {Settings[setting].Option}.");

        Uri Endpoint(int setting) =>
            Uri.TryCreate(Required(setting), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? uri
                : throw new FoundrySettingsException($"{Settings[setting].Variable} is not an absolute https URL.");

        return new FoundryOptions(Endpoint(0), Endpoint(1), Required(2), Required(3), Value(4) ?? DefaultAgentName);
    }

    /// <summary>The Foundry resource's name: the first label of <see cref="ResourceEndpoint"/>'s host, its custom subdomain.</summary>
    public string ResourceName => ResourceEndpoint.Host.Split('.')[0];

    /// <summary>The settings without the endpoints, which are identifiers and stay out of any log.</summary>
    public override string ToString() =>
        $"FoundryOptions {{ GptDeployment = {GptDeployment}, ClaudeDeployment = {ClaudeDeployment}, AgentName = {AgentName} }}";
}

/// <summary>A Foundry setting is missing or malformed; the message names it, and never its value.</summary>
public sealed class FoundrySettingsException(string message) : Exception(message);
