using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Workshop.Agent.Surface;

/// <summary>
/// The workshop app's endpoint, as a client sees it: <c>GET /screens</c>, <c>GET /screen</c> and
/// <c>POST /actions</c> on <c>http://127.0.0.1:&lt;port&gt;/</c>, each with the launch's token in
/// <c>X-Surface-Token</c>. Any answer but a 2xx throws <see cref="SurfaceHttpException"/>; a 503 or
/// a 500 on an action does not mean the action did not happen (README, "HTTP errors"). It keeps
/// every screen description it receives, for the runner's gate audit.
/// </summary>
public sealed class SurfaceClient : IDisposable
{
    public const string TokenHeader = "X-Surface-Token";

    // Longer than the endpoint's own 10-second UI wait, so its 503 arrives rather than a client timeout.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient http;
    private readonly Lock seenLock = new();
    private readonly List<Screen> seen = [];

    public SurfaceClient(int port, string token)
        // No proxy: a system proxy must never see the token, and loopback needs none. The arguments
        // are checked first, so a bad one throws before a handler exists that nothing would dispose.
        : this(Checked(port, token), token, new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
    {
    }

    /// <summary>For tests: a handler that stands between the client and the app. The client disposes it.</summary>
    internal SurfaceClient(int port, string token, HttpMessageHandler handler)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(handler);

        http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"),
            Timeout = RequestTimeout,
        };
        http.DefaultRequestHeaders.Add(TokenHeader, token);
    }

    private static int Checked(int port, string token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return port;
    }

    /// <summary>
    /// Every screen description this client has received, in order, from <c>GET /screen</c> and from
    /// action replies: which buttons a run saw flagged destructive.
    /// </summary>
    public IReadOnlyList<Screen> SeenScreens
    {
        get
        {
            lock (seenLock)
            {
                return [.. seen];
            }
        }
    }

    /// <summary><c>GET /screens</c>: every screen the app can open.</summary>
    public async Task<ScreenInfo[]> ListScreensAsync(CancellationToken ct = default) =>
        await SendAsync<ScreenInfo[]>(new HttpRequestMessage(HttpMethod.Get, "screens"), ct).ConfigureAwait(false);

    /// <summary><c>GET /screen</c>: the current screen.</summary>
    public async Task<Screen> DescribeAsync(CancellationToken ct = default) =>
        Saw(await SendAsync<Screen>(new HttpRequestMessage(HttpMethod.Get, "screen"), ct).ConfigureAwait(false));

    /// <summary><c>POST /actions</c>: one action, and what it did.</summary>
    public async Task<ActionReply> ActAsync(ActionRequest action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(action, ContractJson.Options));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        var reply = await SendAsync<ActionReply>(new HttpRequestMessage(HttpMethod.Post, "actions") { Content = content }, ct).ConfigureAwait(false);
        Saw(reply.Screen);
        return reply;
    }

    public void Dispose() => http.Dispose();

    private Screen Saw(Screen screen)
    {
        lock (seenLock)
        {
            seen.Add(screen);
        }

        return screen;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await http.SendAsync(request, ct).ConfigureAwait(false))
        {
            var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SurfaceHttpException((int)response.StatusCode, System.Text.Encoding.UTF8.GetString(body));
            }

            return JsonSerializer.Deserialize<T>(body, ContractJson.Options)
                ?? throw new JsonException($"The endpoint answered {request.RequestUri} with null.");
        }
    }
}
