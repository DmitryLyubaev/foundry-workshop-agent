using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Portfolio.Analyst;

/// <summary>One question asked once: what <see cref="RecordWriter"/> writes as a record.</summary>
public sealed record AnalystRecord(string QuestionId, int Pass, AnalystAnswer Answer, long WallMs, DateTimeOffset StartedUtc);

/// <summary>
/// Writes the capstone's shared record (the Fabric repo's <c>capstone/record.schema.json</c>), one
/// JSON object per line, for the Foundry route:
/// <c>{"route","freeze","question_id","pass","answer_text","raw_output","tool_calls","input_tokens","output_tokens","wall_ms","started_utc"}</c>.
/// <c>answer_text</c> and every string in <c>raw_output</c> (keys too) are redacted first: the given
/// secrets (the MCP URL and the connection ID, which may have no shape a pattern knows) as literals,
/// then <see cref="AnswerRedaction"/>. Each record is flushed as it is written.
/// </summary>
public sealed partial class RecordWriter
{
    public const string Route = "foundry";

    /// <summary>Compact, with non-ASCII text and the placeholders' angle brackets written as they are, as Python's <c>json.dumps(ensure_ascii=False)</c> does.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly TextWriter writer;
    private readonly string freeze;
    private readonly (string Literal, string Placeholder)[] secrets;

    /// <param name="writer">Where the lines go.</param>
    /// <param name="freeze">The freeze's short hash: 12 lowercase hex characters.</param>
    /// <param name="secrets">Values replaced wherever they appear, longest first, before the patterns run.</param>
    public RecordWriter(TextWriter writer, string freeze, IEnumerable<(string Literal, string Placeholder)> secrets)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(secrets);
        if (freeze is null || !ShortHash().IsMatch(freeze))
        {
            throw new ArgumentException("The freeze's short hash is 12 lowercase hex characters.", nameof(freeze));
        }

        this.writer = writer;
        this.freeze = freeze;
        this.secrets = [.. secrets.Where(s => !string.IsNullOrEmpty(s.Literal)).OrderByDescending(s => s.Literal.Length)];
    }

    /// <summary>Writes <paramref name="record"/> as one line and flushes it.</summary>
    public void Write(AnalystRecord record)
    {
        writer.Write(Format(record));
        writer.Write('\n');
        writer.Flush();
    }

    /// <summary>The record's line, without its line break.</summary>
    public string Format(AnalystRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfLessThan(record.Pass, 1, nameof(record));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(record.Pass, 3, nameof(record));
        ArgumentOutOfRangeException.ThrowIfNegative(record.WallMs, nameof(record));
        var answer = record.Answer;
        var line = new JsonObject
        {
            ["route"] = Route,
            ["freeze"] = freeze,
            ["question_id"] = record.QuestionId,
            ["pass"] = record.Pass,
            ["answer_text"] = Redact(answer.Text),
            ["raw_output"] = AnswerRedaction.RedactJson(Scrub(answer.RawOutput))?.ToJsonString(Json) ?? "null",
            ["tool_calls"] = answer.ToolCalls,
            ["input_tokens"] = answer.InputTokens,
            ["output_tokens"] = answer.OutputTokens,
            ["wall_ms"] = record.WallMs,
            ["started_utc"] = record.StartedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };
        return line.ToJsonString(Json);
    }

    private string Redact(string text) => AnswerRedaction.Redact(ScrubText(text));

    private string ScrubText(string text)
    {
        foreach (var (literal, placeholder) in secrets)
        {
            text = text.Replace(literal, placeholder, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    /// <summary>The secrets taken out of every string of a JSON value, keys too; the patterns follow in <see cref="AnswerRedaction.RedactJson"/>.</summary>
    private JsonNode? Scrub(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => ScrubObject(obj),
        JsonArray array => new JsonArray([.. array.Select(Scrub)]),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => JsonValue.Create(ScrubText(value.GetValue<string>())),
        _ => node.DeepClone(),
    };

    private JsonObject ScrubObject(JsonObject obj)
    {
        var scrubbed = new JsonObject();
        foreach (var (key, value) in obj)
        {
            // A later key that scrubs to the same text wins, as in AnswerRedaction.RedactJson.
            scrubbed[ScrubText(key)] = Scrub(value);
        }

        return scrubbed;
    }

    [GeneratedRegex(@"^[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShortHash();
}
