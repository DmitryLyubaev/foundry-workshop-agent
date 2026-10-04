using System.Text.Json;
using Workshop.Agent.Surface;
using Server = Workshop.Surface;

namespace Workshop.Agent.Tests;

/// <summary>
/// The client's records against the server's: what <c>Workshop.Surface</c> writes with
/// <see cref="Server.SurfaceJson.Options"/> reads into the client's records with nothing lost, and
/// the client's actions read back as the server's.
/// </summary>
public sealed class ContractTests
{
    private static readonly Server.ScreenDescription JobDetail = new(
        "job-detail",
        "Job",
        [
            new Server.FieldDescription("job", "Job", Server.FieldKinds.Text, "J-1008: Inkwell 300 (printer) for Henderson Family", null, false, false, null),
            new Server.FieldDescription("status", "Status", Server.FieldKinds.Choice, "diagnosing", ["booked in", "diagnosing", "cancelled"], true, true, null),
            new Server.FieldDescription("quantity", "Quantity", Server.FieldKinds.Number, "1", null, true, false, null),
            new Server.FieldDescription("note", "Note", Server.FieldKinds.Text, null, null, true, false, 1000),
        ],
        [
            new Server.ButtonDescription("save-status", "Save status", true, false),
            new Server.ButtonDescription("cancel-job", "Cancel job", false, true),
        ],
        [
            new Server.ListDescription(
                "parts",
                "Parts",
                ["Part", "Name", "Quantity", "State"],
                [
                    new Server.RowDescription("P-04#1", ["P-04", "Ink cartridge", "1", "fitted"], true),
                    new Server.RowDescription("P-02#1", ["P-02", "Phone screen assembly", "2", "on order"], false),
                ]),
            new Server.ListDescription("notes", "Notes", ["At (UTC)", "Note"], []),
        ]);

    [Fact]
    public void Screen_description_round_trips_into_screen()
    {
        var json = JsonSerializer.Serialize(JobDetail, Server.SurfaceJson.Options);

        var screen = JsonSerializer.Deserialize<Screen>(json, ContractJson.Options)!;

        AssertSame(JobDetail, screen);
        Assert.Equal(json, JsonSerializer.Serialize(screen, ContractJson.Options));
    }

    [Theory]
    [InlineData(Server.Outcomes.Ok, null)]
    [InlineData(Server.Outcomes.Ok, "Added on order: not enough in stock.")]
    [InlineData(Server.Outcomes.ValidationFailed, "Job J-1006 still has parts on order.")]
    [InlineData(Server.Outcomes.NotFound, "There is no button 'x' on this screen.")]
    [InlineData(Server.Outcomes.Disabled, "Cancel job is disabled.")]
    public void Action_result_round_trips_into_action_reply(string outcome, string? message)
    {
        var result = new Server.ActionResult(outcome, message, JobDetail);
        var json = JsonSerializer.Serialize(result, Server.SurfaceJson.Options);

        var reply = JsonSerializer.Deserialize<ActionReply>(json, ContractJson.Options)!;

        Assert.Equal(outcome, reply.Outcome);
        Assert.Equal(message, reply.Message);
        AssertSame(JobDetail, reply.Screen);
        Assert.Equal(json, JsonSerializer.Serialize(reply, ContractJson.Options));
    }

    [Fact]
    public void Screen_list_reads_into_screen_info()
    {
        // The server's screen entry is private to the endpoint; this is the README's shape for GET /screens.
        const string json = """[{"id":"job-list","title":"Jobs"},{"id":"new-job","title":"New job"}]""";

        var screens = JsonSerializer.Deserialize<ScreenInfo[]>(json, ContractJson.Options)!;

        Assert.Equal([new ScreenInfo("job-list", "Jobs"), new ScreenInfo("new-job", "New job")], screens);
    }

    [Fact]
    public void Each_action_request_reads_as_the_servers_action_and_sends_only_its_targets()
    {
        (ActionRequest Request, Server.SurfaceAction Expected, string Json)[] cases =
        [
            (ActionRequest.Open("new-job"), new("open", "new-job", null, null, null, null, null), """{"type":"open","screen":"new-job"}"""),
            (ActionRequest.Set("fault", "Fan is very loud"), new("set", null, "fault", "Fan is very loud", null, null, null), """{"type":"set","field":"fault","value":"Fan is very loud"}"""),
            (ActionRequest.Select("jobs", "J-1008"), new("select", null, null, null, "jobs", "J-1008", null), """{"type":"select","list":"jobs","row":"J-1008"}"""),
            (ActionRequest.Press("cancel-job"), new("press", null, null, null, null, null, "cancel-job"), """{"type":"press","button":"cancel-job"}"""),
        ];

        foreach (var (request, expected, expectedJson) in cases)
        {
            var json = JsonSerializer.Serialize(request, ContractJson.Options);

            Assert.Equal(expectedJson, json);
            Assert.Equal(expected, JsonSerializer.Deserialize<Server.SurfaceAction>(json, Server.SurfaceJson.Options));
        }
    }

    [Fact]
    public void Action_request_carries_every_target()
    {
        var request = new ActionRequest("press", "s", "f", "v", "l", "r", "b");
        var json = JsonSerializer.Serialize(request, ContractJson.Options);

        Assert.Equal(
            new Server.SurfaceAction("press", "s", "f", "v", "l", "r", "b"),
            JsonSerializer.Deserialize<Server.SurfaceAction>(json, Server.SurfaceJson.Options));
    }

    private static void AssertSame(Server.ScreenDescription expected, Screen actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Title, actual.Title);

        Assert.Equal(expected.Fields.Count, actual.Fields.Length);
        foreach (var (e, a) in expected.Fields.Zip(actual.Fields))
        {
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Label, a.Label);
            Assert.Equal(e.Kind, a.Kind);
            Assert.Equal(e.Value, a.Value);
            Assert.Equal(e.Options, a.Options);
            Assert.Equal(e.Enabled, a.Enabled);
            Assert.Equal(e.Required, a.Required);
            Assert.Equal(e.MaxLength, a.MaxLength);
        }

        Assert.Equal(expected.Buttons.Count, actual.Buttons.Length);
        foreach (var (e, a) in expected.Buttons.Zip(actual.Buttons))
        {
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Label, a.Label);
            Assert.Equal(e.Enabled, a.Enabled);
            Assert.Equal(e.Destructive, a.Destructive);
        }

        Assert.Equal(expected.Lists.Count, actual.Lists.Length);
        foreach (var (e, a) in expected.Lists.Zip(actual.Lists))
        {
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Label, a.Label);
            Assert.Equal(e.Columns, a.Columns);
            Assert.Equal(e.Rows.Count, a.Rows.Length);
            foreach (var (er, ar) in e.Rows.Zip(a.Rows))
            {
                Assert.Equal(er.Key, ar.Key);
                Assert.Equal(er.Cells, ar.Cells);
                Assert.Equal(er.Selected, ar.Selected);
            }
        }
    }
}
