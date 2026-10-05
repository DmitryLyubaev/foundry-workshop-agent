#pragma warning disable MAAI001 // OpenTelemetryAgent.DefaultSourceName is marked experimental; read here only to check the name the export subscribes to.

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azure.Core.Pipeline;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;
using Workshop.Agent.Engines;
using Workshop.Agent.Engines.Claude;
using Workshop.Agent.Telemetry;
using Workshop.Agent.Tests.RealEngines;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests;

/// <summary>
/// The trace export to Application Insights (spec §4.7): the sources it subscribes to, the Entra
/// credential it signs in with, content capture off unless asked for, and no identifier in what it
/// sends (Review Focus 4). Application Insights is faked under the exporter's own HTTP pipeline, and
/// the spans are also read from memory: nothing leaves the process.
/// </summary>
/// <remarks>
/// In a collection of its own, run alone: an export listens to Agent Framework's source process-wide,
/// which would put an <c>invoke_agent</c> span between other tests' runs and their model calls, and
/// content capture is a process-wide switch.
/// </remarks>
[Collection(TraceExportCollection.Name)]
public sealed class TraceExportTests
{
    // A placeholder resource: the instrumentation key is all zeros, and the endpoint can never resolve.
    private const string ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.invalid/";

    private const string MonitorScope = "https://monitor.azure.com//.default";

