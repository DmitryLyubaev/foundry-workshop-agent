using System.Diagnostics;
using Workshop.Agent.Surface;

namespace Workshop.Agent.Tests;

/// <summary>The client against the real built app, started by <see cref="Runner.AppProcess"/>.</summary>
public sealed class SurfaceClientTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, "token")]
    [InlineData(65536, "token")]
    [InlineData(5000, " ")]
    public void A_bad_port_or_token_is_refused_before_anything_is_made(int port, string token)
    {
        // Checked before the handler is built, so a refused client leaves no handler behind.
        var refused = Assert.ThrowsAny<ArgumentException>(() => new SurfaceClient(port, token));

        Assert.Equal(port is < 1 or > 65535 ? "port" : "token", refused.ParamName);
    }

    [Fact]
    public async Task Lists_the_six_screens()
    {
        using var app = RunningApp.Start();

        var screens = await app.App.Client.ListScreensAsync(Cancel);

        Assert.Equal(
            [
                new ScreenInfo("job-list", "Jobs"),
                new ScreenInfo("job-detail", "Job"),
                new ScreenInfo("new-job", "New job"),
                new ScreenInfo("customer-list", "Customers"),
                new ScreenInfo("customer-detail", "Customer"),
                new ScreenInfo("parts", "Parts"),
            ],
            screens);
    }

    [Fact]
    public async Task Describe_returns_job_list_with_15_rows()
    {
        using var app = RunningApp.Start();

        var screen = await app.App.Client.DescribeAsync(Cancel);

        Assert.Equal("job-list", screen.Id);
        Assert.Equal("Jobs", screen.Title);
        Assert.Equal(["search", "status-filter"], screen.Fields.Select(f => f.Id));
        Assert.Equal(["open-job", "new-job"], screen.Buttons.Select(b => b.Id));
        var jobs = Assert.Single(screen.Lists);
        Assert.Equal("jobs", jobs.Id);
        Assert.Equal(["Job", "Customer", "Device", "Status"], jobs.Columns);
        Assert.Equal(15, jobs.Rows.Length);
        Assert.Equal(
            Enumerable.Range(1001, 15).Select(n => $"J-{n}"),
            jobs.Rows.Select(r => r.Key).Order(StringComparer.Ordinal));
        Assert.All(jobs.Rows, row => Assert.Equal(4, row.Cells.Length));
    }

    [Fact]
    public async Task Act_open_new_job_returns_ok_and_the_new_screen()
    {
        using var app = RunningApp.Start();

        var reply = await app.App.Client.ActAsync(ActionRequest.Open("new-job"), Cancel);

        Assert.Equal("ok", reply.Outcome);
        Assert.Null(reply.Message);
        Assert.Equal("new-job", reply.Screen.Id);
        Assert.Equal("New job", reply.Screen.Title);
        Assert.Equal(["customer", "device", "fault"], reply.Screen.Fields.Select(f => f.Id));
        Assert.Equal("choice", reply.Screen.Fields[0].Kind);
        Assert.Contains("C-001 Sam Rivera", reply.Screen.Fields[0].Options!);
        Assert.Equal(["book-in"], reply.Screen.Buttons.Select(b => b.Id));
        Assert.Empty(reply.Screen.Lists);
    }

    [Fact]
    public async Task Act_reports_the_apps_outcome_and_message()
    {
        using var app = RunningApp.Start();

        var reply = await app.App.Client.ActAsync(ActionRequest.Press("cancel-job"), Cancel);

        Assert.Equal("not_found", reply.Outcome);
        Assert.Equal("There is no button 'cancel-job' on this screen.", reply.Message);
        Assert.Equal("job-list", reply.Screen.Id);
    }

    [Fact]
    public async Task Keeps_every_screen_it_receives()
    {
        using var app = RunningApp.Start();
        var client = app.App.Client;

        _ = await client.ListScreensAsync(Cancel);
        var described = await client.DescribeAsync(Cancel);
        var opened = await client.ActAsync(ActionRequest.Open("new-job"), Cancel);

        Assert.Equal(["job-list", "new-job"], client.SeenScreens.Select(s => s.Id));
        Assert.Same(described, client.SeenScreens[0]);
        Assert.Same(opened.Screen, client.SeenScreens[1]);
    }

    [Fact]
    public async Task Wrong_token_throws_401()
    {
        using var app = RunningApp.Start();
        using var client = new SurfaceClient(app.App.Port, "not-the-token");

        var refused = await Assert.ThrowsAsync<SurfaceHttpException>(() => client.DescribeAsync(Cancel));

        Assert.Equal(401, refused.Status);
        Assert.Equal("""{"error":"unauthorized"}""", refused.Body);
    }

    [Fact]
    public async Task Session_reader_waits_for_the_file_to_appear()
    {
        using var run = TempRun.Create();
        var writer = Task.Run(
            async () =>
            {
                await Task.Delay(300, Cancel);
                Workshop.Surface.SessionFile.Write(run.SessionDirectory, 50123, "made-up-token");
            },
            Cancel);

        SessionInfo session;
        try
        {
            session = SessionReader.WaitFor(run.SessionDirectory, TimeSpan.FromSeconds(10));
        }
        finally
        {
            // Even when the wait fails, so the file is not written after the directory is deleted.
            await writer;
        }

        Assert.Equal(new SessionInfo(50123, "made-up-token"), session);
    }

    [Fact]
    public void Session_reader_times_out_when_no_file_appears()
    {
        using var run = TempRun.Create();
        var waited = Stopwatch.StartNew();

        var timedOut = Assert.Throws<TimeoutException>(
            () => SessionReader.WaitFor(run.SessionDirectory, TimeSpan.FromMilliseconds(500)));

        Assert.InRange(waited.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        Assert.Contains(run.SessionDirectory, timedOut.Message, StringComparison.Ordinal);
    }
}
