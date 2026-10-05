using System.Diagnostics;
using System.Runtime.InteropServices;
using Workshop.Agent.Runner;

namespace Workshop.Agent.Tests;

public sealed partial class AppProcessTests
{
    [Fact]
    public void Start_fails_cleanly_when_the_db_is_missing()
    {
        using var run = TempRun.Create();
        var waited = Stopwatch.StartNew();

        var failed = Assert.Throws<AppStartException>(
            () => AppProcess.Start(AppProcess.FindAppExe(), run.DatabasePath, run.SessionDirectory, RunningApp.FreePort()));

        // The app's exit is noticed at once, not after the 20-second wait for the session file.
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(15), $"Took {waited.Elapsed}.");
        Assert.Contains("exited with code 1", failed.Message, StringComparison.Ordinal);
        Assert.Contains($"There is no workshop database at '{run.DatabasePath}'.", failed.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(run.SessionFilePath));
        // As the transcript keeps it: the app's own words, with the run's paths taken out.
        var described = Redaction.Describe(failed, fullName: false);
        Assert.Contains(@"There is no workshop database at '<temp>\WorkshopAgentTests\<run>\workshop.db'.", described, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(run.Directory), described, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Start_fails_cleanly_when_the_exe_is_missing()
    {
        using var run = TempRun.Create();
        var missing = Path.Combine(run.Directory, "Workshop.App.exe");

        var failed = Assert.Throws<AppStartException>(
            () => AppProcess.Start(missing, run.DatabasePath, run.SessionDirectory, RunningApp.FreePort()));

        Assert.Contains(missing, failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_leaves_no_process()
    {
        using var app = RunningApp.Start();
        using var process = Process.GetProcessById(app.App.ProcessId);
        Assert.Equal("Workshop.App", process.ProcessName);
        Assert.Equal("job-list", (await app.App.Client.DescribeAsync(TestContext.Current.CancellationToken)).Id);

        app.App.Close();

        Assert.True(process.HasExited);
        // The app closed itself, as CloseMainWindow asks: a killed app would have left its session file.
        Assert.False(File.Exists(app.Run.SessionFilePath));
        app.App.Close();
    }

    [Fact]
    public async Task Close_kills_an_app_that_does_not_close_in_time()
    {
        using var app = RunningApp.Start();
        using var process = Process.GetProcessById(app.App.ProcessId);
        // Once it has answered, its window is up; suspended, it cannot act on the request to close.
        _ = await app.App.Client.DescribeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, NtSuspendProcess(process.Handle));

        var waited = Stopwatch.StartNew();
        app.App.Close();

        Assert.True(process.HasExited);
        Assert.True(waited.Elapsed >= AppProcess.CloseTimeout - TimeSpan.FromMilliseconds(250), $"Killed after {waited.Elapsed}.");
        // Killed, not closed: an app that closed itself would have removed its session file.
        Assert.True(File.Exists(app.Run.SessionFilePath));
    }

    [Fact]
    public void Closing_the_job_kills_the_app()
    {
        using var app = RunningApp.Start();
        using var process = Process.GetProcessById(app.App.ProcessId);

        // What Windows does when the runner ends, however it ends: its handle to the job closes.
        app.App.CloseJob();

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)));
        Assert.True(File.Exists(app.Run.SessionFilePath));
    }

    [Fact]
    public void Find_app_exe_finds_the_built_app()
    {
        var exe = AppProcess.FindAppExe();

        Assert.True(File.Exists(exe), exe);
        Assert.Equal("Workshop.App.exe", Path.GetFileName(exe));
        Assert.Equal("net10.0-windows", Path.GetFileName(Path.GetDirectoryName(exe)));
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}Workshop.App{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            exe,
            StringComparison.OrdinalIgnoreCase);
    }
}

public sealed partial class AppProcessTests
{
    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);
}

/// <summary>
/// Counts Workshop.App processes, so it runs alone: no other test's app may start or end meanwhile.
/// </summary>
[Collection(ProcessCensus.Name)]
public sealed class AppProcessCensusTests
{
    [Fact]
    public void Start_times_out_and_leaves_no_app()
    {
        using var run = TempRun.Create();
        _ = Workshop.Core.WorkshopDb.CreateFresh(run.DatabasePath);
        var before = AppIds();

        var failed = Assert.Throws<AppStartException>(
            () => AppProcess.Start(AppProcess.FindAppExe(), run.DatabasePath, run.SessionDirectory, RunningApp.FreePort(), TimeSpan.Zero));

        // The run's session directory, not its path: the path names the temp directory, the user and the run.
        Assert.Equal("Workshop.App wrote no session file in the run's session directory within 0 seconds.", failed.Message);
        Assert.Equal("AppStartException: Workshop.App wrote no session file in the run's session directory within 0 seconds.", Redaction.Describe(failed, fullName: false));
        Assert.Subset(before, AppIds());
    }

    private static HashSet<int> AppIds()
    {
        var apps = Process.GetProcessesByName("Workshop.App");
        try
        {
            return [.. apps.Where(p => !p.HasExited).Select(p => p.Id)];
        }
        finally
        {
            foreach (var app in apps)
            {
                app.Dispose();
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessCensus
{
    public const string Name = "Process census";
}
