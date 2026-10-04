using System.Text.Json;

namespace Workshop.Surface;

/// <summary>
/// The append-only record of every action the agent asked for: one JSON object per line, with the
/// time, the action's type, its target, its value and its outcome. A value cannot break a line,
/// because JSON escapes line breaks.
/// </summary>
public sealed class AuditLog
{
    private readonly Lock gate = new();

    public AuditLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    /// <summary>Appends one line: <c>{"at","type","target","value","outcome"}</c>.</summary>
    public void Append(SurfaceAction action, string outcome)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        var line = Line(action, outcome, DateTimeOffset.UtcNow);

        lock (gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(line);
        }
    }

    private static byte[] Line(SurfaceAction action, string outcome, DateTimeOffset at)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("at", at);
            json.WriteString("type", action.Type);
            json.WriteString("target", TargetOf(action));
            json.WriteString("value", action.Value);
            json.WriteString("outcome", outcome);
            json.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>What the action is aimed at: a screen, a field, a list and its row as <c>list/row</c>, or a button.</summary>
    private static string? TargetOf(SurfaceAction action) => action.Type switch
    {
        ActionTypes.Open => action.Screen,
        ActionTypes.Set => action.Field,
        ActionTypes.Select => $"{action.List}/{action.Row}",
        ActionTypes.Press => action.Button,
        _ => null,
    };
}
