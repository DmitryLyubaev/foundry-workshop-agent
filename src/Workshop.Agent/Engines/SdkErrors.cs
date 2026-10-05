using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;

namespace Workshop.Agent.Engines;

/// <summary>
/// Maps the model SDKs' errors to what the engine makes of them (Review Focus 1):
/// <list type="table">
/// <item><term>429</term><description><see cref="ThrottledException"/>, with the wait the service asks for (<c>retry-after-ms</c>, else <c>Retry-After</c>).</description></item>
/// <item><term>401, 403, 5xx, the network, a timeout, the credentials</term><description>left as thrown: <see cref="ServiceFailure"/> knows them, and the run is an infrastructure error.</description></item>
/// <item><term>400 from the content filter</term><description><see cref="ContentFilteredException"/>: Azure's <c>content_filter</c> code, or an Anthropic <c>invalid_request_error</c> naming a content policy.</description></item>
/// <item><term>any other 4xx</term><description><see cref="ModelRequestException"/>, carrying the body's error code: the model's own failure, <c>engine_error</c>.</description></item>
/// </list>
/// The Anthropic SDK drops a 429's headers from its exception, so its client reads the wait in
/// <see cref="RetryAfterHandler"/>, under the SDK, before the SDK sees the answer.
/// </summary>
internal static partial class SdkErrors
{
    /// <summary>The exception the engine is to see in place of <paramref name="e"/>; <paramref name="e"/> itself when it needs no mapping.</summary>
    public static Exception Translate(Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e)
        {
            case ThrottledException:
                return e;
            case ClientResultException { Status: 429 } throttled:
                return new ThrottledException(RetryAfter(name => Header(throttled, name)) ?? TimeSpan.Zero);
            case AnthropicRateLimitException:
                // Only when the handler that reads the wait was not in the way: the throttling wait's minimum applies.
                return new ThrottledException(TimeSpan.Zero);
        }

        if (ServiceFailure.Is(e))
        {
            return e;
        }

        return e switch
        {
            ClientResultException { Status: >= 400 and < 500 } c => Refused(c.Status, AzureError(Body(c)), e),
            AnthropicApiException { StatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError } a => Refused((int)a.StatusCode, AnthropicError(a.ResponseBody), e),
            _ => e,
        };
    }

    /// <summary>
    /// The wait a throttled service asks for: <c>retry-after-ms</c> (or Azure's <c>x-ms-retry-after-ms</c>)
    /// in milliseconds, else <c>Retry-After</c> in seconds or as an HTTP date; null when it names none.
    /// </summary>
    public static TimeSpan? RetryAfter(Func<string, string?> header)
    {
        ArgumentNullException.ThrowIfNull(header);
        foreach (var name in new[] { "retry-after-ms", "x-ms-retry-after-ms" })
        {
            if (double.TryParse(header(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms >= 0)
            {
                return TimeSpan.FromMilliseconds(ms);
            }
        }

        var retryAfter = header("Retry-After");
        if (double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (!DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return null;
        }

        var wait = at - DateTimeOffset.UtcNow;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    private static string? Header(ClientResultException e, string name) =>
        e.GetRawResponse() is { } response && response.Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>The answer's body, when the SDK kept it.</summary>
    private static string? Body(ClientResultException e)
    {
        try
        {
            return e.GetRawResponse()?.Content?.ToString();
        }
        catch (InvalidOperationException)
        {
            // The answer was streamed and not buffered: there is no body to read.
            return null;
        }
    }

    private static Exception Refused(int status, (string? Code, string? Message, bool Filtered) error, Exception e) =>
        status == 400 && error.Filtered
            ? new ContentFilteredException(error.Code, error.Message ?? "The content filter refused the request.")
            : new ModelRequestException(status, error.Code, error.Message, e);

    /// <summary>
    /// An Azure error body, <c>{"error":{"code","message","innererror":{"code"}}}</c>: the content
    /// filter's is code <c>content_filter</c>, its inner code <c>ResponsibleAIPolicyViolation</c>.
    /// </summary>
    private static (string? Code, string? Message, bool Filtered) AzureError(string? body)
    {
        if (ErrorObject(body) is not { } error)
        {
            return (null, null, false);
        }

        var code = Text(error, "code") ?? Text(error, "type");
        var inner = error.TryGetProperty("innererror", out var innerError) && innerError.ValueKind == JsonValueKind.Object ? Text(innerError, "code") : null;
        var filtered = code == "content_filter" || inner == "ResponsibleAIPolicyViolation";
        return (code, Text(error, "message"), filtered);
    }

    /// <summary>
    /// An Anthropic error body, <c>{"type":"error","error":{"type","message"}}</c>: a content policy's
    /// refusal is an <c>invalid_request_error</c> whose message names it. On Foundry the answer may
    /// be Azure's own content filter error, which counts too.
    /// </summary>
    private static (string? Code, string? Message, bool Filtered) AnthropicError(string? body)
    {
        var azure = AzureError(body);
        if (azure.Filtered)
        {
            return azure;
        }

        if (ErrorObject(body) is not { } error)
        {
            return (null, null, false);
        }

        var type = Text(error, "type");
        var message = Text(error, "message");
        var filtered = type == "invalid_request_error" && message is not null && ContentPolicy().IsMatch(message);
        return (type, message, filtered);
    }

    private static JsonElement? ErrorObject(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                ? error.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"content[ _-]?(filter|policy|management)|usage polic", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContentPolicy();
}

/// <summary>
/// Sits on the model SDK's <see cref="IChatClient"/> and throws what <see cref="SdkErrors"/> maps
/// its errors to, so the throttling retry and the engine above see one vocabulary for both SDKs.
/// </summary>
internal sealed class SdkErrorChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (SdkErrors.Translate(e) is var mapped && !ReferenceEquals(mapped, e))
        {
            throw mapped;
        }
    }

    /// <summary>The engine never streams; a stream here would go unmapped, so it is refused.</summary>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The engine calls the model without streaming.");
}

/// <summary>
/// Under the Anthropic SDK, turns a 429 into <see cref="ThrottledException"/> with the wait the
/// service asks for, which the SDK's own exception would not carry.
/// </summary>
internal sealed class RetryAfterHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return response;
        }

        var wait = SdkErrors.RetryAfter(name => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null);
        response.Dispose();
        throw new ThrottledException(wait ?? TimeSpan.Zero);
    }
}

/// <summary>The content filter refused the request (HTTP 400): the run's outcome is <see cref="EngineOutcome.ContentFiltered"/>.</summary>
public sealed class ContentFilteredException(string? code, string message) : Exception(message)
{
    /// <summary>The error code or type the service gave, such as <c>content_filter</c>.</summary>
    public string? Code { get; } = code;
}

/// <summary>
/// The model's service refused the request with a 4xx other than 401, 403 or 429: the model's own
/// failure (<see cref="EngineOutcome.EngineError"/>), with the body's error code in the message.
/// </summary>
public sealed class ModelRequestException(int status, string? code, string? message, Exception inner)
    : Exception(string.Create(CultureInfo.InvariantCulture, $"HTTP {status} ({code ?? "no error code"}): {message ?? "the service gave no message."}"), inner)
{
    public int Status { get; } = status;

    /// <summary>The body's error code (Azure) or error type (Anthropic); null when it had none.</summary>
    public string? Code { get; } = code;
}
