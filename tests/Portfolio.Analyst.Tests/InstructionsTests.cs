using System.Text;

namespace Portfolio.Analyst.Tests;

/// <summary>
/// <c>instructions.md</c> tells the agent to refuse as the Fabric repo's scorer recognises a refusal:
/// its phrases start with "not in the data", and a refusal that states a figure or uses a change or
/// comparison word is scored wrong.
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

    [Theory]
    [InlineData("better")]
    [InlineData("worse")]
    [InlineData("rose")]
    [InlineData("fell")]
    [InlineData("higher")]
    [InlineData("lower")]
    [InlineData("increased")]
    [InlineData("decreased")]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("changed")]
    public void A_refusal_uses_no_change_or_comparison_word(string word)
    {
        Assert.Contains("no change or comparison word", RefusalLine, StringComparison.Ordinal);
        Assert.Matches($@"\b{word}\b", RefusalLine);
    }

    [Fact]
    public void The_instructions_are_lf_only_without_a_bom()
    {
        var bytes = TestFiles.Instructions();
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
    }
}
