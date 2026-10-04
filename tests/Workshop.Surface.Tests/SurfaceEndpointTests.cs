using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Workshop.Surface.Tests;

public sealed class SurfaceEndpointTests
{
    private const string BumpJson = """{"type":"press","button":"bump"}""";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Start-up and binding

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.2")]
    [InlineData("192.168.1.10")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")]
    public void Refuses_a_non_loopback_address(string address)
    {
        using var anchor = new Control();
        var audit = new AuditLog(Path.Combine(Path.GetTempPath(), "never-written.jsonl"));

        var refused = Assert.Throws<ArgumentException>(
            () => new SurfaceEndpoint(IPAddress.Parse(address), 47811, Path.GetTempPath(), anchor, new NoScreens(), audit));

        Assert.Contains("127.0.0.1", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_instance_fails_and_leaves_the_session_file()
    {
        using var bench = Bench.Started();
        var before = File.ReadAllText(bench.SessionPath);

        var second = bench.NewEndpoint();
        var refused = Assert.Throws<InvalidOperationException>(second.Start);
        second.Dispose();

        Assert.Contains(bench.Port.ToString(CultureInfo.InvariantCulture), refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(bench.SessionPath));
        Assert.Equal(HttpStatusCode.OK, (await bench.SendAsync(HttpMethod.Get, "/screen")).StatusCode);
    }

    [Fact]
    public async Task Each_start_writes_a_new_token()
    {
        using var bench = Bench.Created();

        bench.Endpoint.Start();
        var first = SessionFile.Read(bench.Directory).Token;
        bench.Endpoint.Stop();

        bench.Endpoint.Start();
        var second = SessionFile.Read(bench.Directory).Token;

        Assert.NotEqual(first, second);
        foreach (var token in new[] { first, second })
        {
            Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
            Assert.Equal(32, Base64Url.DecodeFromChars(token).Length);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await bench.SendAsync(HttpMethod.Get, "/screen", token: first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bench.SendAsync(HttpMethod.Get, "/screen", token: second)).StatusCode);
    }

    [Fact]
    public async Task Stale_session_file_is_replaced()
    {
        using var bench = Bench.Created();
        Directory.CreateDirectory(bench.Directory);
        File.WriteAllText(bench.SessionPath, """{"port":1,"token":"stale","pid":999999,"startedAt":"2026-01-01T00:00:00+00:00"}""");

        bench.Endpoint.Start();

        var session = SessionFile.Read(bench.Directory);
        Assert.Equal(bench.Port, session.Port);
        Assert.NotEqual("stale", session.Token);
        Assert.Equal(Environment.ProcessId, session.Pid);
        SessionFileTests.AssertOnlyTheCurrentUser(bench.SessionPath);
        Assert.Equal(HttpStatusCode.OK, (await bench.SendAsync(HttpMethod.Get, "/screen")).StatusCode);
    }

    // Requests that are refused

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("wrong")]
    [InlineData("one character off")]
    [InlineData("one character more")]
    [InlineData("other case")]
    public async Task Missing_or_wrong_token_is_401(string how)
    {
        using var bench = Bench.Started();
        var token = bench.Token;
        var sent = how switch
        {
            "missing" => null,
            "empty" => "",
            "wrong" => "wrong",
            "one character off" => token[..^1] + (token[^1] == 'A' ? 'B' : 'A'),
            "one character more" => token + "A",
            "other case" => token.ToUpperInvariant() == token ? token.ToLowerInvariant() : token.ToUpperInvariant(),
            _ => throw new ArgumentOutOfRangeException(nameof(how)),
        };

        await AssertError(await bench.SendAsync(HttpMethod.Get, "/screen", token: sent), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertError(await bench.SendAsync(HttpMethod.Get, "/screens", token: sent), HttpStatusCode.Unauthorized, "unauthorized");
        await AssertError(await bench.PostAsync(BumpJson, token: sent), HttpStatusCode.Unauthorized, "unauthorized");

        Assert.Equal(0, bench.Bumps);
        Assert.False(File.Exists(bench.AuditPath));
    }

    [Theory]
    [InlineData("evil.example:47811")]
    [InlineData("evil.example:{port}")]
    [InlineData("127.0.0.1:1")]
    [InlineData("localhost:1")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1.evil.example:{port}")]
    public async Task Foreign_host_header_is_403(string host)
    {
        using var bench = Bench.Started();
        host = host.Replace("{port}", bench.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        await AssertError(await bench.SendAsync(HttpMethod.Get, "/screen", host: host), HttpStatusCode.Forbidden, "bad_host");
        await AssertError(await bench.PostAsync(BumpJson, host: host), HttpStatusCode.Forbidden, "bad_host");

        // The Host check comes before the token check.
        await AssertError(await bench.SendAsync(HttpMethod.Get, "/screen", host: host, token: null), HttpStatusCode.Forbidden, "bad_host");

        Assert.Equal(0, bench.Bumps);
    }

    [Fact]
    public async Task Options_is_405_without_cors_headers()
    {
        using var bench = Bench.Started();

        foreach (var route in new[] { "/screens", "/screen", "/actions" })
        {
            var preflight = bench.Request(HttpMethod.Options, route);
            preflight.Headers.Add("Origin", "http://evil.example");
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", "x-surface-token, content-type");

            var response = await bench.SendAsync(preflight);

            await AssertError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
            Assert.Equal([route == "/actions" ? "POST" : "GET"], response.Content.Headers.Allow);
            AssertNoCorsHeaders(response);
        }

        (HttpMethod Method, string Route)[] wrongMethods =
        [
            (HttpMethod.Post, "/screens"),
            (HttpMethod.Put, "/screen"),
            (HttpMethod.Delete, "/screen"),
            (HttpMethod.Get, "/actions"),
            (HttpMethod.Patch, "/actions"),
        ];

        foreach (var (method, route) in wrongMethods)
        {
            var response = await bench.SendAsync(method, route);
            await AssertError(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
            AssertNoCorsHeaders(response);
        }

        var head = await bench.SendAsync(HttpMethod.Head, "/screen");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, head.StatusCode);
        AssertNoCorsHeaders(head);
    }

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/nope")]
    [InlineData("GET", "/screens/")]
    [InlineData("GET", "/screen/1")]
    [InlineData("GET", "/Screen")]
    [InlineData("GET", "/screen%20")]
    [InlineData("POST", "/actions/open")]
    [InlineData("OPTIONS", "/nope")]
    public async Task Unknown_route_is_404(string method, string route)
    {
        using var bench = Bench.Started();

        var response = await bench.SendAsync(new HttpMethod(method), route);

        await AssertError(response, HttpStatusCode.NotFound, "not_found");
        AssertNoCorsHeaders(response);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/json-patch+json")]
    [InlineData("text/json")]
    public async Task Wrong_content_type_is_415(string? contentType)
    {
        using var bench = Bench.Started();
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(BumpJson));
        if (contentType is not null)
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        var request = bench.Request(HttpMethod.Post, "/actions");
        request.Content = content;

        await AssertError(await bench.SendAsync(request), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        Assert.Equal(0, bench.Bumps);
        Assert.False(File.Exists(bench.AuditPath));
    }

    [Fact]
    public async Task Oversize_body_is_413()
    {
        using var bench = Bench.Started();

        // A valid action padded with whitespace, so only the size can refuse it.
        await AssertError(await bench.PostAsync(Padded(BumpJson, 65_537)), HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        await AssertError(await bench.PostAsync(Padded(BumpJson, 65_537), chunked: true), HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        await AssertError(await bench.PostAsync(Padded(BumpJson, 1_000_000), chunked: true), HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        Assert.Equal(0, bench.Bumps);
        Assert.False(File.Exists(bench.AuditPath));

        var atTheLimit = await bench.PostAsync(Padded("""{"type":"set","field":"note","value":"fits"}""", 65_536));
        Assert.Equal(HttpStatusCode.OK, atTheLimit.StatusCode);
        Assert.Equal(Outcomes.Ok, (await JsonOf(atTheLimit)).GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"press\"")]
    [InlineData("{}")]
    [InlineData("""{"type":null}""")]
    [InlineData("""{"type":5}""")]
    [InlineData("""{"type":"dance"}""")]
    [InlineData("""{"type":"PRESS","button":"bump"}""")]
    [InlineData("""{"button":"bump"}""")]
    [InlineData("""{"type":"press","button":5}""")]
    [InlineData("""{"type":"set","field":"note","value":{}}""")]
    [InlineData("""{"type":"set","field":"note","value":[1]}""")]
    [InlineData("""{"type":"open","type":"press","button":"bump"}""")]
    [InlineData("""{"type":"press","button":"bump"} {}""")]
    public async Task Bad_json_is_400(string body)
    {
        using var bench = Bench.Started();

        await AssertError(await bench.PostAsync(body), HttpStatusCode.BadRequest, "bad_request");
        Assert.Equal(0, bench.Bumps);
        Assert.False(File.Exists(bench.AuditPath));
    }

    // Requests that are served

    [Fact]
    public async Task Get_screen_and_screens_with_token()
    {
        using var bench = Bench.Started();

        var screens = await bench.SendAsync(HttpMethod.Get, "/screens");
        Assert.Equal(HttpStatusCode.OK, screens.StatusCode);
        Assert.Equal("application/json", screens.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""[{"id":"bench","title":"Bench"},{"id":"other","title":"Other"}]""", await screens.Content.ReadAsStringAsync(Cancel));

        var expected = JsonSerializer.Serialize(bench.Ui.Run(() => ScreenDescriber.Describe(bench.Navigator.Current)), SurfaceJson.Options);
        foreach (var host in new[] { $"127.0.0.1:{bench.Port}", $"localhost:{bench.Port}", $"LocalHost:{bench.Port}" })
        {
            var request = bench.Request(HttpMethod.Get, "/screen", host: host);
            request.Headers.Add("Origin", "http://evil.example");

            var screen = await bench.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, screen.StatusCode);
            Assert.Equal("application/json", screen.Content.Headers.ContentType?.MediaType);
            Assert.Equal(expected, await screen.Content.ReadAsStringAsync(Cancel));
            AssertNoCorsHeaders(screen);
        }
    }

    [Fact]
    public async Task Post_action_returns_the_result_and_audits_it()
    {
        using var bench = Bench.Started();

        var set = await JsonOf(await bench.PostAsync("""{"type":"set","field":"note","value":"Hello"}"""));
        Assert.Equal(Outcomes.Ok, set.GetProperty("outcome").GetString());
        Assert.False(set.TryGetProperty("message", out _));
        Assert.Equal("bench", set.GetProperty("screen").GetProperty("id").GetString());
        Assert.Equal("Hello", FieldValue(set, "note"));
        Assert.Equal("Hello", bench.Ui.Run(() => bench.Navigator.Bench.Note.Text));

        var tooLong = await JsonOf(await bench.PostAsync("""{"type":"set","field":"note","value":"twenty-one characters"}"""));
        Assert.Equal(Outcomes.ValidationFailed, tooLong.GetProperty("outcome").GetString());
        Assert.Equal("Note can be at most 20 characters.", tooLong.GetProperty("message").GetString());

        var number = await JsonOf(await bench.PostAsync("""{"type":"set","field":"note","value":42}"""));
        Assert.Equal("42", FieldValue(number, "note"));

        var boolean = await JsonOf(await bench.PostAsync("""{"type":"set","field":"note","value":true}"""));
        Assert.Equal("true", FieldValue(boolean, "note"));

        var press = await JsonOf(await bench.PostAsync(BumpJson));
        Assert.Equal(Outcomes.Ok, press.GetProperty("outcome").GetString());
        Assert.Equal(1, bench.Bumps);

        var open = await JsonOf(await bench.PostAsync("""{"type":"open","screen":"other"}"""));
        Assert.Equal("other", open.GetProperty("screen").GetProperty("id").GetString());

        var missing = await JsonOf(await bench.PostAsync("""{"type":"select","list":"items","row":"J-1"}"""));
        Assert.Equal(Outcomes.NotFound, missing.GetProperty("outcome").GetString());

        string?[][] expected =
        [
            ["set", "note", "Hello", "ok"],
            ["set", "note", "twenty-one characters", "validation_failed"],
            ["set", "note", "42", "ok"],
            ["set", "note", "true", "ok"],
            ["press", "bump", null, "ok"],
            ["open", "other", null, "ok"],
            ["select", "items/J-1", null, "not_found"],
        ];
        var audited = bench.AuditLines();
        Assert.Equal(expected.Length, audited.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var line = audited[i];
            Assert.Equal(["at", "type", "target", "value", "outcome"], line.EnumerateObject().Select(p => p.Name));
            Assert.True(DateTimeOffset.TryParse(line.GetProperty("at").GetString(), CultureInfo.InvariantCulture, out _));
            Assert.Equal(expected[i], new[]
            {
                line.GetProperty("type").GetString(),
                line.GetProperty("target").GetString(),
                line.GetProperty("value").GetString(),
                line.GetProperty("outcome").GetString(),
            });
        }
    }

    [Fact]
    public async Task Parallel_posts_are_serialised()
    {
        using var bench = Bench.Started();

        var responses = await Task.WhenAll(bench.PostAsync(BumpJson), bench.PostAsync(BumpJson));

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Outcomes.Ok, (await JsonOf(response)).GetProperty("outcome").GetString());
        }

        // Run in sequence, two bumps make two; a bump run inside the other would lose one.
        Assert.Equal(2, bench.Bumps);
        Assert.Equal(2, bench.AuditLines().Count);
    }

    [Fact]
    public async Task Blocked_ui_thread_is_503_and_the_endpoint_recovers()
    {
        using var bench = Bench.Started();
        var clock = Stopwatch.StartNew();

        var blocked = await bench.PostAsync("""{"type":"press","button":"slow"}""");

        await AssertError(blocked, HttpStatusCode.ServiceUnavailable, "ui_timeout");
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(9.5), TimeSpan.FromSeconds(14));

        // The UI thread is still blocked, but the listener keeps answering.
        await AssertError(await bench.SendAsync(HttpMethod.Get, "/screen", token: null), HttpStatusCode.Unauthorized, "unauthorized");
        Assert.True(clock.Elapsed < BenchScreen.SlowFor, "The listener answered only after the UI thread was free.");

        // Once the handler returns, the endpoint serves the UI again.
        var screen = await bench.SendAsync(HttpMethod.Get, "/screen");
        Assert.Equal(HttpStatusCode.OK, screen.StatusCode);
        Assert.True(clock.Elapsed >= BenchScreen.SlowFor);

        // The timed-out press still reached the executor, so it is audited with its real outcome.
        var audited = Assert.Single(bench.AuditLines());
        Assert.Equal("slow", audited.GetProperty("target").GetString());
        Assert.Equal(Outcomes.Ok, audited.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Stop_removes_only_its_own_session_file()
    {
        using var bench = Bench.Created();

        bench.Endpoint.Start();
        Assert.True(File.Exists(bench.SessionPath));
        bench.Endpoint.Stop();
        Assert.False(File.Exists(bench.SessionPath));
        await Assert.ThrowsAsync<HttpRequestException>(() => bench.SendAsync(HttpMethod.Get, "/screen", token: "any"));

        bench.Endpoint.Start();
        SessionFile.Write(bench.Directory, bench.Port, "another-instance");
        bench.Endpoint.Stop();
        Assert.Equal("another-instance", SessionFile.Read(bench.Directory).Token);

        // Stopping twice, or disposing after a stop, changes nothing.
        bench.Endpoint.Stop();
        bench.Endpoint.Dispose();
        Assert.Equal("another-instance", SessionFile.Read(bench.Directory).Token);
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($$"""{"error":"{{error}}"}""", await response.Content.ReadAsStringAsync(Cancel));
    }

    private static void AssertNoCorsHeaders(HttpResponseMessage response)
    {
        var names = response.Headers.Select(h => h.Key).Concat(response.Content.Headers.Select(h => h.Key));
        Assert.DoesNotContain(names, name => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement;
    }

    private static string? FieldValue(JsonElement result, string id) =>
        result.GetProperty("screen").GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("id").GetString() == id)
            .GetProperty("value").GetString();

    private static string Padded(string json, int length) => json + new string(' ', length - Encoding.UTF8.GetByteCount(json));

    /// <summary>
    /// An endpoint on a free port with its own session directory and audit log, over a two-screen
    /// navigator on a live UI thread.
    /// </summary>
    private sealed class Bench : IDisposable
    {
        private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false });

        private Bench()
        {
            Ui = new UiThread();
            Navigator = Ui.Run(() => new BenchNavigator());
            Directory = Path.Combine(Path.GetTempPath(), "SurfaceEndpointTests", Guid.NewGuid().ToString("N"));
            Port = FreePort();
            Endpoint = NewEndpoint();
        }

        public UiThread Ui { get; }
        public BenchNavigator Navigator { get; }
        public string Directory { get; }
        public int Port { get; }
        public SurfaceEndpoint Endpoint { get; }

        public string SessionPath => Path.Combine(Directory, "session.json");
        public string AuditPath => Path.Combine(Directory, "audit.jsonl");
        public string Token => SessionFile.Read(Directory).Token;
        public int Bumps => Ui.Run(() => Navigator.Bench.Bumps);

        public static Bench Created() => new();

        public static Bench Started()
        {
            var bench = new Bench();
            bench.Endpoint.Start();
            return bench;
        }

        public SurfaceEndpoint NewEndpoint() =>
            new(IPAddress.Loopback, Port, Directory, Navigator.Host, Navigator, new AuditLog(AuditPath));

        /// <summary>A request with the current token and the endpoint's own Host, unless told otherwise.</summary>
        public HttpRequestMessage Request(HttpMethod method, string route, string? host = null, string? token = "current")
        {
            var request = new HttpRequestMessage(method, new Uri($"http://127.0.0.1:{Port}{route}"));
            request.Headers.Host = host ?? $"127.0.0.1:{Port}";

            if (token is not null)
            {
                request.Headers.TryAddWithoutValidation("X-Surface-Token", token == "current" ? Token : token);
            }

            return request;
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => http.SendAsync(request, Cancel);

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, string? host = null, string? token = "current") =>
            SendAsync(Request(method, route, host, token));

        public Task<HttpResponseMessage> PostAsync(string json, string? host = null, string? token = "current", bool chunked = false)
        {
            var request = Request(HttpMethod.Post, "/actions", host, token);
            var bytes = Encoding.UTF8.GetBytes(json);
            request.Content = chunked ? new ChunkedContent(bytes) : new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            return SendAsync(request);
        }

        public IReadOnlyList<JsonElement> AuditLines() =>
            [.. File.ReadAllLines(AuditPath).Select(line => JsonDocument.Parse(line).RootElement)];

        public void Dispose()
        {
            Endpoint.Dispose();
            http.Dispose();
            Ui.Run(Navigator.Dispose);
            Ui.Dispose();

            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    /// <summary>A body sent without a Content-Length, in chunks.</summary>
    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A shown host with the bench screen and a second screen, one visible at a time.</summary>
    private sealed class BenchNavigator : IScreenNavigator, IDisposable
    {
        public BenchNavigator()
        {
            OtherScreen.Visible = false;
            Host.Controls.AddRange([Bench, OtherScreen]);
            Current = Bench;
            Host.Show();
        }

        public Form Host { get; } = TestForms.Host();
        public BenchScreen Bench { get; } = new();
        public UserControl OtherScreen { get; } = TestForms.Other();

        public IReadOnlyList<(string Id, string Title)> Screens { get; } = [("bench", "Bench"), ("other", "Other")];

        public Control Current { get; private set; }

        public bool Open(string id)
        {
            Control next = id == "bench" ? Bench : OtherScreen;
            Current.Visible = false;
            next.Visible = true;
            Current = next;
            return true;
        }

        public void Dispose() => Host.Dispose();
    }

    /// <summary>A note field, a button whose handler can expose overlapping actions, and a button that blocks.</summary>
    private sealed class BenchScreen : UserControl
    {
        public static readonly TimeSpan SlowFor = TimeSpan.FromSeconds(15);

        public BenchScreen()
        {
            Dock = DockStyle.Fill;
            Surface.Screen(this, "bench", "Bench");

            Note = new TextBox { TabIndex = 0 }.Meta("note", "Note", maxLength: 20);

            var bump = new Button { TabIndex = 1, Text = "Bump" }.Meta("bump", "Bump");
            bump.Click += (_, _) => Bump();

            var slow = new Button { TabIndex = 2, Text = "Slow" }.Meta("slow", "Slow");
            slow.Click += (_, _) => Thread.Sleep(SlowFor);

            Controls.AddRange([Note, bump, slow]);
        }

        public TextBox Note { get; }

        public int Bumps { get; private set; }

        /// <summary>
        /// Reads the count, lets the message loop run as a long handler might, then writes the
        /// count back: an action run inside this one would have its increment overwritten.
        /// </summary>
        private void Bump()
        {
            var seen = Bumps;
            var until = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 4);
            while (Stopwatch.GetTimestamp() < until)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }

            Bumps = seen + 1;
        }
    }

    private sealed class NoScreens : IScreenNavigator
    {
        public IReadOnlyList<(string Id, string Title)> Screens => [];

        public Control Current => throw new InvalidOperationException("No screen.");

        public bool Open(string id) => false;
    }
}
