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
/// (<c>&lt;email&gt;</c>) and GUIDs (<c>&lt;guid&gt;</c>). Then the local paths that an app or a run
/// failing names: this machine's temp directory (<c>&lt;temp&gt;</c>), its user profile or any
/// other <c>X:\Users\&lt;name&gt;</c> (<c>&lt;home&gt;</c>), a run directory's 32-hex name
/// (<c>&lt;run&gt;</c>), and any other 32-hex value (<c>&lt;hex&gt;</c>), which is the shape the
/// study's scan takes for a key. A SHA-256, 64 hex characters, is left as it is.
/// </remarks>
public static partial class Redaction
{
    public const string Token = "<token>";
    public const string Url = "<url>";
    public const string Host = "<host>";
    public const string Email = "<email>";
    public const string Guid = "<guid>";
    public const string Temp = "<temp>";
    public const string Home = "<home>";
    public const string Run = "<run>";
    public const string Hex = "<hex>";

    /// <summary>This machine's temp directory and user profile, longest first: the temp directory is usually under the profile.</summary>
    private static readonly Lazy<(string Path, string Placeholder)[]> LocalDirectories = new(() =>
        [.. new[]
            {
                (Path: Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Placeholder: Temp),
                (Path: Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), Placeholder: Home),
            }
            // A drive's root, or no path at all, would match every path: only a real directory is replaced.
            .Where(d => d.Path.Length > 3)
            .OrderByDescending(d => d.Path.Length)]);

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
        text = GuidPattern().Replace(text, Guid);
        return LocalPaths(text);
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

    /// <summary>
    /// The temp directory and the user profile, which name the user, and a run's 32-hex directory
    /// name. An app that cannot start names its paths, and the study's scan flags a user name or a
    /// 32-hex value, so none of them may reach a transcript or a log.
    /// </summary>
    private static string LocalPaths(string text)
    {
        foreach (var (path, placeholder) in LocalDirectories.Value)
        {
            text = text.Replace(path, placeholder, StringComparison.OrdinalIgnoreCase);
        }

        text = ProfilePattern().Replace(text, Home);
        text = HomeTempPattern().Replace(text, Temp);
        text = RunDirectoryPattern().Replace(text, "${dir}" + Run);
        return Hex32Pattern().Replace(text, Hex);
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

    // Any user's profile on any drive, by either separator, however the name is written (an 8.3 short name too).
    [GeneratedRegex(@"\b[A-Za-z]:[\\/]Users[\\/][^\\/\s""'<>:*?|;,()]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfilePattern();

    // A profile's temp directory, once the profile is <home>: Windows' default %TEMP%.
    [GeneratedRegex(@"<home>[\\/]AppData[\\/]Local[\\/]Temp(?![^\\/\s""'<>;,()])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HomeTempPattern();

    // The runner's run directories (and the tests'), each named by a GUID's 32 hex digits.
    [GeneratedRegex(@"(?<dir>WorkshopAgent(?:Runs|Tests)[\\/])[0-9A-Fa-f]{32}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex RunDirectoryPattern();

    // Bounded by non-hex rather than \b, as the scan's own rule is: key_<32 hex> is found, a 64-hex SHA-256 is not.
    [GeneratedRegex(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{32}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex Hex32Pattern();
}
