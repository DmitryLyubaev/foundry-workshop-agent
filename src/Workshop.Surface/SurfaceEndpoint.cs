using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Workshop.Surface;

/// <summary>
/// The agent surface's HTTP endpoint, the only way into the app from outside it. It serves loopback
/// clients only, and a request is served only if its Host is this endpoint, it carries this
/// launch's token, and its route, method and body are exactly what the route takes. Everything it
/// does to the UI runs on the UI thread, one request at a time.
/// </summary>
/// <remarks>
/// <para>
/// Routes: <c>GET /screens</c>, <c>GET /screen</c> and <c>POST /actions</c>. The token is written
/// to the user-only <see cref="SessionFile"/> once the port is bound, and every action that reaches
/// the executor is written to the <see cref="AuditLog"/>.
/// </para>
/// <para>
/// HTTP.sys shares one URL namespace between processes and gives each request to the longest
/// matching registration. So that no other process can take a route, and with it the token, the
/// endpoint registers the root and every route's sub-path, <c>/screens/</c>, <c>/screen/</c> and
/// <c>/actions/</c>, under both <c>127.0.0.1:port</c> and <c>localhost:port</c>: any other
/// registration that would match a route then conflicts and fails. A registration deeper than a
/// route (such as <c>/actions/x/</c>) still succeeds, but receives only paths the endpoint answers
/// 404. HTTP.sys accepts a localhost registration on every local address, so each request's
/// remote address is also checked to be loopback.
/// </para>
/// </remarks>
public sealed class SurfaceEndpoint : IDisposable
{
    public const int DefaultPort = 47811;

    /// <summary>The largest <c>POST /actions</c> body accepted, in bytes.</summary>
    public const int MaxBodyBytes = 65_536;

    /// <summary>How long a request waits for the UI thread, in all, before it is answered 503.</summary>
    public static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(10);

    private const string TokenHeader = "X-Surface-Token";

    /// <summary>The audited outcome of an action whose execution threw, which is none of <see cref="Outcomes"/>.</summary>
    private const string FailedOutcome = "error";

    private static readonly JsonDocumentOptions ActionJson = new() { AllowDuplicateProperties = false, MaxDepth = 4 };

    private readonly int port;
    private readonly string sessionDirectory;
    private readonly Control uiAnchor;
    private readonly IScreenNavigator navigator;
    private readonly ActionExecutor executor;
    private readonly AuditLog audit;
    private readonly string[] allowedHosts;

    // One UI request at a time. It is released when the UI work leaves the UI queue, having run or
    // been skipped, never when its request gives up waiting: so an action still running after its
    // 503 is never overlapped by the next one. It is never disposed, because a timed-out action can
    // still release it after the endpoint is.
    private readonly SemaphoreSlim uiGate = new(1, 1);

    private readonly Lock lifecycle = new();
    private HttpListener? listener;
    private string? token;
    private bool disposed;

    /// <param name="address">Must be <see cref="IPAddress.Loopback"/>; anything else is refused.</param>
    /// <param name="uiAnchor">A control on the UI thread whose handle exists; requests are invoked onto it.</param>
    /// <exception cref="ArgumentException">The address is not 127.0.0.1, or the anchor has no handle.</exception>
    public SurfaceEndpoint(IPAddress address, int port, string sessionDirectory, Control uiAnchor, IScreenNavigator navigator, AuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.Equals(IPAddress.Loopback))
        {
            throw new ArgumentException($"The agent surface listens on 127.0.0.1 only, not on {address}.", nameof(address));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentNullException.ThrowIfNull(uiAnchor);
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(audit);

        if (!uiAnchor.IsHandleCreated)
        {
            throw new ArgumentException("The UI anchor's handle must exist, so requests can be invoked onto its thread.", nameof(uiAnchor));
        }

        this.port = port;
        this.sessionDirectory = sessionDirectory;
        this.uiAnchor = uiAnchor;
        this.navigator = navigator;
        this.audit = audit;
        executor = new ActionExecutor(navigator);
        allowedHosts = [$"127.0.0.1:{port}", $"localhost:{port}"];
    }

