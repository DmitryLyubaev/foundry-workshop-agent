using System.Text.Json;
using Workshop.App.Screens;
using Workshop.Core;
using static Workshop.App.Tests.AppHost;

namespace Workshop.App.Tests;

/// <summary>The app driven through its endpoint, as the agent drives it, with the database checked after.</summary>
public sealed class EndToEndTests
{
    [Fact]
    public async Task Book_in_Sam_Riveras_laptop()
    {
        using var app = AppHost.Start();

        Assert.Equal("new-job", ScreenId(AssertOk(await app.OpenAsync("new-job"))));
        var customer = AssertOk(await app.SetAsync("customer", "C-001 Sam Rivera"));
        Assert.Equal(["D-001 Aster Book 14 (laptop)", "D-002 Nimbus 8 (phone)"], Options(customer, "device"));
        AssertOk(await app.SetAsync("device", "D-001 Aster Book 14 (laptop)"));
        AssertOk(await app.SetAsync("fault", "Fan is very loud"));
        var booked = AssertOk(await app.PressAsync("book-in"));

        var job = Assert.Single(app.Jobs.Jobs(JobStatus.BookedIn, null), j => j.DeviceId == "D-001");
        Assert.Equal("Fan is very loud", job.Fault);
        Assert.Equal("job-detail", ScreenId(booked));
        Assert.StartsWith($"{job.Id}: ", FieldValue(booked, "job"));
    }

    [Fact]
    public async Task Ready_with_a_part_on_order_is_validation_failed()
    {
        using var app = AppHost.Start();
        await app.OpenJobAsync("J-1006");

        // P-02 is out of stock, so the part goes on order: the action succeeds, with the app's message.
        AssertOk(await app.SetAsync("part", "P-02 Phone screen assembly"));
        var added = AssertOk(await app.PressAsync("add-part"));
        Assert.Equal("Added on order: not enough in stock.", added.GetProperty("message").GetString());
        Assert.Equal("Added on order: not enough in stock.", app.OnUi(InlineMessage));

        AssertOk(await app.SetAsync("status", "ready"));
        var refused = await app.PressAsync("save-status");

        Assert.Equal("validation_failed", refused.GetProperty("outcome").GetString());
        Assert.Equal("Job J-1006 still has parts on order.", refused.GetProperty("message").GetString());
        Assert.Equal("Job J-1006 still has parts on order.", app.OnUi(InlineMessage));
        Assert.Equal(JobStatus.InRepair, app.Jobs.Job("J-1006")!.Status);
        // The refused status is not left showing: the field is back to the job's own.
        Assert.Equal("in repair", FieldValue(refused, "status"));
    }

    [Fact]
    public async Task Cancel_job_button_is_described_as_destructive()
    {
        using var app = AppHost.Start();
        await app.OpenJobAsync("J-1008");

        var buttons = (await app.ScreenAsync()).GetProperty("buttons").EnumerateArray()
            .ToDictionary(b => b.GetProperty("id").GetString()!, b => b.GetProperty("destructive").GetBoolean());

        Assert.True(buttons["cancel-job"]);
        Assert.Equal(["cancel-job"], buttons.Where(b => b.Value).Select(b => b.Key));
    }

    [Fact]
    public async Task Saving_the_cancelled_status_is_validation_failed_so_only_cancel_job_cancels()
    {
        using var app = AppHost.Start();
        var opened = await app.OpenJobAsync("J-1008");

        // Still an option, so a cancelled job shows its status.
        Assert.Contains("cancelled", Options(opened, "status"));
        AssertOk(await app.SetAsync("status", "cancelled"));
        var refused = await app.PressAsync("save-status");

        Assert.Equal("validation_failed", refused.GetProperty("outcome").GetString());
        Assert.Equal("Use Cancel job to cancel a job.", refused.GetProperty("message").GetString());
        Assert.Equal("Use Cancel job to cancel a job.", app.OnUi(InlineMessage));
        Assert.Equal(JobStatus.Diagnosing, app.Jobs.Job("J-1008")!.Status);
    }

    [Fact]
    public async Task Refused_cancel_through_save_status_restores_the_status_field()
    {
        using var app = AppHost.Start();
        await app.OpenJobAsync("J-1008");

        AssertOk(await app.SetAsync("status", "cancelled"));
        var refused = await app.PressAsync("save-status");

        // The screen must not show a cancelled job: a reader of the screen, not the outcome, would believe it.
        Assert.Equal("validation_failed", refused.GetProperty("outcome").GetString());
        Assert.Equal("diagnosing", FieldValue(refused, "status"));
        Assert.Equal(JobStatus.Diagnosing, app.Jobs.Job("J-1008")!.Status);
    }

    [Fact]
    public async Task Cancel_job_cancels_the_job()
    {
        using var app = AppHost.Start();
        await app.OpenJobAsync("J-1008");

        var cancelled = AssertOk(await app.PressAsync("cancel-job"));

        Assert.Equal(JobStatus.Cancelled, app.Jobs.Job("J-1008")!.Status);
        Assert.Equal("cancelled", FieldValue(cancelled, "status"));
    }

