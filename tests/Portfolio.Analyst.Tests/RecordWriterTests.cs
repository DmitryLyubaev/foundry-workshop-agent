using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Portfolio.Analyst.Tests;

/// <summary>
/// <see cref="RecordWriter"/> writes the capstone's shared record (<c>record.schema.json</c>, a copy of
/// the Fabric repo's) for the Foundry route, redacted before it is written.
/// </summary>
public sealed class RecordWriterTests
{
    private const string Short = "1b18407a2310";
    private const string Guid = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string McpUrl = "https://mcp-sentinel.example.invalid/v1/workspaces/ws-sentinel/mcp";
    private const string ConnectionId = "conn-sentinel-capstone";

    private static readonly DateTimeOffset Started = new(2026, 11, 8, 9, 30, 15, 42, TimeSpan.Zero);

    private static AnalystRecord Record(string text = "Project p2 spent A$1,234.56.", JsonNode? raw = null, int pass = 1) => new(
        "q01",
        pass,
        new AnalystAnswer(text, raw ?? new JsonObject { ["items"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) }, ToolCalls: 1, InputTokens: 1_000, OutputTokens: 50),
        WallMs: 2_345,
        Started);

    private static JsonObject Write(AnalystRecord record, params (string Literal, string Placeholder)[] secrets)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        new RecordWriter(text, Short, secrets).Write(record);
        var lines = text.ToString().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("", lines[1]);
        return JsonNode.Parse(lines[0])!.AsObject();
    }

    [Fact]
    public void A_record_matches_the_shared_schema()
    {
        var record = Write(Record());

        SchemaValidator.Validate(record, TestFiles.RecordSchema());
        Assert.Equal("foundry", record["route"]!.GetValue<string>());
        Assert.Equal(Short, record["freeze"]!.GetValue<string>());
        Assert.Equal("q01", record["question_id"]!.GetValue<string>());
        Assert.Equal(1, record["pass"]!.GetValue<int>());
        Assert.Equal("Project p2 spent A$1,234.56.", record["answer_text"]!.GetValue<string>());
        Assert.Equal(1, record["tool_calls"]!.GetValue<int>());
        Assert.Equal(1_000, record["input_tokens"]!.GetValue<long>());
        Assert.Equal(50, record["output_tokens"]!.GetValue<long>());
        Assert.Equal(2_345, record["wall_ms"]!.GetValue<long>());
        Assert.Equal("2026-11-08T09:30:15.042Z", record["started_utc"]!.GetValue<string>());
    }

    [Fact]
    public void The_fields_are_in_the_schemas_order()
    {
        var record = Write(Record());

        Assert.Equal(TestFiles.RecordSchema()["required"]!.AsArray().Select(n => n!.GetValue<string>()), record.Select(p => p.Key));
    }

    [Fact]
    public void Raw_output_is_the_answers_json_as_a_string()
    {
        var record = Write(Record());

        var raw = JsonNode.Parse(record["raw_output"]!.GetValue<string>())!;
        Assert.Equal("Project p2 spent A$1,234.56.", raw["items"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Answer_text_and_raw_output_are_redacted_before_writing()
    {
        var leaky = $"Source https://app.fabric.microsoft.com/groups/abc item {Guid}, ask someone@example.org. Total A$1,234.56 for p2.";
        var raw = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["type"] = "mcp_call", ["arguments"] = new JsonObject { [Guid] = leaky } }) };

        using var text = new StringWriter(CultureInfo.InvariantCulture);
        new RecordWriter(text, Short, []).Write(Record(leaky, raw));
        var line = text.ToString();

        foreach (var secret in new[] { "app.fabric.microsoft.com", Guid, "someone@example.org", "example.org" })
        {
            Assert.DoesNotContain(secret, line, StringComparison.OrdinalIgnoreCase);
        }

        var record = JsonNode.Parse(line)!.AsObject();
        Assert.Equal("Source <url> item <id>, ask <email>. Total A$1,234.56 for p2.", record["answer_text"]!.GetValue<string>());
        var call = JsonNode.Parse(record["raw_output"]!.GetValue<string>())!["items"]![0]!;
        Assert.Equal(record["answer_text"]!.GetValue<string>(), call["arguments"]!["<id>"]!.GetValue<string>());
    }

    [Fact]
    public void The_mcp_url_and_the_connection_id_never_reach_a_record_whatever_their_shape()
    {
        // A connection id has no shape the patterns know: it is replaced as the literal it is.
        var leaky = $"I asked {McpUrl} through {ConnectionId}; p2 spent A$1,234.56.";

        using var text = new StringWriter(CultureInfo.InvariantCulture);
        new RecordWriter(text, Short, [(McpUrl, AnswerRedaction.Url), (ConnectionId, AnswerRedaction.Id)]).Write(Record(leaky));
        var line = text.ToString();

        Assert.DoesNotContain("sentinel", line, StringComparison.Ordinal);
        Assert.Equal("I asked <url> through <id>; p2 spent A$1,234.56.", JsonNode.Parse(line)!["answer_text"]!.GetValue<string>());
    }

    [Fact]
    public void Unknown_token_counts_are_null()
    {
        var record = Write(Record() with { Answer = new AnalystAnswer("x", new JsonObject(), ToolCalls: 0, InputTokens: null, OutputTokens: null) });

        SchemaValidator.Validate(record, TestFiles.RecordSchema());
        Assert.Null(record["input_tokens"]);
        Assert.True(record.ContainsKey("input_tokens"));
    }

    [Theory]
    [InlineData("1B18407A2310")]
    [InlineData("1b18407a231")]
    [InlineData("")]
    public void A_freeze_short_hash_of_the_wrong_shape_is_refused(string freeze)
    {
        Assert.Throws<ArgumentException>(() => new RecordWriter(TextWriter.Null, freeze, []));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void A_pass_outside_1_to_3_is_refused(int pass)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordWriter(TextWriter.Null, Short, []).Write(Record(pass: pass)));
    }

    [Fact]
    public void Non_ascii_text_is_written_as_it_is()
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        new RecordWriter(text, Short, []).Write(Record("Café p2: A$1,234.56 <ok>"));

        Assert.Contains("\"answer_text\":\"Café p2: A$1,234.56 <ok>\"", text.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>
/// The subset of JSON Schema that <c>record.schema.json</c> uses, as the Fabric repo's
/// <c>validate_record</c> reads it: type, required, additionalProperties: false, and per property
/// type, enum, pattern, minimum and maximum.
/// </summary>
internal static class SchemaValidator
{
    public static void Validate(JsonObject record, JsonObject schema)
    {
        foreach (var name in schema["required"]!.AsArray().Select(n => n!.GetValue<string>()))
        {
            Assert.True(record.ContainsKey(name), $"missing field {name}");
        }

        var properties = schema["properties"]!.AsObject();
        if (schema["additionalProperties"] is JsonValue additional && !additional.GetValue<bool>())
        {
            Assert.All(record, p => Assert.True(properties.ContainsKey(p.Key), $"unknown field {p.Key}"));
        }

        foreach (var (name, ruleNode) in properties)
        {
            if (!record.TryGetPropertyValue(name, out var value))
            {
                continue;
            }

            var rule = ruleNode!.AsObject();
            var types = rule["type"] is JsonArray list ? list.Select(t => t!.GetValue<string>()).ToArray() : [rule["type"]!.GetValue<string>()];
            Assert.True(types.Any(t => Is(t, value)), $"{name} must be {string.Join(" or ", types)}");
            if (value is null)
            {
                continue;
            }

            if (rule["enum"] is JsonArray allowed)
            {
                Assert.Contains(value.GetValue<string>(), allowed.Select(a => a!.GetValue<string>()));
            }

            if (rule["pattern"] is JsonValue pattern)
            {
                Assert.Matches(new Regex(pattern.GetValue<string>(), RegexOptions.CultureInvariant), value.GetValue<string>());
            }

            if (rule["minimum"] is JsonValue minimum)
            {
                Assert.True(value.GetValue<long>() >= minimum.GetValue<long>(), $"{name} is below its minimum");
            }

            if (rule["maximum"] is JsonValue maximum)
            {
                Assert.True(value.GetValue<long>() <= maximum.GetValue<long>(), $"{name} is above its maximum");
            }
        }
    }

    private static bool Is(string type, JsonNode? value) => type switch
    {
        "null" => value is null,
        "string" => value?.GetValueKind() == JsonValueKind.String,
        "integer" => value?.GetValueKind() == JsonValueKind.Number && value.GetValue<JsonElement>().TryGetInt64(out _),
        "object" => value is JsonObject,
        _ => false,
    };
}
