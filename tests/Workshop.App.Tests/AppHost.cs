using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Workshop.Core;
using Workshop.Surface;

namespace Workshop.App.Tests;

/// <summary>
/// The workshop app, launched as <c>--db</c> launches it, on an STA thread of its own: a fresh
/// database, a free port and a session directory, all in a temporary directory deleted at the end.
/// Tests drive it through HTTP, as the agent does, and read the database to check what happened.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) });
    private readonly Thread thread;
    private readonly MainForm form;
    private readonly SurfaceEndpoint endpoint;

    private AppHost()
    {
        Directory = Path.Combine(Path.GetTempPath(), "WorkshopAppTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        DatabasePath = Path.Combine(Directory, "workshop.db");
        SessionDirectory = Path.Combine(Directory, "session");
        Port = FreePort();
        _ = WorkshopDb.CreateFresh(DatabasePath);
        Jobs = new JobService(WorkshopDb.OpenExisting(DatabasePath));

        var started = new TaskCompletionSource<Program.Launched>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            Program.Launched launched;
            try
            {
                launched = Program.Launch(new AppOptions(DatabasePath, Port, SessionDirectory));

                // Off screen and out of the taskbar, so test runs do not flash windows.
                launched.Form.ShowInTaskbar = false;
                launched.Form.StartPosition = FormStartPosition.Manual;
                launched.Form.Location = new Point(-3000, -3000);
                started.SetResult(launched);
            }
            catch (Exception e)
            {
                started.SetException(e);
                return;
            }

            Application.Run(launched.Form);
        })
        {
            IsBackground = true,
            Name = "WorkshopApp",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!started.Task.Wait(Timeout))
        {
            throw new TimeoutException("The app did not start.");
        }

        (form, endpoint) = started.Task.Result;
    }

    public string Directory { get; }

    public string DatabasePath { get; }

    public string SessionDirectory { get; }

    public int Port { get; }

    /// <summary>The app's audit log: <c>audit.jsonl</c>, beside the database.</summary>
    public string AuditPath => Path.Combine(Directory, "audit.jsonl");

    /// <summary>A second view of the app's database, for checking what the app wrote.</summary>
    public JobService Jobs { get; }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static AppHost Start() => new();

    /// <summary>Runs <paramref name="work"/> on the app's UI thread, with its main form.</summary>
    public T OnUi<T>(Func<MainForm, T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        form.Invoke(() =>
        {
            try
            {
                result = work(form);
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        });

        failure?.Throw();
        return result;
    }

    public void OnUi(Action<MainForm> work) => OnUi(f =>
    {
        work(f);
        return 0;
    });

    /// <summary><c>GET /screen</c>: the current screen's description.</summary>
    public async Task<JsonElement> ScreenAsync()
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, "/screen"), Cancel);
        return await ReadOkAsync(response);
    }

    /// <summary><c>POST /actions</c> with one action; returns the whole result: outcome, message and screen.</summary>
    public async Task<JsonElement> ActAsync(object action)
    {
        var request = Request(HttpMethod.Post, "/actions");
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(action));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using var response = await http.SendAsync(request, Cancel);
        return await ReadOkAsync(response);
    }

    public Task<JsonElement> OpenAsync(string screen) => ActAsync(new { type = "open", screen });

    public Task<JsonElement> SetAsync(string field, string value) => ActAsync(new { type = "set", field, value });

    public Task<JsonElement> SelectAsync(string list, string row) => ActAsync(new { type = "select", list, row });

    public Task<JsonElement> PressAsync(string button) => ActAsync(new { type = "press", button });

    /// <summary>Opens a job as a person would: the job list, its row, then Open job.</summary>
    public async Task<JsonElement> OpenJobAsync(string jobId)
    {
        AssertOk(await OpenAsync("job-list"));
        AssertOk(await SelectAsync("jobs", jobId));
        return AssertOk(await PressAsync("open-job"));
    }

    public static JsonElement AssertOk(JsonElement result)
    {
        Assert.Equal("ok", result.GetProperty("outcome").GetString());
        return result;
    }

    public IReadOnlyList<JsonElement> AuditLines() =>
        [.. File.ReadAllLines(AuditPath).Select(line => JsonDocument.Parse(line).RootElement)];

    public void Dispose()
    {
        endpoint.Dispose();
        http.Dispose();
        form.BeginInvoke(form.Close);

        if (!thread.Join(Timeout))
        {
            throw new TimeoutException("The app did not stop.");
        }

        System.IO.Directory.Delete(Directory, recursive: true);
    }

    private HttpRequestMessage Request(HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, new Uri($"http://127.0.0.1:{Port}{route}"));
        request.Headers.TryAddWithoutValidation("X-Surface-Token", SessionFile.Read(SessionDirectory).Token);
        return request;
    }

    private static async Task<JsonElement> ReadOkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Cancel);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(Encoding.UTF8.GetBytes(body));
        return document.RootElement.Clone();
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
