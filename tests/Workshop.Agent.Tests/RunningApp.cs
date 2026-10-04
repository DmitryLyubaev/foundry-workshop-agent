using System.Net;
using System.Net.Sockets;
using Workshop.Agent.Runner;
using Workshop.Core;

namespace Workshop.Agent.Tests;

/// <summary>
/// The built Workshop.App.exe, started through <see cref="AppProcess"/> as the runner starts it: a
/// fresh database, a free port and a session directory, in a temporary directory deleted at the end.
/// </summary>
internal sealed class RunningApp : IDisposable
{
    private readonly TempRun run;

    private RunningApp(TempRun run, AppProcess app)
    {
        this.run = run;
        App = app;
    }

    public AppProcess App { get; }

    public TempRun Run => run;

    public static RunningApp Start()
    {
        var run = TempRun.Create();
        try
        {
            _ = WorkshopDb.CreateFresh(run.DatabasePath);
            return new RunningApp(run, AppProcess.Start(AppProcess.FindAppExe(), run.DatabasePath, run.SessionDirectory, FreePort()));
        }
        catch
        {
            run.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        App.Dispose();
        run.Dispose();
    }

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>A temporary directory for one run: where its database, audit log and session file go.</summary>
internal sealed class TempRun : IDisposable
{
    private TempRun(string directory) => Directory = directory;

    public string Directory { get; }

    public string DatabasePath => Path.Combine(Directory, "workshop.db");

    public string SessionDirectory => Path.Combine(Directory, "session");

    public string SessionFilePath => Path.Combine(SessionDirectory, "session.json");

    public static TempRun Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WorkshopAgentTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        return new TempRun(directory);
    }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}
