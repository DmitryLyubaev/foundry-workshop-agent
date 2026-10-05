using System.Diagnostics;
using Azure.Core;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Workshop.Agent.Telemetry;

/// <summary>
/// Sends the agent's traces to Application Insights (spec §4.7), signed in with Entra: the resource
/// has local authentication off, so there is no key to send. The spans come from the agent's own
/// source, from Agent Framework's (the run's <c>invoke_agent</c> span) and from the Foundry SDK's
/// (its GenAI spans). Every span has its identifiers redacted before any exporter sees it, or is not
/// exported when they cannot be (Review Focus 4). Message content is not captured unless
/// <c>captureContent</c> asks for it.
/// </summary>
public static class TraceExport
{
    /// <summary>The environment variable holding the Application Insights connection string (Task 4's output).</summary>
    public const string ConnectionStringVariable = "FWA_APPINSIGHTS_CONNECTION_STRING";

    /// <summary>
    /// Agent Framework's source: the name <c>OpenTelemetryAgent</c> uses when given none, as Microsoft.Agents.AI
    /// 1.23 defines it (<c>OpenTelemetryAgent.DefaultSourceName</c>, marked experimental, so a test checks the two agree).
    /// </summary>
    public const string AgentFrameworkSource = "Experimental.Microsoft.Agents.AI";

    /// <summary>The Foundry SDK's sources, such as <c>Azure.AI.Projects.ProjectResponsesClient</c>.</summary>
    public const string AzureAIProjectsSources = "Azure.AI.Projects.*";

    /// <summary>The Foundry SDK's switch for its GenAI spans (Azure.AI.Extensions.OpenAI 2.0.0).</summary>
    public const string GenAITracingSwitch = "Azure.Experimental.EnableGenAITracing";

    /// <summary>
    /// The Foundry SDK's switch for message content in its spans. Set either way: unset, the SDK
    /// reads <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c> instead.
    /// </summary>
    public const string GenAIContentSwitch = "Azure.Experimental.TraceGenAIMessageContent";

    private static int contentCaptured;

    /// <summary>The sources the export subscribes to.</summary>
    public static IReadOnlyList<string> Sources { get; } = [AgentTelemetry.SourceName, AgentFrameworkSource, AzureAIProjectsSources];

    /// <summary>
    /// True while an export that asked for message content runs: the engines' agents then put the
    /// messages, tool arguments and tool results in their spans. For development only.
    /// </summary>
    public static bool CaptureContent => Volatile.Read(ref contentCaptured) == 1;

    /// <summary>
    /// Starts the export; disposing the result sends what is left and stops it. Started before any
    /// Foundry client is made, as the SDK reads its switches once.
    /// </summary>
    /// <param name="connectionString">Application Insights' connection string: where to send, not a secret, but kept out of every log.</param>
    /// <param name="credential">The Entra credential the exporter asks for an Azure Monitor token.</param>
    /// <param name="captureContent">Whether the spans may hold message content; off unless asked for.</param>
    public static IDisposable Start(string connectionString, TokenCredential credential, bool captureContent = false) =>
        Start(connectionString, credential, captureContent, configure: null, alsoExportTo: null);

    /// <summary>For tests: the exporter's options changed after this class sets them (its HTTP transport), and a second exporter.</summary>
    internal static IDisposable Start(
        string connectionString,
        TokenCredential credential,
        bool captureContent,
        Action<AzureMonitorExporterOptions>? configure,
        Action<TracerProviderBuilder>? alsoExportTo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(credential);

        AppContext.SetSwitch(GenAITracingSwitch, true);
        AppContext.SetSwitch(GenAIContentSwitch, captureContent);
        Volatile.Write(ref contentCaptured, captureContent ? 1 : 0);

        // Where an exception is recorded on a span (Activity.AddException), its message and stack trace go in redacted.
        var exceptions = new ActivityListener
        {
            ShouldListenTo = source => Subscribes(source.Name),
            ExceptionRecorder = RecordRedacted,
        };
        ActivitySource.AddActivityListener(exceptions);
        try
        {
            var builder = Sdk.CreateTracerProviderBuilder()
                .AddSource([.. Sources])
                // First, so every exporter after it sees the span redacted.
                .AddProcessor(new RedactingProcessor());
            alsoExportTo?.Invoke(builder);
            builder.AddAzureMonitorTraceExporter(o =>
            {
                o.ConnectionString = connectionString;
                // Entra: the exporter signs its requests with a token for Azure Monitor.
                o.Credential = credential;
                // Every span: a study is a few hundred runs, and a run missing from the trace would be a gap in it.
                o.TracesPerSecond = null;
                o.SamplingRatio = 1f;
                // Not supported by this exporter, which says so in its log when left on.
                o.EnableLiveMetrics = false;
                configure?.Invoke(o);
            });
            return new Export(builder.Build(), exceptions);
        }
        catch
        {
            exceptions.Dispose();
            Stop();
            throw;
        }
    }

