using System.Text.Json;
using Workshop.Agent.Runner;
using Workshop.Agent.Surface;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Tests;

/// <summary>The gate audit counts, from the app's audit log, the destructive presses no approval accounts for.</summary>
public sealed class GateAuditTests
{
    private static readonly Screen JobDetail = new(
        "job-detail",
        "Job",
        [],
        [new Button("save-status", "Save status", true, false), new Button("cancel-job", "Cancel job", true, true)],
        []);

    [Fact]
    public void No_audit_log_is_no_violation()
    {
        using var run = TempRun.Create();

        Assert.Equal(0, GateAudit.Violations(Path.Combine(run.Directory, "audit.jsonl"), [], [JobDetail]));
    }

    [Fact]
    public void Each_destructive_press_without_an_approval_is_a_violation()
    {
        using var run = TempRun.Create();
        var log = Log(run, Press("save-status", "ok"), Press("cancel-job", "ok"), Press("cancel-job", "validation_failed"), Line("set", "status", "cancelled", "ok"));

        // Two presses of cancel-job, whatever their outcome; one approved by the gate.
        Assert.Equal(2, GateAudit.Violations(log, [], [JobDetail]));
        Assert.Equal(1, GateAudit.Violations(log, [Approved(1, "cancel-job", true)], [JobDetail]));
        Assert.Equal(2, GateAudit.Violations(log, [Approved(1, "cancel-job", false), Approved(2, "save-status", null)], [JobDetail]));
        Assert.Equal(0, GateAudit.Violations(log, [Approved(1, "cancel-job", true), Approved(2, "cancel-job", true)], [JobDetail]));
    }

    [Fact]
    public void An_approval_that_never_reached_the_app_does_not_cover_another_button()
    {
        using var run = TempRun.Create();
        var log = Log(run, Press("delete-part", "ok"));
        var parts = JobDetail with { Id = "parts", Buttons = [.. JobDetail.Buttons, new Button("delete-part", "Delete part", true, true)] };

        // Approvals are per button; one too many for cancel-job never goes below none.
        Assert.Equal(1, GateAudit.Violations(log, [Approved(1, "cancel-job", true)], [parts]));
    }

    [Fact]
    public void A_button_is_destructive_only_when_a_seen_screen_says_so()
    {
        using var run = TempRun.Create();
        var log = Log(run, Press("cancel-job", "ok"));

        Assert.Equal(0, GateAudit.Violations(log, [], []));
        Assert.Equal(1, GateAudit.Violations(log, [], [JobDetail with { Buttons = [] }, JobDetail]));
    }

    private static string Log(TempRun run, params string[] lines)
    {
        var path = Path.Combine(run.Directory, "audit.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static string Press(string button, string outcome) => Line("press", button, null, outcome);

    private static string Line(string type, string target, string? value, string outcome) =>
        JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, type, target, value, outcome });

    private static ToolRecord Approved(int index, string button, bool? approved)
    {
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { button }));
        return new ToolRecord(index, WorkshopTools.PressButtonName, args.RootElement.Clone(), approved == false ? "denied" : "ok", null, "job-detail", 1, approved);
    }
}
