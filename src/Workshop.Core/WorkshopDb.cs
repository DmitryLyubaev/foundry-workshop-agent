using Dapper;
using Microsoft.Data.Sqlite;

namespace Workshop.Core;

/// <summary>A workshop database file. Every run starts from a fresh, seeded copy.</summary>
public sealed class WorkshopDb
{
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

    /// <summary>Opens a database file that already exists, such as a fresh copy a test runner made.</summary>
    /// <exception cref="FileNotFoundException">There is no file at <paramref name="path"/>.</exception>
    public static WorkshopDb OpenExisting(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // The connections open read-write without create, so a missing file would fail later anyway;
        // checking here fails at start-up, with the path in the message.
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There is no workshop database at '{path}'.", path);
        }

        return new WorkshopDb(path);
    }

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
