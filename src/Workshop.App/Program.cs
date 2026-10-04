using System.Globalization;
using System.Net;
using Workshop.Core;
using Workshop.Surface;

namespace Workshop.App;

internal static class Program
{
    /// <summary>The audit log's file name. It is kept beside the database, so each run's copy has its own.</summary>
    public const string AuditFileName = "audit.jsonl";

    /// <summary>
    /// Exit codes: 0 after a normal close, 1 when the app could not start (no database, or the
    /// port is taken), 2 for a bad command line. The reason goes to standard error.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        AppOptions options;
        try
        {
            options = AppOptions.Parse(args);
        }
        catch (ArgumentException e)
        {
            return Fail(2, $"{e.Message}{Environment.NewLine}{AppOptions.Usage}");
        }

        // An unhandled exception ends the app rather than opening WinForms' modal error dialog:
        // the agent can neither see nor dismiss a modal, and no action may wait behind one.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        ApplicationConfiguration.Initialize();

        Launched launched;
        try
        {
            launched = Launch(options);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Fail(1, e.Message);
        }

        using (launched.Form)
        using (launched.Endpoint)
        {
            Application.Run(launched.Form);
        }

        return 0;
    }

    /// <summary>
    /// Opens or creates the database, builds the main form, and starts the agent surface's endpoint
    /// on it. Call on the thread that will run the form's message loop.
    /// </summary>
    /// <exception cref="FileNotFoundException"><c>--db</c> names a file that does not exist.</exception>
    /// <exception cref="InvalidOperationException">The endpoint could not listen, as when another instance holds the port.</exception>
    internal static Launched Launch(AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var created = options.DatabasePath is null;
        var databasePath = Path.GetFullPath(options.DatabasePath ?? NewDatabasePath());
        var db = created ? WorkshopDb.CreateFresh(databasePath) : WorkshopDb.OpenExisting(databasePath);

        var form = new MainForm(new JobService(db));
        try
        {
            // The endpoint invokes onto the UI thread through the form's handle, so it must exist first.
            _ = form.Handle;
            var audit = new AuditLog(Path.Combine(Path.GetDirectoryName(databasePath)!, AuditFileName));
            var endpoint = new SurfaceEndpoint(IPAddress.Loopback, options.Port, options.SessionDirectory, form, form.Navigator, audit);
            endpoint.Start();
            return new Launched(form, endpoint);
        }
        catch
        {
            form.Dispose();
            if (created)
            {
                File.Delete(databasePath);
            }

            throw;
        }
    }

    /// <summary><c>%LOCALAPPDATA%\FoundryWorkshopAgent\workshop-&lt;timestamp&gt;.db</c>, its directory created.</summary>
    private static string NewDatabasePath()
    {
        Directory.CreateDirectory(SessionFile.DefaultDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        return Path.Combine(SessionFile.DefaultDirectory, $"workshop-{stamp}.db");
    }

    /// <summary>A WinExe has no console of its own; standard error still reaches a caller that captures it.</summary>
    private static int Fail(int exitCode, string reason)
    {
        Console.Error.WriteLine($"Workshop.App: {reason}");
        return exitCode;
    }

    /// <summary>A started app: its main form, and the endpoint serving it.</summary>
    internal sealed record Launched(MainForm Form, SurfaceEndpoint Endpoint);
}
