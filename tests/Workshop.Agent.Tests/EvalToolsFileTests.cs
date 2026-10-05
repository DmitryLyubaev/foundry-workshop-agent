using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests;

/// <summary>
/// The evaluation package's copy of the six tools: the Python side gives the evaluators the tools'
/// definitions from <c>eval/fwa_eval/tools.json</c>, so the file must be exactly the canonical JSON
/// the freeze hashes, or the judge would score the transcripts against tools the model never had.
/// </summary>
public sealed class EvalToolsFileTests
{
    [Fact]
    public void The_eval_package_holds_the_tools_exactly_as_the_freeze_hashes_them()
    {
        var path = Path.Combine(RepoPaths.RepoRoot, "eval", "fwa_eval", "tools.json");

        // One line, LF-terminated: .gitattributes keeps it LF on every checkout.
        Assert.Equal(ToolSchemas.CanonicalJson(WorkshopTools.Declarations) + "\n", File.ReadAllText(path));
    }
}
