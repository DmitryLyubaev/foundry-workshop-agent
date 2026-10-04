using System.Data.Common;
using System.Globalization;
using System.Net;
using Workshop.Core;
using Workshop.Surface;

namespace Workshop.App;

internal static class Program
{
    /// <summary>
    /// The audit log's file name. It is kept beside the database: a fresh database gets a run
    /// directory of its own, and a runner that passes <c>--db</c> gives each run's copy its own.
    /// </summary>
    public const string AuditFileName = "audit.jsonl";

    /// <summary>
    /// Exit codes: 0 after a normal close, 1 when the app could not start (the database is missing
    /// or is not a workshop database, or the port is taken), 2 for a bad command line. The reason
    /// goes to standard error.
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
        // InvalidDataException and DbException: a --db that is not a workshop database, found by the
        // table check or by the first query as the main form loads.
        catch (Exception e) when (e is IOException or InvalidDataException or DbException or InvalidOperationException or UnauthorizedAccessException)
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
    /// <exception cref="InvalidDataException"><c>--db</c> names a file that is not a workshop database.</exception>
    /// <exception cref="DbException">The database has the workshop's tables, but a query on them fails.</exception>
    /// <exception cref="InvalidOperationException">The endpoint could not listen, as when another instance holds the port.</exception>
    internal static Launched Launch(AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var created = options.DatabasePath is null;
        var databasePath = Path.GetFullPath(options.DatabasePath ?? NewDatabasePath(SessionFile.DefaultDirectory));
        var db = created ? WorkshopDb.CreateFresh(databasePath) : WorkshopDb.OpenExisting(databasePath);

        MainForm? form = null;
        try
        {
            // The form's first screen queries the database, so a bad one fails here.
            form = new MainForm(new JobService(db));

            // The endpoint invokes onto the UI thread through the form's handle, so it must exist first.
            _ = form.Handle;
            var audit = new AuditLog(Path.Combine(Path.GetDirectoryName(databasePath)!, AuditFileName));
            var endpoint = new SurfaceEndpoint(IPAddress.Loopback, options.Port, options.SessionDirectory, form, form.Navigator, audit);
            endpoint.Start();
            return new Launched(form, endpoint);
        }
        catch
        {
            form?.Dispose();
            if (created)
            {
                File.Delete(databasePath);
                DeleteIfEmpty(Path.GetDirectoryName(databasePath)!);
            }

            throw;
        }
    }

    /// <summary>
    /// <c>&lt;baseDirectory&gt;\runs\&lt;timestamp&gt;\workshop.db</c>, its run directory created, so
    /// each run without <c>--db</c> keeps its database and its audit log apart from every other run's.
    /// </summary>
    internal static string NewDatabasePath(string baseDirectory)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var run = Directory.CreateDirectory(Path.Combine(baseDirectory, "runs", stamp));
        return Path.Combine(run.FullName, "workshop.db");
    }

    /// <summary>Removes a run directory left empty by a failed start; one that is not empty is kept, and the start's own error stands.</summary>
    private static void DeleteIfEmpty(string directory)
    {
        try
        {
            Directory.Delete(directory);
        }
        catch (IOException)
        {
        }
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
