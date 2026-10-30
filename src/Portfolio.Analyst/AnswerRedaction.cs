using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Workshop.Agent;

namespace Portfolio.Analyst;

/// <summary>
/// Takes URLs, credentials, email addresses and IDs out of an answer before it is written to a
/// record: the rules of the Fabric repo's <c>lakehouse.capstone.report.redact</c>, ported pattern for
/// pattern, so both routes' records are redacted alike and a redacted answer scores as the original
/// would. Figures (<c>A$1,234.56</c>, <c>98.3%</c>), dates, labels (<c>p4</c>, <c>p3-agent</c>) and
/// model names (<c>gpt-5.6-luna</c>) are left alone.
/// </summary>
/// <remarks>
/// <para>In this order, as <c>report.py</c> applies them:</para>
/// <list type="number">
/// <item>URLs with a scheme (<c>abfss://container@account…</c> too, whole), up to the first space,
/// quote, angle bracket, backtick or backslash; punctuation that ends a sentence or closes a bracket
/// is given back.</item>
/// <item>Credentials become <c>&lt;token&gt;</c>: <c>Bearer</c> and a token of 20 or more characters of
/// <c>[\w.~+/-]</c>, with Python's <c>\w</c> (the word stays); a JWT; SAS parameter values, only in an <c>&amp;</c>-joined run
/// that holds <c>sig=</c> (the names stay).</item>
/// <item>Email addresses.</item>
/// <item>Hosts without a scheme: every label holds a letter and a common top-level domain ends it;
/// a top-level domain that is also a word or a namespace (ms, ai, azure, microsoft, windows, app,
/// dev) needs two labels before it.</item>
/// <item>GUIDs, dashed, anywhere; and a run of exactly 32 hex digits.</item>
/// </list>
/// <para>
/// The placeholders are project 3's own (<see cref="Redaction.Url"/>, <see cref="Redaction.Email"/>,
/// <see cref="Redaction.Token"/>), but not its patterns: the shared schema names a GUID <c>&lt;id&gt;</c>
/// rather than <see cref="Redaction.Guid"/>, <see cref="Redaction"/>'s bearer pattern has no length
/// floor (it would take "the bearer of 3 checks"), and its hosts are Azure's only.
/// <see cref="Workshop.Agent.Runner.TranscriptRedaction"/> re-applies <see cref="Redaction"/> to
/// transcripts, which are not answers.
/// </para>
/// </remarks>
public static partial class AnswerRedaction
{
    public const string Url = Redaction.Url;
    public const string Email = Redaction.Email;
    public const string Token = Redaction.Token;
    public const string Id = "<id>";

    /// <summary><paramref name="text"/> with every URL or host, credential, email address and ID replaced by its placeholder.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = UrlPattern().Replace(text, UrlKeepingTail);
        text = Bearer().Replace(text, "${1} " + Token);
        text = Jwt().Replace(text, Token);
        text = Params().Replace(text, SignedSas);
        text = EmailPattern().Replace(text, Email);
        text = Host().Replace(text, UrlKeepingTail);
        text = GuidPattern().Replace(text, Id);
        return Hex32().Replace(text, Id);
    }

    /// <summary>A JSON value with <see cref="Redact"/> applied to every string in it, the keys of objects too; a new tree.</summary>
    public static JsonNode? RedactJson(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var redacted = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    // A later key that redacts to the same text wins, as Python's dict comprehension does.
                    redacted[Redact(key)] = RedactJson(value);
                }

                return redacted;
            case JsonArray array:
                return new JsonArray([.. array.Select(RedactJson)]);
            case JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String:
                return JsonValue.Create(Redact(value.GetValue<string>()));
            default:
                return node.DeepClone();
        }
    }

    /// <summary><c>&lt;url&gt;</c>, with the sentence's or the bracket's punctuation the match took given back.</summary>
    private static string UrlKeepingTail(Match match)
    {
        var tail = UrlTail().Match(match.Value);
        return tail.Success ? Url + tail.Value : Url;
    }

    /// <summary>The SAS values in a run of <c>&amp;</c>-joined parameters, if the run is signed (holds <c>sig=</c>).</summary>
    private static string SignedSas(Match match) =>
        Signed().IsMatch(match.Value) ? Sas().Replace(match.Value, "${1}${2}=" + Token) : match.Value;

    // Python's \w, \b and \s, not .NET's: the shared vector (tests/vectors/redaction-vector.json)
    // pins them. Python's \w is a letter, any number (Nd, Nl, No: "²" too) or "_"; .NET's also takes
    // combining marks (Mn) and every connector (Pc), and its \b counts ZWJ and ZWNJ as word
    // characters. Python's \s also takes the separators \x1C-\x1F. So: the classes below, and \b as
    // a lookaround (every \b here stands next to a word character, so one side is enough).
    private const string WordChars = @"\p{L}\p{N}_";
    private const string NotWordBefore = "(?<![" + WordChars + "])";
    private const string NotWordAfter = "(?![" + WordChars + "])";
    private const string SpaceChars = @"\s\x1C-\x1F";
    private const string Rest = "[^" + SpaceChars + @"""'<>\\`]";
    private const string SafeTlds = "com|net|org|io|cloud|invalid|local|internal|au|uk|us|eu";
    private const string RiskyTlds = "ms|ai|azure|microsoft|windows|app|dev";
    private const string Label = @"(?=[A-Za-z0-9-]*[A-Za-z])[A-Za-z0-9-]+\.";

    [GeneratedRegex(NotWordBefore + "(?:https?|wss?|ftp|abfss?)://" + Rest + "+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[.,;:!?)\]}]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UrlTail();

    [GeneratedRegex(NotWordBefore + "(bearer)[" + SpaceChars + "]+[" + WordChars + ".~+/-]{20,}=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();

    [GeneratedRegex(NotWordBefore + "eyJ[" + WordChars + "-]+\\.[" + WordChars + "-]+\\.[" + WordChars + "-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex("[" + WordChars + ".-]+=[^&" + SpaceChars + "\"]*(?:&[" + WordChars + ".-]+=[^&" + SpaceChars + "\"]*)*", RegexOptions.CultureInvariant)]
    private static partial Regex Params();

    [GeneratedRegex(@"(?:^|&)sig=", RegexOptions.CultureInvariant)]
    private static partial Regex Signed();

    [GeneratedRegex("(^|&)(sig|skoid|sktid|se|sv|st|sp|spr|srt|ss)=[^&" + SpaceChars + "\"]*", RegexOptions.CultureInvariant)]
    private static partial Regex Sas();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}" + NotWordAfter, RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(
        "(?<![" + WordChars + "@.-])(?:(?:" + Label + ")+(?:" + SafeTlds + ")|" + Label + "(?:" + Label + ")+(?:" + RiskyTlds + "))"
            + NotWordAfter + "(?![.-][" + WordChars + "])(?:[/:?#]" + Rest + "*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Host();

    [GeneratedRegex(@"[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", RegexOptions.CultureInvariant)]
    private static partial Regex Hex32();
}
