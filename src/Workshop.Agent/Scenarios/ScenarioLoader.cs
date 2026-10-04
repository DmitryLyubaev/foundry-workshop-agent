using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workshop.Agent.Scenarios;

/// <summary>
/// Reads a directory of scenario files, <c>&lt;id&gt;.json</c>, in file-name order, and refuses the
/// whole set on the first problem, naming its file.
/// </summary>
public static class ScenarioLoader
{
    /// <summary>The workshop database's tables: what <see cref="Expectation.UnchangedExcept"/> may name.</summary>
    public static readonly string[] Tables = ["customers", "devices", "parts", "jobs", "job_parts", "notes"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <exception cref="InvalidDataException">A file is not a valid scenario, or the set is empty or repeats an ID.</exception>
    public static Scenario[] LoadAll(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        var files = Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException($"There are no scenario files (*.json) in '{dir}'.");
        }

        var scenarios = files.Select(Load).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scenario, file) in scenarios.Zip(files))
        {
            if (!seen.Add(scenario.Id))
            {
                throw Bad(file, $"repeats the ID '{scenario.Id}'.");
            }
        }

        return scenarios;
    }

    private static Scenario Load(string file)
    {
        ScenarioFile? read;
        try
        {
            read = JsonSerializer.Deserialize<ScenarioFile>(File.ReadAllText(file), Json);
        }
        catch (JsonException e)
        {
            throw Bad(file, $"is not a scenario: {e.Message}", e);
        }

        if (read is null)
        {
            throw Bad(file, "is not a scenario: it is null.");
        }

        var id = Path.GetFileNameWithoutExtension(file);
        if (read.Id != id)
        {
            throw Bad(file, $"has the id '{read.Id}', which does not match its file name.");
        }

        if (!Categories.All.Contains(read.Category, StringComparer.Ordinal))
        {
            throw Bad(file, $"has the category '{read.Category}', which is not one of: {string.Join(", ", Categories.All)}.");
        }

        if (string.IsNullOrWhiteSpace(read.Task))
        {
            throw Bad(file, "has no task.");
        }

        if (read.Gate is not (null or GateAnswers.Approve or GateAnswers.Deny))
        {
            throw Bad(file, $"has the gate '{read.Gate}', which is not approve, deny or null.");
        }

        var setup = read.Setup ?? [];
        if (setup.Any(string.IsNullOrWhiteSpace))
        {
            throw Bad(file, "has an empty setup statement.");
        }

        var expect = read.Expect ?? throw Bad(file, "has no expect.");
        return new Scenario(id, read.Category!, read.Task, setup, read.Gate, ReadExpectation(file, expect));
    }

    private static Expectation ReadExpectation(string file, ExpectFile expect)
    {
        var checks = (expect.Checks ?? []).Select((check, i) =>
        {
            // Setup may write; a check never may.
            if (!ReadOnlySql.IsSingleSelect(check?.Sql))
            {
                throw Bad(file, $"has check {i + 1}, whose SQL is not a single read-only SELECT: {check?.Sql}");
            }

            return new Check(check!.Sql!, check.Expected ?? throw Bad(file, $"has check {i + 1} with no equals."));
        }).ToArray();

        var unchanged = expect.Unchanged ?? false;
        var except = expect.UnchangedExcept ?? [];
        if (except.Length > 0 && !unchanged)
        {
            throw Bad(file, "has unchangedExcept without unchanged: true.");
        }

        if (except.FirstOrDefault(t => !Tables.Contains(t, StringComparer.Ordinal)) is { } unknown)
        {
            throw Bad(file, $"has the table '{unknown}' in unchangedExcept, which is not one of: {string.Join(", ", Tables)}.");
        }

        var reply = expect.ReplyContains ?? [];
        if (reply.Any(string.IsNullOrEmpty))
        {
            throw Bad(file, "has an empty text in replyContains.");
        }

        return new Expectation(checks, unchanged, reply, [.. except.Distinct(StringComparer.Ordinal)]);
    }

    private static InvalidDataException Bad(string file, string problem, Exception? inner = null) =>
        new($"The scenario file {Path.GetFileName(file)} {problem}", inner);

    // The file's shape. Everything is nullable so a missing property gets its own message.
    private sealed class ScenarioFile
    {
        public string? Id { get; set; }

        public string? Category { get; set; }

        public string? Task { get; set; }

        public string[]? Setup { get; set; }

        public string? Gate { get; set; }

        public ExpectFile? Expect { get; set; }
    }

    private sealed class ExpectFile
    {
        public CheckFile?[]? Checks { get; set; }

        public bool? Unchanged { get; set; }

        public string[]? ReplyContains { get; set; }

        public string[]? UnchangedExcept { get; set; }
    }

    private sealed class CheckFile
    {
        public string? Sql { get; set; }

        // The file calls it equals, which a C# member cannot be named (see Check).
        [JsonPropertyName("equals")]
        public string? Expected { get; set; }
    }
}
