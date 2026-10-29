using System.Text.Json.Nodes;

namespace Portfolio.Analyst.Tests;

/// <summary>
/// <see cref="AnswerRedaction"/> redacts exactly as the Fabric repo's <c>lakehouse.capstone.report.redact</c>
/// does: these cases are that repo's (tests/test_ask_data_agent.py), so both routes' records read alike
/// and a redacted answer scores as the original would.
/// </summary>
public sealed class AnswerRedactionTests
{
    private const string Guid = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string Hex32 = "0f8fad5bd9cb469fa16570867728950e";
    private const string Email = "someone.else@example.org";
    private const string Link = "https://app.fabric.microsoft.com/groups/abc/reports?x=1";

    // Built at run time so this file holds no token-shaped literal.
    private static readonly string Jwt = "eyJ" + "hbGciOiJSUzI1NiJ9" + ".eyJ" + "zdWIiOiJ4In0" + ".c2lnbmF0dXJl-_x";

    [Theory]
    [InlineData("Project p2 spent A$1,234.56 (AUD 1 234.56) in October 2026.")]
    [InlineData("The p3-agent study's gpt-5.6-luna success rate was 98.3%, or 0.983, 59 of 60.")]
    [InlineData("p2-retrieval arm E3: top-1 rate 0.75; text-embedding-3-large had 1.2k results.")]
    [InlineData("From 2026-10-06 to 2026-10-09 project p4 cost 0.0047 USD; untagged and releaselens-shared too.")]
    [InlineData("Projects p1, p4 & p7 had failures; -A$3.40 refund; 10/25/2026 14:05; 5-7 days; 1.5x.")]
    [InlineData("fabric@ ran it; version 2 vs v1.2.3.")]
    [InlineData("A$1,234.56 and 98.3% for p4 with gpt-5.6-luna and gpt-4.1-2025-04-14.")]
    [InlineData("the bearer of 3 checks; Bearer short-token-123 failed")]
    [InlineData("sp=12 failed and st=3 passed; sv=1&sp=r&se=2 without a signature")]
    [InlineData("a 33-hex run " + Hex32 + "a or 31 0f8fad5bd9cb469fa16570867728950 is not an ID")]
    [InlineData("e.g. the p7 checks: i.e. 3 failed. Rate 0.995 vs .75; ratio 3.2.1-beta; file report.md.")]
    [InlineData("claude-haiku-4-5 and gpt-5.6-luna, model names, stay.")]
    public void Figures_labels_and_model_names_are_left_alone(string text)
    {
        Assert.Equal(text, AnswerRedaction.Redact(text));
    }

    public static TheoryData<string, string> Credentials => new()
    {
        { $"token {Jwt} used", "token <token> used" },
        { $"header Authorization: Bearer {Jwt}", "header Authorization: Bearer <token>" },
        { "auth bearer abc.def-123_456~789+0/xyz= failed", "auth bearer <token> failed" },
        { "query ?sv=2024-01-01&se=2026-11-10&sp=r&sig=AbC%2Bx%3D done", "query ?sv=<token>&se=<token>&sp=<token>&sig=<token> done" },
        { "skoid=x1&sktid=y2&st=2026&spr=https&srt=sco&ss=b&sig=abc", "skoid=<token>&sktid=<token>&st=<token>&spr=<token>&srt=<token>&ss=<token>&sig=<token>" },
        { $"item {Hex32}.", "item <id>." },
        { $"item {Hex32}: 12 checks, A$3.40", "item <id>: 12 checks, A$3.40" },
        { $"ws_{Hex32}_x", "ws_<id>_x" },
        { $"id={Hex32}", "id=<id>" },
        { "read 9lake.dfs.core.windows.net/x/y now", "read <url> now" },
        { $"read abfss://gold@acct.dfs.core.windows.net/Tables/{Guid} ok", "read <url> ok" },
        { "abfs://c@a.dfs.core.windows.net/x", "<url>" },
    };

    [Theory]
    [MemberData(nameof(Credentials))]
    public void Tokens_and_other_id_shapes_are_redacted(string text, string redacted)
    {
        Assert.Equal(redacted, AnswerRedaction.Redact(text));
    }

    [Theory]
    [InlineData("Microsoft.Azure")]
    [InlineData("p2.dev")]
    [InlineData("3.ms")]
    [InlineData("1.5.us")]
    [InlineData("use Microsoft.ai")]
    [InlineData("Fabric.AI and p3.app")]
    [InlineData("gpt-4.1-2025-04-14")]
    public void The_host_pattern_leaves_dotted_words_alone(string text)
    {
        Assert.Equal(text, AnswerRedaction.Redact(text));
    }

    [Theory]
    [InlineData("api.fabric.contoso.dev/v1")]
    [InlineData("x.y.ms")]
    [InlineData("model.studio.ai")]
    [InlineData("my.service.azure")]
    public void A_risky_tld_after_two_labels_is_a_host(string text)
    {
        Assert.Equal("<url>", AnswerRedaction.Redact(text));
    }

    [Fact]
    public void Urls_guids_and_emails_have_their_placeholders()
    {
        Assert.Equal("see <url>.", AnswerRedaction.Redact($"see {Link}."));
        Assert.Equal("(<url>)", AnswerRedaction.Redact($"({Link})"));
        Assert.Equal("<id>", AnswerRedaction.Redact(Guid.ToUpperInvariant()));
        Assert.Equal("{<id>}", AnswerRedaction.Redact($"{{{Guid}}}"));
        Assert.Equal("mail <email>, now", AnswerRedaction.Redact($"mail {Email}, now"));
        Assert.Equal("<url>", AnswerRedaction.Redact($"https://x.example/a/{Guid}?u={Email}"));
        Assert.Equal("<email> <id> <url>", AnswerRedaction.Redact($"{Email} {Guid} {Link}"));
        Assert.Equal("item-<id>", AnswerRedaction.Redact($"item-{Guid}"));
        Assert.Equal("ws<id>x", AnswerRedaction.Redact($"ws{Guid}x"));
        Assert.Equal("POST <url> failed.", AnswerRedaction.Redact("POST api.fabric.example.com/v1/mcp/workspaces/w/agent failed."));
        Assert.Equal("host <url>, then", AnswerRedaction.Redact("host leak.example.org, then"));
    }

    [Fact]
    public void The_leaky_answer_of_the_direct_routes_test_reads_the_same()
    {
        var leaky = $"Source {Link} item {Guid}, ask {Email}. Total A$1,234.56 for p2.";

        Assert.Equal("Source <url> item <id>, ask <email>. Total A$1,234.56 for p2.", AnswerRedaction.Redact(leaky));
    }

    [Fact]
    public void Every_string_in_a_json_value_is_redacted_keys_too()
    {
        var node = JsonNode.Parse($$"""{"{{Guid}}": ["{{Link}}", 3, {"mail": "{{Email}}", "n": null, "ok": true}]}""");

        var redacted = AnswerRedaction.RedactJson(node);

        Assert.Equal("""{"<id>":["<url>",3,{"mail":"<email>","n":null,"ok":true}]}""", redacted!.ToJsonString(RecordWriter.Json));
    }

    [Fact]
    public void The_placeholders_are_the_shared_ones()
    {
        Assert.Equal(Workshop.Agent.Redaction.Url, AnswerRedaction.Url);
        Assert.Equal(Workshop.Agent.Redaction.Email, AnswerRedaction.Email);
        Assert.Equal(Workshop.Agent.Redaction.Token, AnswerRedaction.Token);
        Assert.Equal("<id>", AnswerRedaction.Id);
    }
}
