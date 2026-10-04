namespace Workshop.Core.Tests;

/// <summary>A fresh seeded database in a file of its own, deleted when the test ends.</summary>
internal sealed class TempDb : IDisposable
{
    public TempDb()
    {
        Path = NewPath();
        Db = WorkshopDb.CreateFresh(Path);
    }

    public string Path { get; }

    public WorkshopDb Db { get; }

    public static string NewPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"workshop-test-{Guid.NewGuid():N}.db");

    public void Dispose() => File.Delete(Path);
}
