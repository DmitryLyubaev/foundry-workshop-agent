using System.Text;
using System.Text.Json.Nodes;

namespace Portfolio.Analyst.Tests;

/// <summary>
/// <see cref="AnalystFreeze.Verify"/>: the analyst recomputes the questions, its instructions and its
/// model's hashes against <c>freeze.json</c>, and refuses, naming the first that differs (Review Focus 3).
/// </summary>
public sealed class AnalystFreezeTests
{
    private static readonly JsonObject Vector = TestFiles.Vector();
    private static readonly JsonObject Inputs = Vector["inputs"]!.AsObject();

    private static byte[] Questions => TestFiles.Utf8(Inputs["questions"]);

    private static byte[] Instructions => TestFiles.Utf8(Inputs["analyst_instructions"]);

    private static string Model => Inputs["model_foundry"]!.GetValue<string>();

    /// <summary>The vector's expected freeze, as <c>capstone_freeze.py freeze</c> writes <c>freeze.json</c>.</summary>
    private static JsonObject FreezeObject() => Vector["expected"]!.DeepClone().AsObject();

    private static string FreezeJson(Action<JsonObject>? edit = null)
    {
        var freeze = FreezeObject();
        edit?.Invoke(freeze);
        return freeze.ToJsonString();
    }

    [Fact]
    public void The_vectors_freeze_verifies_and_gives_its_short_hash()
    {
        Assert.Equal("1b18407a2310", AnalystFreeze.Verify(FreezeJson(), Questions, Instructions, Model));
    }

    [Fact]
    public void Line_endings_alone_do_not_break_it()
    {
        // The questions with CRLF turned to LF, and the instructions with lone CRs: the same after normalisation.
        var questions = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Questions).Replace("\r\n", "\n", StringComparison.Ordinal));
        var instructions = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Instructions).Replace("\r\n", "\r", StringComparison.Ordinal));

        Assert.Equal("1b18407a2310", AnalystFreeze.Verify(FreezeJson(), questions, instructions, Model));
    }

    public static TheoryData<string> Verified => new(AnalystFreeze.Verified);

    [Theory]
    [MemberData(nameof(Verified))]
    public void Each_verified_component_that_drifted_is_named(string component)
    {
        var questions = component == "questions" ? [.. Questions, (byte)' '] : Questions;
        var instructions = component == "analyst_instructions" ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Instructions) + "Be brief.\n") : Instructions;
        var model = component == "model_foundry" ? Model + "-2" : Model;

        var e = Assert.Throws<FreezeBrokenException>(() => AnalystFreeze.Verify(FreezeJson(), questions, instructions, model));

        Assert.Equal(component, e.Component);
        Assert.Equal($"the freeze is broken: {component} changed", e.Message);
    }

    [Fact]
    public void A_bom_added_to_the_instructions_breaks_it()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Instructions];

        Assert.Equal("analyst_instructions", Assert.Throws<FreezeBrokenException>(() => AnalystFreeze.Verify(FreezeJson(), Questions, withBom, Model)).Component);
    }

    [Fact]
    public void The_first_mismatch_in_component_order_is_named()
    {
        var e = Assert.Throws<FreezeBrokenException>(() => AnalystFreeze.Verify(FreezeJson(), [.. Questions, (byte)' '], [.. Instructions, (byte)' '], Model + "x"));

        Assert.Equal("questions", e.Component);
    }

    [Theory]
    [InlineData("expected")]
    [InlineData("rules")]
    [InlineData("snapshot_time")]
    [InlineData("data_agent")]
    [InlineData("model_direct")]
    public void A_component_the_analyst_cannot_recompute_must_still_be_there(string component)
    {
        var json = FreezeJson(f => f["components"]!.AsObject().Remove(component));

        Assert.Equal(component, Assert.Throws<FreezeBrokenException>(() => AnalystFreeze.Verify(json, Questions, Instructions, Model)).Component);
    }

    [Fact]
    public void An_edited_component_the_analyst_cannot_recompute_breaks_the_overall_hash()
    {
        var json = FreezeJson(f => f["components"]!["expected"] = new string('0', 64));

        Assert.Equal("hash", Assert.Throws<FreezeBrokenException>(() => AnalystFreeze.Verify(json, Questions, Instructions, Model)).Component);
    }

    [Fact]
    public void An_unknown_component_is_named()
    {
        var json = FreezeJson(f => f["components"]!["extra"] = new string('0', 64));

        Assert.Equal("extra", Assert.Throws<FreezeBrokenException>(() => AnalystFreeze.Verify(json, Questions, Instructions, Model)).Component);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    public void An_edited_hash_or_short_hash_is_the_hash(string field)
    {
        var json = FreezeJson(f => f[field] = field == "short" ? "000000000000" : new string('0', 64));

        Assert.Equal("hash", Assert.Throws<FreezeBrokenException>(() => AnalystFreeze.Verify(json, Questions, Instructions, Model)).Component);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"components\": [], \"hash\": \"x\", \"short\": \"y\"}")]
    [InlineData("{\"components\": {\"questions\": 1}, \"hash\": \"x\", \"short\": \"y\"}")]
    public void A_freeze_file_that_is_not_a_freeze_is_an_input_error(string json)
    {
        Assert.Throws<FreezeFormatException>(() => AnalystFreeze.Verify(json, Questions, Instructions, Model));
    }
}
