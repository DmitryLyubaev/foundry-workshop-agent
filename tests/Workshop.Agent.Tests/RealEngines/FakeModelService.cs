using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace Workshop.Agent.Tests.RealEngines;

/// <summary>One request the SDK sent, as the fake service saw it.</summary>
internal sealed record SentRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body)
{
    /// <summary>The request's path, without the query.</summary>
    public string Path => Uri.AbsolutePath;

    /// <summary>The request's JSON body.</summary>
    public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
}

/// <summary>
/// The model's service, faked under the SDKs' own HTTP stacks: it keeps every request and answers
/// each with the recorded JSON <see cref="Recorded"/> holds, so the SDKs parse, retry (or not) and
/// fail exactly as they would against the service. No request leaves the process.
/// </summary>
internal sealed class FakeModelService(Func<SentRequest, HttpResponseMessage> answer) : HttpMessageHandler
{
    private readonly Lock gate = new();
    private readonly List<SentRequest> requests = [];

    /// <summary>A service that answers every request with <paramref name="answers"/> in turn, the last one again once they run out.</summary>
    public FakeModelService(params Func<HttpResponseMessage>[] answers)
        : this(Sequence(answers))
    {
    }

    public IReadOnlyList<SentRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    /// <summary>The GPT engine's transport: the project client's pipeline over this service.</summary>
    public PipelineTransport Transport() => new HttpClientPipelineTransport(new HttpClient(this, disposeHandler: false));

    /// <summary>The Claude engine's HTTP client over this service.</summary>
    public HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    public static HttpResponseMessage Json(int status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new SentRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body);
        lock (gate)
        {
            requests.Add(sent);
        }

        return answer(sent);
    }

    private static Func<SentRequest, HttpResponseMessage> Sequence(Func<HttpResponseMessage>[] answers)
    {
        var next = 0;
        return _ => answers[Math.Min(Interlocked.Increment(ref next) - 1, answers.Length - 1)]();
    }
}

/// <summary>A credential that hands out a made-up token, and keeps the scopes it was asked for. No sign-in happens.</summary>
internal sealed class FakeCredential : TokenCredential
{
    private readonly Lock gate = new();
    private readonly List<string> scopes = [];

    public IReadOnlyList<string> Scopes
    {
        get
        {
            lock (gate)
            {
                return [.. scopes];
            }
        }
    }

    public int Calls { get; private set; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            scopes.AddRange(requestContext.Scopes);
            Calls++;
        }

        return new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}

/// <summary>A credential that cannot sign in, as Azure.Identity's credentials fail.</summary>
internal sealed class FailingCredential(bool unavailable) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        throw (unavailable
            ? new CredentialUnavailableException("Please run 'az login' to set up an account.")
            : new AuthenticationFailedException("The token could not be acquired."));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}
