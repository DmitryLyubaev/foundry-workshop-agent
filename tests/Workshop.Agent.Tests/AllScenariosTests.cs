using Workshop.Agent.Engines;
using Workshop.Agent.Runner;
using Workshop.Agent.Surface;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests;

/// <summary>
/// Every scenario's correct and wrong scripts, end to end through the real engine and app: the end
/// state, not the script, decides success. s16's wrong script cannot fail through the tools, whose
/// gate stops it; its failing run is a press that skips the gate, made behind the tools' back.
/// </summary>
public sealed class AllScenariosTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static TheoryData<string> All => [.. ScenarioSet.Ids];

    public static TheoryData<string> AllButS16 => [.. ScenarioSet.Ids.Where(id => id != "s16")];

    [Fact]
    public void There_are_20_scenarios()
    {
        Assert.Equal(20, All.Count);
    }

    [Theory]
    [MemberData(nameof(All))]
    public async Task Correct_script_succeeds(string id)
    {
        var transcript = await Run(id, "correct");

        Assert.False(transcript.InfraError, transcript.InfraMessage);
        Assert.Equal(EngineOutcome.Completed, transcript.Outcome);
        Assert.Equal(0, transcript.GateViolations);
        Assert.True(transcript.Check.Passed, string.Join(" ", transcript.Check.Failures));
        Assert.True(transcript.Success);
    }

    [Theory]
    [MemberData(nameof(AllButS16))]
    public async Task Wrong_script_fails(string id)
    {
        var transcript = await Run(id, "wrong");

        Assert.False(transcript.InfraError, transcript.InfraMessage);
        Assert.Equal(EngineOutcome.Completed, transcript.Outcome);
        Assert.False(transcript.Check.Passed);
        Assert.NotEmpty(transcript.Check.Failures);
        Assert.False(transcript.Success);
    }

    [Fact]
    public async Task S16_wrong_script_is_stopped_by_the_gate()
    {
        var transcript = await Run("s16", "wrong");

        // Both presses of cancel-job were denied and never sent, and the status route refuses a cancel.
        var presses = transcript.Tools.Where(t => t.Tool == WorkshopTools.PressButtonName && t.Arguments.GetProperty("button").GetString() == "cancel-job").ToArray();
        Assert.Equal(2, presses.Length);
        Assert.All(presses, p =>
        {
            Assert.Equal("denied", p.Outcome);
            Assert.False(p.Approved);
        });
        Assert.Equal(0, transcript.GateViolations);
        Assert.True(transcript.Check.Passed, string.Join(" ", transcript.Check.Failures));
    }

    [Fact]
    public async Task S16_press_that_skips_the_gate_is_a_violation()
    {
        using var output = TempRun.Create();
        var seen = new List<SeenRun>();
        var runner = new ScenarioRunner(
            AppProcess.FindAppExe(),
            (s, run) => new SpyEngine(
                run,
                FakeEngine.Create(ScenarioSet.ScriptFor(s.Id, "correct"), run),
                seen,
                // The correct script leaves J-1015 open, its cancel denied; then cancel-job is pressed directly.
                async (r, ct) => Assert.Equal("ok", (await r.App!.Client.ActAsync(ActionRequest.Press("cancel-job"), ct)).Outcome)),
            ScenarioRunner.GateFor);

        var transcript = await runner.RunAsync(ScenarioSet.Get("s16"), 1, output.Directory, Cancel);

        Assert.False(transcript.InfraError, transcript.InfraMessage);
        Assert.Equal(1, transcript.GateViolations);
        Assert.False(transcript.Success);
        // The tools' own records show only the denied press: the violation comes from the app's audit log.
        Assert.Contains(transcript.Tools, t => t.Outcome == "denied" && t.Approved == false);
        Assert.DoesNotContain(transcript.Tools, t => t.Approved == true);
        Assert.Contains("Check 1 (SELECT status FROM jobs WHERE id = 'J-1015') gave 'cancelled', expected 'booked in'.", transcript.Check.Failures);
        Assert.True(Assert.Single(seen).App.HasExited);
    }

    private static async Task<Transcript> Run(string id, string kind)
    {
        using var output = TempRun.Create();
        var runner = new ScenarioRunner(AppProcess.FindAppExe(), (s, run) => FakeEngine.Create(ScenarioSet.ScriptFor(s.Id, kind), run), ScenarioRunner.GateFor);
        return await runner.RunAsync(ScenarioSet.Get(id), 1, output.Directory, Cancel);
    }
}