    // Made-up identifiers, distinct enough that finding one anywhere means it leaked.
    private const string Principal = "11111111-2222-3333-4444-555555555555";
    private const string Host = "example-leak-resource.services.ai.azure.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exporter_is_configured_with_the_credential_and_sources()
    {
        var ingestion = new FakeIngestion();
        var credential = new FakeCredential();
        var exported = new List<Activity>();

        ActivityTraceId trace;
        using (var export = Start(ingestion, credential, exported))
        using (var projects = new ActivitySource("Azure.AI.Projects.ProjectResponsesClient"))
        using (var other = new ActivitySource("Example.NotSubscribed"))
        {
            using var run = AgentTelemetry.StartScenarioRun("s01", 1, "fake", "scripted");
            Assert.NotNull(run);
            trace = run.TraceId;
            var result = await Engine("""[{ "reply": "Done." }]""").RunAsync("Look.", [], Cancel);
            Assert.Equal(EngineOutcome.Completed, result.Outcome);
            projects.StartActivity("invoke_agent fwa-workshop-agent")?.Dispose();
            Assert.Null(other.StartActivity("not exported"));
        }

        var spans = exported.Where(a => a.TraceId == trace).ToArray();

        // The three sources, and nothing else: the agent's own, Agent Framework's and the Foundry SDK's.
        Assert.Equal(
            ["Azure.AI.Projects.ProjectResponsesClient", TraceExport.AgentFrameworkSource, AgentTelemetry.SourceName],
            spans.Select(a => a.Source.Name).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal([AgentTelemetry.SourceName, TraceExport.AgentFrameworkSource, "Azure.AI.Projects.*"], TraceExport.Sources);

        // Agent Framework 1.23's source, as the package names it, and the span its agent makes for the run.
        Assert.Equal(TraceExport.AgentFrameworkSource, OpenTelemetryAgent.DefaultSourceName);
        var invoke = Assert.Single(spans, a => a.Source.Name == TraceExport.AgentFrameworkSource);
        Assert.Equal("invoke_agent", invoke.GetTagItem("gen_ai.operation.name"));
        Assert.Contains(spans, a => a.OperationName == AgentTelemetry.ModelCall && a.ParentSpanId == invoke.SpanId);

        // Entra, not a key: the exporter asked the credential for Azure Monitor's scope, and sent its token.
        Assert.Contains(MonitorScope, credential.Scopes);
        var sent = ingestion.Requests;
        Assert.NotEmpty(sent);
        Assert.All(sent, r =>
        {
            Assert.Equal("Bearer fake-token", r.Authorization);
            Assert.Equal("example.invalid", r.Uri.Host);
        });
        var body = string.Join('\n', sent.Select(r => r.Body));
        Assert.Contains(AgentTelemetry.ScenarioRun, body, StringComparison.Ordinal);
        Assert.Contains("invoke_agent", body, StringComparison.Ordinal);

        // Foundry's own GenAI spans are switched on.
        Assert.True(AppContext.TryGetSwitch(TraceExport.GenAITracingSwitch, out var genAI) && genAI);
    }

    [Fact]
    public async Task Trace_refused_without_a_connection_string()
    {
        foreach (var value in new string?[] { null, "", "  " })
        {
            // The scenario directory does not exist: the refusal comes before anything is read or started.
            var (code, output, error) = await Run(
                name => name == TraceExport.ConnectionStringVariable ? value : null,
                "run", "--engine", "fake", "--scenarios", "no-such-directory", "--script-dir", RepoPaths.Scripts, "--trace");

            Assert.Equal(2, code);
            Assert.Equal("", output);
            Assert.Equal(
                "FWA_APPINSIGHTS_CONNECTION_STRING is not set: --trace sends the traces to the Application Insights it names. Set it, or leave out --trace.",
                error.Trim());
        }
    }

    [Fact]
    public async Task Trace_refused_with_a_connection_string_it_cannot_read()
    {
        // The exporter's own message quotes what it could not read: the run is refused, and the quote redacted.
        var (code, output, error) = await Run(
            name => name == TraceExport.ConnectionStringVariable ? $"InstrumentationKey={Principal};IngestionEndpoint=https://{Host}:port/" : null,
            "run", "--engine", "fake", "--scenarios", RepoPaths.Scenarios, "--script-dir", RepoPaths.Scripts, "--only", "s05", "--trace");

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.StartsWith("FWA_APPINSIGHTS_CONNECTION_STRING cannot be used, so no run starts: ", error, StringComparison.Ordinal);
        Assert.DoesNotContain(Principal, error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example-leak-resource", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(TraceExport.CaptureContent);
    }

    [Theory]
    [InlineData("--trace-content")]
    [InlineData("--trace", "--trace-content", "--frozen")]
    [InlineData("--trace", "--trace-content", "--study")]
    public async Task Trace_content_needs_trace_and_is_refused_in_a_study(params string[] flags)
    {
        var (code, output, error) = await Run(
            name => name == TraceExport.ConnectionStringVariable ? ConnectionString : null,
            ["run", "--engine", "fake", "--scenarios", "no-such-directory", "--script-dir", RepoPaths.Scripts, .. flags]);

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Equal(
            flags.Contains("--trace")
                ? "--trace-content is for development only: it cannot be used with --study or --frozen."
                : "--trace-content needs --trace.",
            error.Trim());
    }

    [Fact]
    public async Task Content_capture_off_by_default()
    {
        var exported = new List<Activity>();

        ActivityTraceId trace;
        using (Start(new FakeIngestion(), new FakeCredential(), exported))
        {
            Assert.False(TraceExport.CaptureContent);
            // Foundry's GenAI spans hold no message content either; the switch wins over the environment variable.
            Assert.True(AppContext.TryGetSwitch(TraceExport.GenAIContentSwitch, out var content));
            Assert.False(content);

            trace = await RunWithMarkers();
        }

        var spans = exported.Where(a => a.TraceId == trace).ToArray();
        Assert.Contains(spans, a => a.Source.Name == TraceExport.AgentFrameworkSource);
        var text = Everything(spans);
        Assert.DoesNotContain("marker-task", text, StringComparison.Ordinal);
        Assert.DoesNotContain("marker-argument", text, StringComparison.Ordinal);
        Assert.DoesNotContain("marker-reply", text, StringComparison.Ordinal);
        Assert.DoesNotContain(spans, a => a.GetTagItem("gen_ai.input.messages") is not null || a.GetTagItem("gen_ai.output.messages") is not null);
    }

    [Fact]
    public async Task Content_capture_on_only_when_asked_and_off_again_after()
    {
        var exported = new List<Activity>();

        ActivityTraceId trace;
        using (Start(new FakeIngestion(), new FakeCredential(), exported, captureContent: true))
        {
            Assert.True(TraceExport.CaptureContent);
            Assert.True(AppContext.TryGetSwitch(TraceExport.GenAIContentSwitch, out var content) && content);
            trace = await RunWithMarkers();
        }

        // The messages are in the agent's span: so the test above, finding none, means none were captured.
        var text = Everything(exported.Where(a => a.TraceId == trace));
        Assert.Contains("marker-task", text, StringComparison.Ordinal);
        Assert.Contains("marker-reply", text, StringComparison.Ordinal);

        Assert.False(TraceExport.CaptureContent);
        Assert.True(AppContext.TryGetSwitch(TraceExport.GenAIContentSwitch, out var after));
        Assert.False(after);
    }

    [Fact]
    public async Task A_service_error_reaches_the_exported_trace_redacted()
    {
        // Agent Framework's span records the raw exception message as its status: a principal and a host here.
        var service = new FakeModelService(() => FakeModelService.Json(403, Recorded.AnthropicError(
            "permission_error",
            $"Principal {Principal} does not have access to https://{Host}/anthropic/v1/messages.")));
        var ingestion = new FakeIngestion();
        var exported = new List<Activity>();

        ActivityTraceId trace;
        using (Start(ingestion, new FakeCredential(), exported))
        using (var projects = new ActivitySource("Azure.AI.Projects.ProjectResponsesClient"))
        {
            using var run = AgentTelemetry.StartScenarioRun("s05", 1, ClaudeEngineFactory.Name, "claude-haiku-4-5");
            trace = run!.TraceId;
            var engine = ClaudeEngineFactory.Create(FakeAgentService.Options, new ToolBudget(), TimeSpan.FromMinutes(5), null, new FakeCredential(), service.Client());
            var result = await engine.RunAsync("Look.", [], Cancel);
            Assert.Equal(EngineOutcome.ServiceError, result.Outcome);

            // As the Foundry SDK records a failure: its host as a tag, the raw message as the status, the exception as an event.
            using (var call = projects.StartActivity("chat example"))
            {
                Assert.NotNull(call);
                call.SetTag("server.address", Host);
                call.SetStatus(ActivityStatusCode.Error, $"Principal {Principal} lacks access.");
                call.AddException(new InvalidOperationException($"Principal {Principal} at https://{Host}/api lacks access."));
            }

            // An event added with its tags already made cannot be redacted: its span is not exported.
            using (var leaky = projects.StartActivity("leaky example"))
            {
                Assert.NotNull(leaky);
                leaky.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection { ["exception.message"] = $"Principal {Principal} lacks access." }));
            }
        }

        var spans = exported.Where(a => a.TraceId == trace).ToArray();
        var invoke = Assert.Single(spans, a => a.Source.Name == TraceExport.AgentFrameworkSource);
        Assert.Equal(ActivityStatusCode.Error, invoke.Status);
        // Redacted, not dropped: the failure is still described.
        Assert.Contains(Redaction.Guid, invoke.StatusDescription, StringComparison.Ordinal);
        var failed = Assert.Single(spans, a => a.DisplayName == "chat example");
        Assert.Equal(Redaction.Host, failed.GetTagItem("server.address"));
        var exception = Assert.Single(failed.Events);
        Assert.Equal($"Principal {Redaction.Guid} at {Redaction.Url} lacks access.", exception.Tags.Single(t => t.Key == "exception.message").Value);
        Assert.DoesNotContain(spans, a => a.DisplayName == "leaky example");

        var text = Everything(spans);
        var body = string.Join('\n', ingestion.Requests.Select(r => r.Body));
        Assert.Contains($"Principal {Redaction.Guid} at {Redaction.Url} lacks access.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("leaky example", body, StringComparison.Ordinal);
        foreach (var identifier in new[] { Principal, Host, "example-leak-resource" })
        {
            Assert.DoesNotContain(identifier, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(identifier, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The export, sending to <paramref name="ingestion"/> and also to <paramref name="exported"/>.</summary>
    private static IDisposable Start(FakeIngestion ingestion, FakeCredential credential, List<Activity> exported, bool captureContent = false) =>
        TraceExport.Start(
            ConnectionString,
            credential,
            captureContent,
            o =>
            {
                o.Transport = new HttpClientTransport(new HttpClient(ingestion, disposeHandler: false));
                // Nothing written to disk for a later retry.
                o.DisableOfflineStorage = true;
            },
            b => b.AddInMemoryExporter(exported));

    /// <summary>One run whose task, tool argument and reply each carry a marker; its trace's id.</summary>
    private static async Task<ActivityTraceId> RunWithMarkers()
    {
        using var run = AgentTelemetry.StartScenarioRun("s01", 1, "fake", "scripted");
        var engine = Engine("""
            [
              { "call": "open_screen", "args": { "screen": "marker-argument" } },
              { "reply": "marker-reply" }
            ]
            """);
        var open = AIFunctionFactory.Create((string screen) => $"opened {screen}", "open_screen");
        var result = await engine.RunAsync("marker-task", [open], Cancel);
        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        return run!.TraceId;
    }

    private static ChatClientEngine Engine(string script) =>
        new("fake", "scripted", new ScriptedChatClient(Script.Parse(script)), new ToolBudget(), TimeSpan.FromMinutes(5));

    /// <summary>Every name, tag, status and event of <paramref name="spans"/>, as one text to search.</summary>
    private static string Everything(IEnumerable<Activity> spans) => string.Join('\n', spans.SelectMany(a =>
        a.TagObjects.Select(t => $"{t.Key}={t.Value}")
            .Concat(a.Events.SelectMany(e => e.Tags.Select(t => $"{e.Name}.{t.Key}={t.Value}")))
            .Append(a.DisplayName)
            .Append(a.StatusDescription ?? "")));

    private static async Task<(int Code, string Output, string Error)> Run(Func<string, string?> environment, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Program.RunAsync(args, output, error, environment, Cancel);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>One request the exporter sent to ingestion, its body unzipped.</summary>
    private sealed record IngestionRequest(Uri Uri, string? Authorization, string Body);

    /// <summary>Application Insights' ingestion endpoint, faked: it keeps every request and accepts every item.</summary>
    private sealed class FakeIngestion : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly Lock gate = new();
        private readonly List<IngestionRequest> requests = [];

        public IReadOnlyList<IngestionRequest> Requests
        {
            get
            {
                lock (gate)
                {
                    return [.. requests];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            if (request.Content?.Headers.ContentEncoding.Contains("gzip") == true)
            {
                using var unzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var plain = new MemoryStream();
                await unzip.CopyToAsync(plain, cancellationToken);
                bytes = plain.ToArray();
            }

            // One JSON item a line, written again without escapes, so that a test finds "<guid>" as such.
            var body = string.Join('\n', Encoding.UTF8.GetString(bytes)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Serialize(JsonDocument.Parse(line).RootElement, Unescaped)));
            lock (gate)
            {
                requests.Add(new IngestionRequest(request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            }

            var items = body.Split('\n').Length;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"itemsReceived":{{items}},"itemsAccepted":{{items}},"errors":[]}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TraceExportCollection
{
    public const string Name = "Trace export";
}
