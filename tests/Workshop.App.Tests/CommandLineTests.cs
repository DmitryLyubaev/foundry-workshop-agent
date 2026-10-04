using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Workshop.Surface;

namespace Workshop.App.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void No_arguments_use_the_defaults()
    {
        var options = AppOptions.Parse([]);

        Assert.Equal(new AppOptions(null, SurfaceEndpoint.DefaultPort, SessionFile.DefaultDirectory), options);
    }

    [Fact]
    public void Every_argument_is_read_in_any_order()
    {
        var options = AppOptions.Parse(["--session-dir", @"C:\runs\one", "--port", "50123", "--db", @"C:\runs\one\workshop.db"]);

        Assert.Equal(new AppOptions(@"C:\runs\one\workshop.db", 50123, @"C:\runs\one"), options);
    }

    [Theory]
    [InlineData("Unknown argument '--verbose'.", "--verbose")]
    [InlineData("Unknown argument 'workshop.db'.", "workshop.db")]
    [InlineData("--db needs a value.", "--db")]
    [InlineData("--db needs a value.", "--db", " ")]
    [InlineData("--port is given more than once.", "--port", "50000", "--port", "50001")]
    [InlineData("--port needs a whole number from 1 to 65535, not 'abc'.", "--port", "abc")]
    [InlineData("--port needs a whole number from 1 to 65535, not '0'.", "--port", "0")]
    [InlineData("--port needs a whole number from 1 to 65535, not '65536'.", "--port", "65536")]
    [InlineData("--port needs a whole number from 1 to 65535, not '-1'.", "--port", "-1")]
    public void Bad_arguments_are_refused_with_the_reason(string reason, params string[] args)
    {
        var refused = Assert.Throws<ArgumentException>(() => AppOptions.Parse(args));

        Assert.Equal(reason, refused.Message);
    }

    [Fact]
    public void A_missing_database_fails_the_launch_and_writes_no_session()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WorkshopAppTests", Guid.NewGuid().ToString("N"));
        var options = new AppOptions(Path.Combine(directory, "missing.db"), SurfaceEndpoint.DefaultPort, directory);

        var refused = Assert.Throws<FileNotFoundException>(() => Program.Launch(options));

        Assert.Contains("missing.db", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void A_file_that_is_not_a_workshop_database_fails_the_launch_and_writes_no_session()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "notes.txt");
            File.WriteAllText(path, "not a database");
            var sessionDirectory = Path.Combine(directory, "session");

            var refused = Assert.Throws<InvalidDataException>(
                () => Program.Launch(new AppOptions(path, SurfaceEndpoint.DefaultPort, sessionDirectory)));

            Assert.Equal($"'{path}' is not a workshop database.", refused.Message);
            Assert.False(Directory.Exists(sessionDirectory));
            Assert.Equal("not a database", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The real exe, as plan 2's runner starts it: a <c>--db</c> it cannot use ends the process with
    /// exit code 1 and the reason on standard error, never an unhandled exception or a window.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("text")]
    [InlineData("tables without the columns")]
    public void A_database_the_app_cannot_use_exits_1_with_the_reason_on_standard_error(string database)
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "workshop.db");
            var reason = $"'{path}' is not a workshop database.";
            switch (database)
            {
                case "missing":
                    reason = $"There is no workshop database at '{path}'.";
                    break;
                case "text":
                    File.WriteAllText(path, "not a database");
                    break;
                default:
                    // Passes the table check, then fails the first query, as the main form is built.
                    using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
                    {
                        conn.Open();
                        using var create = conn.CreateCommand();
                        create.CommandText = string.Concat(
                            new[] { "customers", "devices", "jobs", "parts", "job_parts", "notes" }.Select(t => $"CREATE TABLE {t} (x TEXT);"));
                        create.ExecuteNonQuery();
                    }

                    reason = "no such column";
                    break;
            }

            var sessionDirectory = Path.Combine(directory, "session");
            var (exitCode, error) = RunApp("--db", path, "--port", FreePort().ToString(CultureInfo.InvariantCulture), "--session-dir", sessionDirectory);

            Assert.Equal(1, exitCode);
            Assert.StartsWith("Workshop.App: ", error, StringComparison.Ordinal);
            Assert.Contains(reason, error, StringComparison.Ordinal);
            Assert.False(Directory.Exists(sessionDirectory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_fresh_database_gets_a_run_directory_of_its_own_for_it_and_its_audit_log()
    {
        var directory = NewDirectory();
        try
        {
            var path = Program.NewDatabasePath(directory);

            Assert.Equal("workshop.db", Path.GetFileName(path));
            var run = Path.GetDirectoryName(path)!;
            Assert.Equal(Path.Combine(directory, "runs"), Path.GetDirectoryName(run));
            Assert.True(Directory.Exists(run));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_second_launch_on_the_same_port_fails_and_leaves_the_first_session()
    {
        using var first = AppHost.Start();
        var session = File.ReadAllText(Path.Combine(first.SessionDirectory, SessionFile.FileName));
        var copy = Path.Combine(first.Directory, "second.db");
        File.Copy(first.DatabasePath, copy);

        var refused = first.OnUi(_ => Assert.Throws<InvalidOperationException>(
            () => Program.Launch(new AppOptions(copy, first.Port, first.SessionDirectory))));

        Assert.Contains(first.Port.ToString(CultureInfo.InvariantCulture), refused.Message, StringComparison.Ordinal);
        Assert.Equal(session, File.ReadAllText(Path.Combine(first.SessionDirectory, SessionFile.FileName)));
        Assert.True(File.Exists(copy), "A database given with --db is never deleted.");
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WorkshopAppTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Runs <c>Workshop.App.exe</c> to its end; one that is still running after 30 seconds is killed.</summary>
    private static (int ExitCode, string Error) RunApp(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Workshop.App.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Workshop.App.exe did not start.");
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Workshop.App.exe was still running after 30 seconds.");
        }

        return (process.ExitCode, error.GetAwaiter().GetResult());
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
