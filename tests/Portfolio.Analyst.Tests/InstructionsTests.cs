using System.Text;
using System.Text.Json.Nodes;

namespace Portfolio.Analyst.Tests;

/// <summary>
/// <c>instructions.md</c> tells the agent to refuse as the Fabric repo's scorer recognises a refusal:
/// its phrases start with "not in the data", and a refusal that states a figure or uses one of the
/// scorer's change words (<c>CHANGE_WORDS</c>, copied as <c>tests/vectors/change-words.json</c>) is
/// scored wrong.
/// </summary>
public sealed class InstructionsTests
{
    private static readonly string Text = Encoding.UTF8.GetString(TestFiles.Instructions());

    private static string RefusalLine =>
        Assert.Single(Text.Split('\n'), l => l.Contains("cannot answer", StringComparison.Ordinal));

    [Fact]
    public void A_refusal_begins_with_not_in_the_data_and_says_why()
    {
        Assert.Contains("\"Not in the data:\"", RefusalLine, StringComparison.Ordinal);
        Assert.Contains("say briefly why", RefusalLine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_gives_no_figure()
    {
        Assert.Contains("Give no figure at all", RefusalLine, StringComparison.Ordinal);
    }

    private const string WordListLead = "Use none of these change or comparison words in it either: ";

    /// <summary>The Fabric scorer's change words, in its order, from the copied <c>change-words.json</c>.</summary>
    private static string[] ScorerChangeWords =>
        JsonNode.Parse(TestFiles.ChangeWords())!.AsArray().Select(w => w!.GetValue<string>()).ToArray();

    [Fact]
    public void A_refusal_lists_exactly_the_scorers_change_words()
    {
        var line = RefusalLine;
        var start = line.IndexOf(WordListLead, StringComparison.Ordinal);
        Assert.True(start >= 0, "The refusal line has no change-word list.");
        Assert.EndsWith(".", line, StringComparison.Ordinal);
        var listed = line[(start + WordListLead.Length)..^1].Split(", ");
        Assert.Equal(ScorerChangeWords, listed);
    }

    [Fact]
    public void The_change_words_copy_is_the_scorers_list_as_the_fabric_repo_writes_it()
    {
        // The Fabric repo writes json.dumps(list(CHANGE_WORDS), indent=2) and a newline: LF, no BOM.
        var bytes = TestFiles.ChangeWords();
        var words = ScorerChangeWords;
        Assert.NotEmpty(words);
        Assert.All(words, w => Assert.Matches("^[a-z]+$", w));
        Assert.Equal(words.Length, words.Distinct(StringComparer.Ordinal).Count());
        var expected = "[\n" + string.Join(",\n", words.Select(w => $"  \"{w}\"")) + "\n]\n";
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
    }

    [Fact]
    public void The_instructions_are_lf_only_without_a_bom()
    {
        var bytes = TestFiles.Instructions();
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
    }
}
