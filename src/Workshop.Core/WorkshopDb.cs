using Dapper;
using Microsoft.Data.Sqlite;

namespace Workshop.Core;

/// <summary>A workshop database file. Every run starts from a fresh, seeded copy.</summary>
public sealed class WorkshopDb
{
    /// <summary>The schema's tables. An empty file opens as an empty database, so the check counts them.</summary>
    private static readonly string[] Tables = ["customers", "devices", "jobs", "parts", "job_parts", "notes"];

    private readonly string _connectionString;

    private WorkshopDb(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            // Without pooling, the file is released as soon as a connection closes, so a run's
            // copy can be deleted or replaced straight after.
            Pooling = false,
        }.ToString();
    }

    /// <summary>Creates a new database file from the schema and the seed. Throws if the file exists.</summary>
    public static WorkshopDb CreateFresh(string path)
    {
        // CreateNew checks and creates in one step, so an existing file is never touched.
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
        }

        try
        {
            var db = new WorkshopDb(path);
            using var conn = db.Open();
            using var tx = conn.BeginTransaction();
            conn.Execute(ReadScript("schema.sql"), transaction: tx);
            conn.Execute(ReadScript("seed.sql"), transaction: tx);
            tx.Commit();
            return db;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    /// <summary>
    /// Opens a database file that already exists, such as a fresh copy a test runner made, after
    /// checking that it is a SQLite database with the workshop's tables. The file is only read.
    /// </summary>
    /// <exception cref="FileNotFoundException">There is no file at <paramref name="path"/>.</exception>
    /// <exception cref="InvalidDataException">The file is not a workshop database.</exception>
    public static WorkshopDb OpenExisting(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // The connections open read-write without create, so a missing file would fail later anyway;
        // checking here fails at start-up, with the path in the message.
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There is no workshop database at '{path}'.", path);
        }

        var db = new WorkshopDb(path);
        try
        {
            using var conn = db.Open();
            var tables = conn.ExecuteScalar<long>(
                "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN @Tables", new { Tables });
            if (tables == Tables.Length)
            {
                return db;
            }
        }
        catch (SqliteException e)
        {
            throw NotAWorkshopDatabase(path, e);
        }

        throw NotAWorkshopDatabase(path, null);
    }

    private static InvalidDataException NotAWorkshopDatabase(string path, Exception? inner) =>
        new($"'{path}' is not a workshop database.", inner);

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string ReadScript(string name)
    {
        var resource = $"Workshop.Core.Sql.{name}";
        using var stream = typeof(WorkshopDb).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The embedded script {resource} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
