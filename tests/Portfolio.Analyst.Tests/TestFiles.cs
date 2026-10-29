using System.Text;
using System.Text.Json.Nodes;

namespace Portfolio.Analyst.Tests;

/// <summary>The contract files copied beside the tests, and the analyst's instructions as built.</summary>
internal static class TestFiles
{
    /// <summary><c>tests/vectors/freeze-vector.json</c>: the Fabric repo's shared freeze vector, byte for byte.</summary>
    public static JsonObject Vector() =>
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "freeze-vector.json")))!.AsObject();

    /// <summary>The Fabric repo's <c>record.schema.json</c>, byte for byte.</summary>
    public static JsonObject RecordSchema() =>
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "record.schema.json")))!.AsObject();

    /// <summary><c>src/Portfolio.Analyst/instructions.md</c>, as copied beside the analyst: raw bytes.</summary>
    public static byte[] Instructions() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "instructions.md"));

    /// <summary>An inline vector input: the JSON string decoded, then encoded as UTF-8.</summary>
    public static byte[] Utf8(JsonNode? node) => new UTF8Encoding(false, true).GetBytes(node!.GetValue<string>());
}

/// <summary>A fresh directory under the temp directory, deleted afterwards.</summary>
internal sealed class TempDir : IDisposable
{
    private TempDir(string path) => Path = path;

    public string Path { get; }

    public static TempDir Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PortfolioAnalystTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempDir(path);
    }

    public string File(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    public void Dispose()
    {
        try
        {
            // Junctions and links first, so a recursive delete never follows one out of the directory.
            foreach (var entry in new DirectoryInfo(Path).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    entry.Delete();
                }
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp directory left behind is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
