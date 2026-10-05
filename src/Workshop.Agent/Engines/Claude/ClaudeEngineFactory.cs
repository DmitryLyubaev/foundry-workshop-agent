using Anthropic.Foundry;
using Azure.Core;
using Microsoft.Extensions.AI;
using Workshop.Agent.Engines.Foundry;
using Workshop.Agent.Runner;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Engines.Claude;

/// <summary>
/// The <c>claude</c> engine: Claude deployed in Foundry, through the Anthropic SDK's
/// <see cref="AnthropicFoundryClient"/> and its <see cref="IChatClient"/>. A <see cref="ChatClientEngine"/>,
/// so the instructions, the tools and the settings go with each request, as for every engine.
/// Keyless: an Entra token for <c>https://ai.azure.com/.default</c>. The SDK's own retries are off,
/// so the engine's throttling budget is the only retry.
/// </summary>
public static class ClaudeEngineFactory
{
    public const string Name = "claude";

    /// <summary>The Entra scope Foundry's Claude endpoint takes tokens for.</summary>
    public const string Scope = "https://ai.azure.com/.default";

    // One connection pool for every run; the SDK applies its own time limit per call, so the client's is off.
    private static readonly HttpClient Shared = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static ChatClientEngine Create(FoundryOptions options, EngineRun run, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Create(options, run.Budget, run.TimeLimit, run, credential, Shared);
    }

    /// <summary>For tests: the run's parts, and the HTTP client the SDK sends through.</summary>
    internal static ChatClientEngine Create(FoundryOptions options, ToolBudget budget, TimeSpan timeLimit, IToolCallRecorder? recorder, TokenCredential credential, HttpClient http) =>
        new(Name, options.ClaudeDeployment, CreateModel(options, credential, http), budget, timeLimit, recorder)
        {
            Deployment = options.ClaudeDeployment,
        };

    /// <summary>The model's client under the engine's loop: the SDK's chat client on the Claude deployment, its errors mapped.</summary>
    internal static IChatClient CreateModel(FoundryOptions options, TokenCredential credential, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(http);

        // The SDK asks for a token on every request, synchronously: the cache asks Entra once an hour, not once a call.
        var credentials = new AnthropicFoundryIdentityTokenCredentials(new CachingTokenCredential(credential), options.ResourceName, [Scope]);
        var client = new AnthropicFoundryClient(credentials)
        {
            HttpClient = http,
            MaxRetries = 0,
            Timeout = FoundryOptions.CallTimeout,
            // Reads a 429's wait before the SDK drops its headers.
            Handlers = [new RetryAfterHandler()],
        };
        return new SdkErrorChatClient(client.AsIChatClient(options.ClaudeDeployment));
    }
}

/// <summary>Keeps a token until five minutes before it expires, so a credential that signs in each time (the Azure CLI's) is asked rarely.</summary>
internal sealed class CachingTokenCredential(TokenCredential inner) : TokenCredential
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly Lock gate = new();
    private AccessToken? cached;
    private string? cachedFor;

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (Fresh(requestContext) is { } token)
            {
                return token;
            }
        }

        // Fetched outside the lock: a slow sign-in holds no one else up for longer than it must.
        return Keep(requestContext, inner.GetToken(requestContext, cancellationToken));
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (Fresh(requestContext) is { } token)
            {
                return token;
            }
        }

        return Keep(requestContext, await inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false));
    }

    private static string Key(TokenRequestContext context) => string.Join(' ', context.Scopes);

    private AccessToken? Fresh(TokenRequestContext context) =>
        cached is { } token && cachedFor == Key(context) && token.ExpiresOn - Margin > DateTimeOffset.UtcNow ? token : null;

    private AccessToken Keep(TokenRequestContext context, AccessToken token)
    {
        lock (gate)
        {
            cached = token;
            cachedFor = Key(context);
        }

        return token;
    }
}
