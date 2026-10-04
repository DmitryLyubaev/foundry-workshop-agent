using System.Text.Json;

namespace Workshop.Agent.Engines;

/// <summary>One answer of the scripted model.</summary>
public abstract record ScriptStep
{
    private ScriptStep()
    {
    }

    /// <summary><c>{ "call": "&lt;tool&gt;", "args": {…} }</c>: the model asks for one tool call.</summary>
    public sealed record Call(string Tool, IReadOnlyDictionary<string, JsonElement> Args) : ScriptStep;

    /// <summary><c>{ "reply": "&lt;text&gt;" }</c>: the model's final reply.</summary>
    public sealed record Reply(string Text) : ScriptStep;

    /// <summary><c>{ "throttle": &lt;seconds&gt; }</c>: the call is throttled, retry after this long.</summary>
    public sealed record Throttle(TimeSpan RetryAfter) : ScriptStep;

    /// <summary><c>{ "filter": true }</c>: the content filter blocks the answer.</summary>
    public sealed record Filter : ScriptStep;
}

/// <summary>
/// The scripted model's answers, in order: a JSON array with one object per step, each holding
/// exactly one of <c>call</c> (with optional <c>args</c>), <c>reply</c>, <c>throttle</c> or <c>filter</c>.
/// </summary>
public sealed class Script
{
    public Script(IReadOnlyList<ScriptStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        Steps = [.. steps];
    }

    public IReadOnlyList<ScriptStep> Steps { get; }

    public static Script Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Reads a script, or throws <see cref="FormatException"/> naming the first bad step.</summary>
    public static Script Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            throw new FormatException($"The script is not JSON: {e.Message}", e);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("A script is a JSON array of steps.");
            }

            return new Script([.. document.RootElement.EnumerateArray().Select((step, i) => ReadStep(step, i + 1))]);
        }
    }

    private static ScriptStep ReadStep(JsonElement step, int number)
    {
        if (step.ValueKind != JsonValueKind.Object)
        {
            throw Bad(number, "is not an object");
        }

        var keys = step.EnumerateObject().Select(p => p.Name).ToArray();
        var kinds = keys.Where(k => k is "call" or "reply" or "throttle" or "filter").ToArray();
        if (kinds.Length != 1)
        {
            throw Bad(number, "needs exactly one of call, reply, throttle or filter");
        }

        var allowed = kinds[0] == "call" ? new[] { "call", "args" } : kinds;
        if (keys.FirstOrDefault(k => !allowed.Contains(k)) is { } extra)
        {
            throw Bad(number, $"has an unknown key '{extra}'");
        }

        var value = step.GetProperty(kinds[0]);
        return kinds[0] switch
        {
            "call" => ReadCall(step, value, number),
            "reply" when value.ValueKind == JsonValueKind.String => new ScriptStep.Reply(value.GetString()!),
            "throttle" when value.ValueKind == JsonValueKind.Number && value.GetDouble() >= 0 => new ScriptStep.Throttle(TimeSpan.FromSeconds(value.GetDouble())),
            "filter" when value.ValueKind == JsonValueKind.True => new ScriptStep.Filter(),
            "reply" => throw Bad(number, "has a reply that is not a string"),
            "throttle" => throw Bad(number, "has a throttle that is not a number of seconds, 0 or more"),
            _ => throw Bad(number, "has a filter that is not true"),
        };
    }

    private static ScriptStep.Call ReadCall(JsonElement step, JsonElement tool, int number)
    {
        if (tool.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tool.GetString()))
        {
            throw Bad(number, "has a call that is not a tool name");
        }

        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (step.TryGetProperty("args", out var given))
        {
            if (given.ValueKind != JsonValueKind.Object)
            {
                throw Bad(number, "has args that are not an object");
            }

            foreach (var arg in given.EnumerateObject())
            {
                // Cloned, so the step outlives the document it was read from.
                args[arg.Name] = arg.Value.Clone();
            }
        }

        return new ScriptStep.Call(tool.GetString()!, args);
    }

    private static FormatException Bad(int number, string problem) => new($"The script's step {number} {problem}.");
}
