using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Workshop.Agent;

/// <summary>
/// Takes identifiers out of text before it reaches a transcript, the console (a CI log) or a trace
/// (Review Focus 4). The services' error bodies name principals' object IDs, subscriptions, project
/// paths and host names, and an HTTP client's message names the host it could not reach; each of
/// those becomes a fixed placeholder. What the text says about the failure (its status, its error
/// code, its type) is left as it was.
/// </summary>
/// <remarks>
/// Replaced, in this order: bearer tokens and JWTs (<c>&lt;token&gt;</c>), URLs (<c>&lt;url&gt;</c>),
/// ARM resource paths (<c>/subscriptions/&lt;redacted&gt;</c>) and project paths
/// (<c>/projects/&lt;project&gt;</c>), host names under azure.com, azure.net, windows.net,
/// microsoft.com, microsoftonline.com and anthropic.com (<c>&lt;host&gt;</c>), email addresses
/// (<c>&lt;email&gt;</c>) and GUIDs (<c>&lt;guid&gt;</c>).
/// </remarks>
public static partial class Redaction
{
    public const string Token = "<token>";
    public const string Url = "<url>";
    public const string Host = "<host>";
    public const string Email = "<email>";
    public const string Guid = "<guid>";

    /// <summary><paramref name="text"/> with every identifier replaced by its placeholder; null stays null.</summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = BearerToken().Replace(text, "Bearer " + Token);
        text = Jwt().Replace(text, Token);
        text = UrlPattern().Replace(text, Url);
        text = ResourcePath().Replace(text, "/subscriptions/<redacted>");
        text = ProjectPath().Replace(text, "/projects/<project>");
        text = HostPattern().Replace(text, Host);
        text = EmailPattern().Replace(text, Email);
        return GuidPattern().Replace(text, Guid);
    }

    /// <summary><c>{type}: {message}</c>, with the message redacted: how a failure is described anywhere it is kept.</summary>
    public static string Describe(Exception e, bool fullName = true)
    {
        ArgumentNullException.ThrowIfNull(e);
        return $"{(fullName ? e.GetType().FullName : e.GetType().Name)}: {Redact(e.Message)}";
    }

    /// <summary>
    /// Records <paramref name="e"/> on <paramref name="span"/> as the OpenTelemetry <c>exception</c>
    /// event, with its type and its redacted message, and no stack trace: in place of
    /// <see cref="Activity.AddException(Exception, in TagList, DateTimeOffset)"/>, which keeps the raw message.
    /// </summary>
    public static void AddException(Activity? span, Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        span?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = e.GetType().FullName,
            ["exception.message"] = Redact(e.Message),
        }));
    }

    [GeneratedRegex(@"\bbearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    // A URL's trailing comma, full stop or colon is the sentence's, not the URL's.
    [GeneratedRegex(@"\b(?:https?|wss?)://[^\s""'<>()\[\]{}]*[^\s""'<>()\[\]{},.;:]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"/subscriptions/[^\s""'<>]*[^\s""'<>,.;:]",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourcePath();

    [GeneratedRegex(@"/projects/[^\s""'<>/?#]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectPath();

    [GeneratedRegex(@"\b(?:[A-Za-z0-9-]+\.)+(?:azure\.com|azure\.net|windows\.net|microsoft\.com|microsoftonline\.com|anthropic\.com)(?::\d+)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();
}
