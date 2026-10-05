using System.ClientModel;
using Anthropic.Exceptions;
using Azure;
using Azure.Identity;

namespace Workshop.Agent.Engines;

/// <summary>
/// Tells a failure of the model's service from a failure of the model or the loop (spec §5.4: an
/// auth failure is an error, not a task failure; Review Focus 1). A service failure is any of these,
/// or one of them wrapped as an inner exception:
/// <list type="bullet">
/// <item><see cref="HttpRequestException"/>, or the Anthropic SDK's <see cref="AnthropicIOException"/>: the network, DNS or TLS.</item>
/// <item>Azure.Identity's <see cref="AuthenticationFailedException"/>, which <see cref="CredentialUnavailableException"/> derives from: the credentials.</item>
/// <item>A 401, 403, 404, 408 or 5xx answer: System.ClientModel's <see cref="ClientResultException"/> (the
/// Foundry project's SDK), Azure.Core's <see cref="RequestFailedException"/>, or the Anthropic SDK's
/// <see cref="AnthropicApiException"/>.</item>
/// <item>A call's own time limit: <see cref="TimeoutException"/>, or an <see cref="OperationCanceledException"/>
/// the engine did not ask for. The engine asks only once neither its caller nor its time limit has
/// cancelled, so a cancellation that still surfaces is an SDK's or an HTTP client's timeout.</item>
/// </list>
/// </summary>
internal static class ServiceFailure
{
    // A cycle of inner exceptions cannot be built through the constructors, but a bound costs nothing.
    private const int MaxDepth = 16;

    public static bool Is(Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var depth = 0;
        for (var current = e; current is not null && depth < MaxDepth; current = current.InnerException, depth++)
        {
            if (IsOne(current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an HTTP status is the service's failure rather than the request's: 401, 403, 404 (no
    /// such deployment, model or agent version: the setup's fault, which would otherwise score one
    /// engine 0 on every run), 408 (the service timed the request out), or 500 and above.
    /// </summary>
    public static bool IsServiceStatus(int status) => status is 401 or 403 or 404 or 408 or >= 500;

    private static bool IsOne(Exception e) => e switch
    {
        HttpRequestException or AnthropicIOException or AuthenticationFailedException => true,
        TimeoutException or OperationCanceledException => true,
        ClientResultException c => IsServiceStatus(c.Status),
        RequestFailedException r => IsServiceStatus(r.Status),
        AnthropicApiException a => IsServiceStatus((int)a.StatusCode),
        _ => false,
    };
}