    [Fact]
    public async Task Find_the_Henderson_printer_job_status()
    {
        using var app = AppHost.Start();
        AssertOk(await app.OpenAsync("job-list"));

        var found = AssertOk(await app.SetAsync("search", "Henderson"));

        var row = Assert.Single(Rows(found, "jobs"));
        Assert.Equal("J-1008", row.GetProperty("key").GetString());
        Assert.Equal(["J-1008", "Henderson Family", "Inkwell 300 (printer)", "diagnosing"], Cells(row));

        // And on the job itself.
        AssertOk(await app.SelectAsync("jobs", "J-1008"));
        var opened = AssertOk(await app.PressAsync("open-job"));
        Assert.Equal("diagnosing", FieldValue(opened, "status"));
    }

    [Fact]
    public async Task Audit_log_has_one_line_per_action()
    {
        using var app = AppHost.Start();

        object[] actions =
        [
            new { type = "open", screen = "job-list" },
            new { type = "set", field = "status-filter", value = "ready" },
            new { type = "select", list = "jobs", row = "J-1005" },
            new { type = "press", button = "open-job" },
            new { type = "set", field = "quantity", value = "21" },
            new { type = "press", button = "nothing" },
        ];
        foreach (var action in actions)
        {
            await app.ActAsync(action);
        }

        _ = await app.ScreenAsync(); // reading the screen is not an action

        var lines = app.AuditLines();
        Assert.Equal(actions.Length, lines.Count);
        Assert.Equal(
            [
                ("open", "job-list", "ok"),
                ("set", "status-filter", "ok"),
                ("select", "jobs/J-1005", "ok"),
                ("press", "open-job", "ok"),
                ("set", "quantity", "validation_failed"),
                ("press", "nothing", "not_found"),
            ],
            lines.Select(l => (l.GetProperty("type").GetString(), l.GetProperty("target").GetString(), l.GetProperty("outcome").GetString())));
    }

    // The rules a screen must report inline, rather than prevent or throw.

    [Fact]
    public async Task Saving_the_same_status_is_validation_failed()
    {
        using var app = AppHost.Start();
        await app.OpenJobAsync("J-1008");

        var refused = await app.PressAsync("save-status");

        Assert.Equal("validation_failed", refused.GetProperty("outcome").GetString());
        Assert.Equal("A job that is diagnosing cannot become diagnosing.", refused.GetProperty("message").GetString());
        Assert.Equal("A job that is diagnosing cannot become diagnosing.", app.OnUi(InlineMessage));
    }

    [Fact]
    public async Task Adding_a_part_to_a_ready_job_is_validation_failed()
    {
        using var app = AppHost.Start();
        await app.OpenJobAsync("J-1005");
        var partsBefore = app.Jobs.JobParts("J-1005").Count;

        AssertOk(await app.SetAsync("part", "P-06 SSD 1 TB"));
        var refused = await app.PressAsync("add-part");

        const string Locked = "The parts of a job that is ready cannot change. Set the status to in repair first.";
        Assert.Equal("validation_failed", refused.GetProperty("outcome").GetString());
        Assert.Equal(Locked, refused.GetProperty("message").GetString());
        Assert.Equal(Locked, app.OnUi(InlineMessage));
        Assert.Equal(partsBefore, app.Jobs.JobParts("J-1005").Count);
    }

    [Theory]
    [InlineData("job-list", "open-job", "Select a job first.")]
    [InlineData("customer-list", "open-customer", "Select a customer first.")]
    public async Task Opening_without_a_selected_row_is_validation_failed(string list, string button, string message)
    {
        using var app = AppHost.Start();
        AssertOk(await app.OpenAsync(list));

        var refused = await app.PressAsync(button);

        Assert.Equal("validation_failed", refused.GetProperty("outcome").GetString());
        Assert.Equal(message, refused.GetProperty("message").GetString());
        Assert.Equal(list, ScreenId(refused));
    }

    [Theory]
    [InlineData("job-detail", "job-list")]
    [InlineData("customer-detail", "customer-list")]
    public async Task Opening_a_detail_screen_without_a_record_opens_the_list(string detail, string list)
    {
        using var app = AppHost.Start();

        var opened = AssertOk(await app.OpenAsync(detail));

        Assert.Equal(list, ScreenId(opened));
    }