    /// <summary>
    /// Binds the port, then writes a session file with a fresh token. If the port cannot be bound,
    /// nothing is written, so another instance's session file is left as it is.
    /// </summary>
    /// <exception cref="InvalidOperationException">The port cannot be bound, or the endpoint is already started.</exception>
    public void Start()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (listener is not null)
            {
                throw new InvalidOperationException("The agent surface is already started.");
            }

            var bound = new HttpListener { IgnoreWriteExceptions = true };
            foreach (var prefix in PrefixesFor(port))
            {
                bound.Prefixes.Add(prefix);
            }
            try
            {
                bound.Start();
            }
            catch (HttpListenerException e)
            {
                bound.Close();
                throw new InvalidOperationException(
                    $"The agent surface could not listen on 127.0.0.1:{port}. Is another instance using port {port}? ({e.Message})", e);
            }

            var fresh = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            try
            {
                SessionFile.Write(sessionDirectory, port, fresh);
            }
            catch
            {
                bound.Close();
                throw;
            }

            listener = bound;
            token = fresh;
            var expected = Encoding.UTF8.GetBytes(fresh);
            _ = Task.Run(() => AcceptAsync(bound, expected));
        }
    }

    /// <summary>
    /// Stops listening, and deletes the session file if it still holds this start's token: a file
    /// another instance has written since is left alone. Stopping a stopped endpoint does nothing.
    /// </summary>
    public void Stop()
    {
        lock (lifecycle)
        {
            if (listener is null)
            {
                return;
            }

            listener.Close();
            SessionFile.DeleteIfOwned(sessionDirectory, token!);
            listener = null;
            token = null;
        }
    }

    public void Dispose()
    {
        lock (lifecycle)
        {
            Stop();
            disposed = true;
        }
    }

    /// <summary>
    /// Accepts requests until the listener is closed. A failure to accept while still listening is
    /// traced and retried after a back-off, so the loop neither dies nor spins, and it serves again
    /// as soon as the failure clears.
    /// </summary>
    private async Task AcceptAsync(HttpListener bound, byte[] expectedToken)
    {
        var failures = 0;
        while (bound.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await bound.GetContextAsync().ConfigureAwait(false);
                failures = 0;
            }
            catch (Exception e)
            {
                if (!bound.IsListening)
                {
                    return;
                }

                failures++;
                var pause = AcceptBackOff(failures);
                Trace.TraceWarning(
                    $"The agent surface failed to accept a request ({failures} in a row); retrying in {pause.TotalMilliseconds} ms. {e.GetType().Name}: {e.Message}");
                await Task.Delay(pause).ConfigureAwait(false);
                continue;
            }

            // Each request is handled on its own, so one waiting for the UI never holds up the listener.
            _ = Task.Run(() => HandleAsync(context, expectedToken));
        }
    }

    /// <summary>
    /// The root and each route's sub-path, under both host names. Holding them all is what stops
    /// another process registering a prefix that HTTP.sys would prefer for one of the routes.
    /// </summary>
    internal static IReadOnlyList<string> PrefixesFor(int port) =>
        [
            .. from host in new[] { "127.0.0.1", "localhost" }
               from path in new[] { "/", "/screens/", "/screen/", "/actions/" }
               select $"http://{host}:{port}{path}",
        ];

    /// <summary>The pause after the given number of failures in a row: 50 ms, doubling, at most 2 seconds.</summary>
    internal static TimeSpan AcceptBackOff(int failures) =>
        TimeSpan.FromMilliseconds(Math.Min(2_000, 50 * Math.Pow(2, Math.Clamp(failures - 1, 0, 10))));

    private async Task HandleAsync(HttpListenerContext context, byte[] expectedToken)
    {
        var response = context.Response;
        try
        {
            var reply = await RespondAsync(context.Request, expectedToken).ConfigureAwait(false);

            response.StatusCode = reply.Status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = reply.Body.Length;
            response.AddHeader("Cache-Control", "no-store");
            response.AddHeader("X-Content-Type-Options", "nosniff");

            // An empty Server header stops HTTP.sys adding its own banner.
            response.AddHeader("Server", "");
            if (reply.Allow is not null)
            {
                response.AddHeader("Allow", reply.Allow);
            }

            if (context.Request.HttpMethod != "HEAD")
            {
                await response.OutputStream.WriteAsync(reply.Body).ConfigureAwait(false);
            }

            response.Close();
        }
        catch (Exception)
        {
            // The client went away or the endpoint stopped while answering: there is no one to tell.
            try
            {
                response.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>The checks, in order: a loopback client, Host, token, route and method, body, then the UI.</summary>
    private async Task<Reply> RespondAsync(HttpListenerRequest request, byte[] expectedToken)
    {
        if (request.RemoteEndPoint is not { } client || !IPAddress.IsLoopback(client.Address))
        {
            return Error(HttpStatusCode.Forbidden, "not_loopback");
        }

        if (!IsAllowedHost(request.Headers["Host"]))
        {
            return Error(HttpStatusCode.Forbidden, "bad_host");
        }

        if (!HasToken(request, expectedToken))
        {
            return Error(HttpStatusCode.Unauthorized, "unauthorized");
        }

        var route = request.Url?.AbsolutePath;
        var method = route switch
        {
            "/screens" or "/screen" => "GET",
            "/actions" => "POST",
            _ => null,
        };

        if (method is null)
        {
            return Error(HttpStatusCode.NotFound, "not_found");
        }

        if (request.HttpMethod != method)
        {
            return Error(HttpStatusCode.MethodNotAllowed, "method_not_allowed") with { Allow = method };
        }

        try
        {
            return route switch
            {
                "/screens" => await OnUiAsync(ListScreens).ConfigureAwait(false),
                "/screen" => await OnUiAsync(() => ScreenDescriber.Describe(navigator.Current)).ConfigureAwait(false),
                _ => await PostActionAsync(request).ConfigureAwait(false),
            };
        }
        catch (Exception)
        {
            // Never echoes the exception: its text is the app's business, not the caller's.
            return Error(HttpStatusCode.InternalServerError, "internal_error");
        }
    }

    private bool IsAllowedHost(string? host) =>
        host is not null && allowedHosts.Any(allowed => string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase));

    private static bool HasToken(HttpListenerRequest request, byte[] expectedToken) =>
        request.Headers.GetValues(TokenHeader) is [var sent]
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), expectedToken);

    private async Task<Reply> PostActionAsync(HttpListenerRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return Error(HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        }

        // Refused by its declared length before reading; a chunked body is refused once it passes the limit.
        if (request.ContentLength64 > MaxBodyBytes || await ReadBodyAsync(request.InputStream).ConfigureAwait(false) is not { } body)
        {
            return Error(HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        }

        if (!TryParseAction(body, out var action))
        {
            return Error(HttpStatusCode.BadRequest, "bad_request");
        }

        return await OnUiAsync(() => ExecuteAndAudit(action)).ConfigureAwait(false);
    }

    /// <summary>The body, or null if it is longer than <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream input)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await input.ReadAsync(buffer.AsMemory(length)).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        return length > MaxBodyBytes ? null : buffer[..length];
    }

    /// <summary>
    /// Reads one action. The body must be a JSON object whose <c>type</c> is one of
    /// <see cref="ActionTypes"/>; the targets are strings, and the value may also be a number or a
    /// boolean, taken as its JSON text. Other properties are ignored, and a repeated one is refused.
    /// </summary>
    private static bool TryParseAction(byte[] body, [NotNullWhen(true)] out SurfaceAction? action)
    {
        action = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, ActionJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is not ("type" or "screen" or "field" or "value" or "list" or "row" or "button"))
                {
                    continue;
                }

                var isValue = property.Name == "value";
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        texts[property.Name] = property.Value.GetString();
                        break;
                    case JsonValueKind.Null:
                        texts[property.Name] = null;
                        break;
                    case JsonValueKind.Number when isValue:
                        // JSON's number text is already culture-invariant: 2 stays "2", 2.50 stays "2.50".
                        texts[property.Name] = property.Value.GetRawText();
                        break;
                    case JsonValueKind.True when isValue:
                        texts[property.Name] = "true";
                        break;
                    case JsonValueKind.False when isValue:
                        texts[property.Name] = "false";
                        break;
                    default:
                        return false;
                }
            }

            var type = texts.GetValueOrDefault("type");
            if (type is not (ActionTypes.Open or ActionTypes.Set or ActionTypes.Select or ActionTypes.Press))
            {
                return false;
            }

            action = new SurfaceAction(
                type,
                texts.GetValueOrDefault("screen"),
                texts.GetValueOrDefault("field"),
                texts.GetValueOrDefault("value"),
                texts.GetValueOrDefault("list"),
                texts.GetValueOrDefault("row"),
                texts.GetValueOrDefault("button"));
            return true;
        }
    }

    private IReadOnlyList<ScreenEntry> ListScreens() => [.. navigator.Screens.Select(s => new ScreenEntry(s.Id, s.Title))];

    /// <summary>
    /// Runs on the UI thread. The action is audited whatever happens once it reaches the executor.
    /// A log that cannot be written is traced, and the action's result still answered: the action
    /// has already happened, and a 500 would invite the client to repeat it.
    /// </summary>
    private ActionResult ExecuteAndAudit(SurfaceAction action)
    {
        var outcome = FailedOutcome;
        try
        {
            var result = executor.Execute(action);
            outcome = result.Outcome;
            return result;
        }
        finally
        {
            try
            {
                audit.Append(action, outcome);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"The agent surface could not write the audit log {audit.Path}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the UI thread, after any earlier UI request has finished, and
    /// answers its result as JSON; or answers 503 if the whole wait passes <see cref="UiTimeout"/>.
    /// Work that has not started by then never runs. Work already running on the UI thread is left
    /// to finish, and holds the gate until it does.
    /// </summary>
    private async Task<Reply> OnUiAsync<T>(Func<T> work)
    {
        var waited = Stopwatch.StartNew();

        // Timed out waiting for the gate: the work was never posted, so it never runs.
        if (!await uiGate.WaitAsync(UiTimeout).ConfigureAwait(false))
        {
            return Error(HttpStatusCode.ServiceUnavailable, "ui_timeout");
        }

        var item = new UiWork<T>(work);
        try
        {
            // BeginInvoke, Invoke's asynchronous form, so this thread can stop waiting at the timeout.
            uiAnchor.BeginInvoke((MethodInvoker)item.Run);
        }
        catch
        {
            uiGate.Release();
            throw;
        }

        _ = item.Done.ContinueWith(
            finished =>
            {
                // Observed here, because a request that timed out no longer awaits it.
                _ = finished.Exception;
                uiGate.Release();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var left = UiTimeout - waited.Elapsed;
        using (var timer = new CancellationTokenSource())
        {
            await Task.WhenAny(item.Done, Task.Delay(left > TimeSpan.Zero ? left : TimeSpan.Zero, timer.Token)).ConfigureAwait(false);
            await timer.CancelAsync().ConfigureAwait(false);
        }

        // Timed out. Abandoning fails only if the work has started; if it has also just finished,
        // its result is served rather than a 503.
        if (!item.Done.IsCompleted && (item.TryAbandon() || !item.Done.IsCompleted))
        {
            return Error(HttpStatusCode.ServiceUnavailable, "ui_timeout");
        }

        var value = await item.Done.ConfigureAwait(false);
        return new Reply((int)HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(value, SurfaceJson.Options));
    }

    private static Reply Error(HttpStatusCode status, string error) =>
        new((int)status, Encoding.UTF8.GetBytes($$"""{"error":"{{error}}"}"""));

    /// <summary>A status and its JSON body; <see cref="Allow"/> is the route's one method, sent with a 405.</summary>
    private readonly record struct Reply(int Status, byte[] Body, string? Allow = null);

    private sealed record ScreenEntry(string Id, string Title);

    /// <summary>
    /// One piece of work posted to the UI thread. It runs only if its request has not given up on
    /// it first; <see cref="Done"/> completes either way, with the result or cancelled.
    /// </summary>
    private sealed class UiWork<T>(Func<T> work)
    {
        private const int Pending = 0;
        private const int Running = 1;
        private const int Abandoned = 2;

        private readonly TaskCompletionSource<T> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int state = Pending;

        public Task<T> Done => done.Task;

        /// <summary>On the UI thread: does the work, unless it was abandoned while it waited in the queue.</summary>
        public void Run()
        {
            if (Interlocked.CompareExchange(ref state, Running, Pending) != Pending)
            {
                done.SetCanceled();
                return;
            }

            try
            {
                done.SetResult(work());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        }

        /// <summary>True if the work had not started, and now never will.</summary>
        public bool TryAbandon() => Interlocked.CompareExchange(ref state, Abandoned, Pending) == Pending;
    }
}
