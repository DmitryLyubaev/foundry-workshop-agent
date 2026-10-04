using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Workshop.Agent.Surface;

namespace Workshop.Agent.Runner;

/// <summary>
/// One launch of the built <c>Workshop.App.exe</c> on a given database, port and session
/// directory, and the client for its endpoint. <see cref="Close"/>, and so <see cref="Dispose"/>,
/// always ends the process: it asks the app to close, as a person would, and kills it if it has not
/// closed within <see cref="CloseTimeout"/>. The app also runs in a kill-on-close job, so it ends
/// with this process even when nothing gets to call <see cref="Close"/>.
/// </summary>
public sealed class AppProcess : IDisposable
{
    public const string AppExeName = "Workshop.App.exe";

    /// <summary>How long <see cref="Start"/> waits for the app's session file.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long <see cref="Close"/> gives the app, from its first <c>CloseMainWindow</c>, to close itself before it kills it.</summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly Process process;
    private readonly KillOnCloseJob? job;
    private readonly Lock closing = new();
    private bool closed;

    private AppProcess(Process process, KillOnCloseJob? job, int port, SurfaceClient client)
    {
        this.process = process;
        this.job = job;
        ProcessId = process.Id;
        Port = port;
        Client = client;
    }

    public SurfaceClient Client { get; }

    public int Port { get; }

    public int ProcessId { get; }

    /// <summary>
    /// Starts <paramref name="appExe"/> with <c>--db</c>, <c>--port</c> and <c>--session-dir</c>, and
    /// waits up to <see cref="StartTimeout"/> for the session file this process writes. On any failure
    /// the process is gone before the exception is thrown.
    /// </summary>
    /// <exception cref="AppStartException">
    /// The app could not be started, exited before it was ready (its standard error is in the message),
    /// or wrote no session file in time.
    /// </exception>
    public static AppProcess Start(string appExe, string dbPath, string sessionDir, int port) =>
        Start(appExe, dbPath, sessionDir, port, StartTimeout);

    /// <summary>For tests: a wait for the session file other than <see cref="StartTimeout"/>.</summary>
    internal static AppProcess Start(string appExe, string dbPath, string sessionDir, int port, TimeSpan startTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appExe);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDir);

        var start = new ProcessStartInfo(appExe)
        {
            UseShellExecute = false,
            // The app writes why it could not start to standard error; read as it comes, so a full pipe never blocks it.
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "--db", dbPath, "--port", port.ToString(CultureInfo.InvariantCulture), "--session-dir", sessionDir })
        {
            start.ArgumentList.Add(arg);
        }

        var errors = new StringBuilder();
        var process = new Process { StartInfo = start };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (errors)
                {
                    errors.AppendLine(e.Data);
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            process.Dispose();
            throw new AppStartException($"Workshop.App could not be started from '{appExe}': {e.Message}", e);
        }

        // At once, so the moment in which a dying runner could orphan the app is as short as it can be.
        var job = KillOnCloseJob.TryAssign(process);
        try
        {
            process.BeginErrorReadLine();
            var session = WaitForSession(process, sessionDir, startTimeout, () =>
            {
                lock (errors)
                {
                    return errors.ToString().Trim();
                }
            });
            return new AppProcess(process, job, port, new SurfaceClient(session.Port, session.Token));
        }
        catch
        {
            End(process);
            job?.Dispose();
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <c>src/Workshop.App/bin/&lt;Configuration&gt;/net10.0-windows/Workshop.App.exe</c> under the
    /// solution root, found from this program's directory or else the current one, in the
    /// configuration this assembly was built in.
    /// </summary>
    /// <exception cref="FileNotFoundException">There is no built app there.</exception>
    public static string FindAppExe()
    {
        var configuration = typeof(AppProcess).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration is { Length: > 0 } built
            ? built
            : "Release";

        foreach (var from in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(from); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "FoundryWorkshopAgent.slnx")))
                {
                    var exe = Path.Combine(directory.FullName, "src", "Workshop.App", "bin", configuration, "net10.0-windows", AppExeName);
                    return File.Exists(exe)
                        ? exe
                        : throw new FileNotFoundException($"There is no {AppExeName} at '{exe}'. Build Workshop.App in {configuration} first.", exe);
                }
            }
        }

        throw new FileNotFoundException($"No solution root (FoundryWorkshopAgent.slnx) above '{AppContext.BaseDirectory}' or '{Environment.CurrentDirectory}'.");
    }

    /// <summary>
    /// Ends the app: <c>CloseMainWindow</c>, so it closes itself and removes its session file; then
    /// up to <see cref="CloseTimeout"/> for it to exit; then <c>Kill(entireProcessTree: true)</c>;
    /// then the job's handle is closed, which ends anything still in it. Safe to call more than once,
    /// and it never throws: a failure is traced.
    /// </summary>
    public void Close()
    {
        lock (closing)
        {
            if (closed)
            {
                return;
            }

            closed = true;
        }

        try
        {
            Client.Dispose();
            End(process);
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"Closing Workshop.App (pid {ProcessId}) failed: {e}");
        }
        finally
        {
            job?.Dispose();
            process.Dispose();
        }
    }

    public void Dispose() => Close();

    /// <summary>For tests: closes this process's handle to the job, as Windows does when this process ends.</summary>
    internal void CloseJob() => job?.Dispose();

    /// <summary>Polls for this process's session file, and stops as soon as the process exits.</summary>
    private static SessionInfo WaitForSession(Process process, string sessionDir, TimeSpan timeout, Func<string> errors)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            // The pid check: a file left by an earlier app in the same directory is not this one's.
            if (SessionReader.TryRead(sessionDir) is { } session && session.Pid == process.Id)
            {
                return session.Info;
            }

            if (process.HasExited)
            {
                // The parameterless wait also waits for standard error to be read to its end.
                process.WaitForExit();
                var reason = errors();
                throw new AppStartException(
                    $"Workshop.App exited with code {process.ExitCode} before it was ready{(reason.Length > 0 ? $": {reason}" : ".")}");
            }

            if (waited.Elapsed >= timeout)
            {
                throw new AppStartException(
                    $"Workshop.App wrote no session file in '{sessionDir}' within {timeout.TotalSeconds:0} seconds.");
            }

            Thread.Sleep(PollInterval);
        }
    }

    private static void End(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            // The app writes its session file before its window is shown, so a run that ends at once
            // can find no window yet: the request to close is repeated until there is one, all within
            // the one CloseTimeout.
            var waited = Stopwatch.StartNew();
            while (!AskToClose(process) && !process.HasExited && waited.Elapsed < CloseTimeout)
            {
                Thread.Sleep(PollInterval);
            }

            var left = CloseTimeout - waited.Elapsed;
            if (!process.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(CloseTimeout);
            }
        }
        catch (Exception e)
        {
            // It exited between a check and a call, or killing its tree partly failed
            // (Kill(entireProcessTree) can throw AggregateException): the kill is the last thing to try.
            Trace.TraceWarning($"Ending Workshop.App (pid {process.Id}) failed, so it is killed: {e.Message}");
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception again)
            {
                Trace.TraceWarning($"Workshop.App (pid {process.Id}) could not be killed: {again.Message}");
            }
        }
    }

    /// <summary>Asks the app's window to close; false while it has none.</summary>
    private static bool AskToClose(Process process)
    {
        // MainWindowHandle is cached by Process; refresh it, or a window shown since start is missed.
        process.Refresh();
        return process.CloseMainWindow();
    }
}