    [Fact]
    public async Task Opening_the_job_detail_screen_shows_the_job_selected_in_the_list()
    {
        using var app = AppHost.Start();
        AssertOk(await app.OpenAsync("job-list"));
        AssertOk(await app.SelectAsync("jobs", "J-1009"));

        var opened = AssertOk(await app.OpenAsync("job-detail"));

        Assert.Equal("job-detail", ScreenId(opened));
        Assert.StartsWith("J-1009: ", FieldValue(opened, "job"));
        Assert.Equal(["P-04#1"], Rows(opened, "parts").Select(r => r.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task Job_detail_shows_the_job_booked_in_last_not_an_older_selection()
    {
        using var app = AppHost.Start();
        await app.OpenJobAsync("J-1008");

        AssertOk(await app.OpenAsync("new-job"));
        AssertOk(await app.SetAsync("customer", "C-001 Sam Rivera"));
        AssertOk(await app.SetAsync("device", "D-001 Aster Book 14 (laptop)"));
        AssertOk(await app.SetAsync("fault", "Fan is very loud"));
        AssertOk(await app.PressAsync("book-in"));
        var booked = Assert.Single(app.Jobs.Jobs(JobStatus.BookedIn, null), j => j.DeviceId == "D-001").Id;
        AssertOk(await app.OpenAsync("parts"));

        var back = AssertOk(await app.OpenAsync("job-detail"));

        Assert.Equal("job-detail", ScreenId(back));
        Assert.StartsWith($"{booked}: ", FieldValue(back, "job"));

        // The list shows the current job selected, so selecting the older job again counts as a change.
        var list = AssertOk(await app.OpenAsync("job-list"));
        Assert.Equal([booked], SelectedKeys(list, "jobs"));
        AssertOk(await app.SelectAsync("jobs", "J-1008"));
        Assert.StartsWith("J-1008: ", FieldValue(AssertOk(await app.OpenAsync("job-detail")), "job"));
    }

    [Fact]
    public async Task Customer_detail_shows_the_customer_selected_or_opened_last()
    {
        using var app = AppHost.Start();
        AssertOk(await app.OpenAsync("customer-list"));
        AssertOk(await app.SelectAsync("customers", "C-008"));
        AssertOk(await app.PressAsync("open-customer"));
        AssertOk(await app.OpenAsync("parts"));

        Assert.Equal("Aiko Tanaka", FieldValue(AssertOk(await app.OpenAsync("customer-detail")), "name"));

        var list = AssertOk(await app.OpenAsync("customer-list"));
        Assert.Equal(["C-008"], SelectedKeys(list, "customers"));
        AssertOk(await app.SelectAsync("customers", "C-002"));
        Assert.Equal("Henderson Family", FieldValue(AssertOk(await app.OpenAsync("customer-detail")), "name"));
    }

    [Fact]
    public async Task Add_a_device_for_a_customer()
    {
        using var app = AppHost.Start();
        AssertOk(await app.OpenAsync("customer-list"));
        AssertOk(await app.SetAsync("search", "Tanaka"));
        AssertOk(await app.SelectAsync("customers", "C-008"));
        Assert.Equal("customer-detail", ScreenId(AssertOk(await app.PressAsync("open-customer"))));

        AssertOk(await app.SetAsync("kind", "phone"));
        AssertOk(await app.SetAsync("model", "Nimbus 9"));
        AssertOk(await app.SetAsync("serial", "NB9-0001"));
        var added = AssertOk(await app.PressAsync("add-device"));

        var device = Assert.Single(app.Jobs.DevicesOf("C-008"), d => d.Model == "Nimbus 9");
        Assert.Contains(device.Id, Rows(added, "devices").Select(r => r.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task Receive_stock_for_a_part()
    {
        using var app = AppHost.Start();
        AssertOk(await app.OpenAsync("parts"));
        AssertOk(await app.SelectAsync("parts", "P-04"));
        AssertOk(await app.SetAsync("quantity", "3"));

        var received = AssertOk(await app.PressAsync("receive-stock"));

        Assert.Equal(3, app.Jobs.Parts().Single(p => p.Id == "P-04").Stock);
        var row = Rows(received, "parts").Single(r => r.GetProperty("key").GetString() == "P-04");
        Assert.Equal("3", Cells(row)[2]);
        Assert.True(row.GetProperty("selected").GetBoolean());
    }

    private static string? InlineMessage(MainForm form) => ((WorkshopScreen)form.Navigator.Current).Message;

    private static string? ScreenId(JsonElement result) => result.GetProperty("screen").GetProperty("id").GetString();

    private static JsonElement Field(JsonElement result, string id) =>
        result.GetProperty("screen").GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("id").GetString() == id);

    private static string? FieldValue(JsonElement result, string id) =>
        Field(result, id).TryGetProperty("value", out var value) ? value.GetString() : null;

    private static string?[] Options(JsonElement result, string id) =>
        [.. Field(result, id).GetProperty("options").EnumerateArray().Select(o => o.GetString())];

    private static JsonElement[] Rows(JsonElement result, string list) =>
        [
            .. result.GetProperty("screen").GetProperty("lists").EnumerateArray()
                .Single(l => l.GetProperty("id").GetString() == list)
                .GetProperty("rows").EnumerateArray(),
        ];

    private static string?[] SelectedKeys(JsonElement result, string list) =>
        [.. Rows(result, list).Where(r => r.GetProperty("selected").GetBoolean()).Select(r => r.GetProperty("key").GetString())];

    private static string?[] Cells(JsonElement row) => [.. row.GetProperty("cells").EnumerateArray().Select(c => c.GetString())];
}
