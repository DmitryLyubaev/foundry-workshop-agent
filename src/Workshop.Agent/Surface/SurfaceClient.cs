using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Workshop.Agent.Surface;

/// <summary>
/// The workshop app's endpoint, as a client sees it: <c>GET /screens</c>, <c>GET /screen</c> and
/// <c>POST /actions</c> on <c>http://127.0.0.1:&lt;port&gt;/</c>, each with the launch's token in
/// <c>X-Surface-Token</c>. Any answer but a 2xx throws <see cref="SurfaceHttpException"/>; a 503 or
/// a 500 on an action does not mean the action did not happen (README, "HTTP errors").
/// </summary>
public sealed class SurfaceClient : IDisposable
{
    public const string TokenHeader = "X-Surface-Token";

    // Longer than the endpoint's own 10-second UI wait, so its 503 arrives rather than a client timeout.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient http;

    public SurfaceClient(int port, string token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // No proxy: a system proxy must never see the token, and loopback needs none.
        http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"),
            Timeout = RequestTimeout,
        };
        http.DefaultRequestHeaders.Add(TokenHeader, token);
    }

    /// <summary><c>GET /screens</c>: every screen the app can open.</summary>
    public async Task<ScreenInfo[]> ListScreensAsync(CancellationToken ct = default) =>
        await SendAsync<ScreenInfo[]>(new HttpRequestMessage(HttpMethod.Get, "screens"), ct).ConfigureAwait(false);

    /// <summary><c>GET /screen</c>: the current screen.</summary>
    public async Task<Screen> DescribeAsync(CancellationToken ct = default) =>
        await SendAsync<Screen>(new HttpRequestMessage(HttpMethod.Get, "screen"), ct).ConfigureAwait(false);

    /// <summary><c>POST /actions</c>: one action, and what it did.</summary>
    public async Task<ActionReply> ActAsync(ActionRequest action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(action, ContractJson.Options));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return await SendAsync<ActionReply>(new HttpRequestMessage(HttpMethod.Post, "actions") { Content = content }, ct).ConfigureAwait(false);
    }

    public void Dispose() => http.Dispose();

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
