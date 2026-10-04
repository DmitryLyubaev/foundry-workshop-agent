using System.Diagnostics;
using Workshop.Agent.Runner;

namespace Workshop.Agent.Tests;

public sealed class AppProcessTests
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