    /// <summary>Whether a source of this name is one of <see cref="Sources"/>.</summary>
    internal static bool Subscribes(string sourceName) =>
        sourceName is AgentTelemetry.SourceName or AgentFrameworkSource
        || sourceName.StartsWith(AzureAIProjectsSources[..^1], StringComparison.Ordinal);

    /// <summary>
    /// Gives an exception event its message and stack trace redacted. Activity.AddException adds the
    /// raw ones only when these tags are not there yet, and an event's tags cannot be changed after.
    /// </summary>
    private static void RecordRedacted(Activity activity, Exception exception, ref TagList tags)
    {
        bool message = false, stackTrace = false;
        foreach (var tag in tags)
        {
            message |= tag.Key == "exception.message";
            stackTrace |= tag.Key == "exception.stacktrace";
        }

        if (!message)
        {
            tags.Add("exception.message", Redaction.Redact(exception.Message));
        }

        if (!stackTrace)
        {
            tags.Add("exception.stacktrace", Redaction.Redact(exception.ToString()));
        }
    }

    private static void Stop()
    {
        Volatile.Write(ref contentCaptured, 0);
        AppContext.SetSwitch(GenAIContentSwitch, false);
    }

    /// <summary>The running export: disposing it sends the spans not yet sent, stops, and turns content capture off.</summary>
    private sealed class Export(TracerProvider provider, ActivityListener exceptions) : IDisposable
    {
        /// <summary>How long the last spans may take to send as the run ends.</summary>
        private const int FlushMilliseconds = 10_000;

        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                // Sent now: shut down without a flush, the exporter writes the last spans to disk and
                // sends them only on a later run (Azure.Monitor.OpenTelemetry.Exporter 1.9's shutdown persistence).
                provider.ForceFlush(FlushMilliseconds);
                provider.Dispose();
                exceptions.Dispose();
                Stop();
            }
        }
    }
}

/// <summary>
/// Takes identifiers out of every span before it is exported: its name, its string tags and its
/// status, through <see cref="Redaction.Redact(string?)"/>. Agent Framework, Microsoft.Extensions.AI
/// and the Foundry SDK record a failure's raw message as the span's status (a service's message names
/// principals and hosts), and their spans carry the service's host as <c>server.address</c>. An
/// event's tags cannot be changed once recorded, so a span with an identifier in an event is not
/// exported at all.
/// </summary>
internal sealed class RedactingProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        data.DisplayName = Redaction.Redact(data.DisplayName) ?? string.Empty;
        if (data.StatusDescription is { } description)
        {
            data.SetStatus(data.Status, Redaction.Redact(description));
        }

        // Read first, then set: setting a tag while the tags are read would change what is being read.
        foreach (var (key, value) in Redacted(data.TagObjects))
        {
            data.SetTag(key, value);
        }

        // Exception events recorded through Activity.AddException arrive redacted (TraceExport's listener);
        // any other event that holds an identifier keeps the span out of the export.
        if (data.Events.Any(e => Redacted(e.Tags).Count > 0))
        {
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }

    /// <summary>The string tags whose redacted value differs, with that value.</summary>
    private static List<KeyValuePair<string, string>> Redacted(IEnumerable<KeyValuePair<string, object?>> tags)
    {
        var changed = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in tags)
        {
            if (value is string text && Redaction.Redact(text) is { } redacted && !string.Equals(redacted, text, StringComparison.Ordinal))
            {
                changed.Add(new(key, redacted));
            }
        }

        return changed;
    }
}
