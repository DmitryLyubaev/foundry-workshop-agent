using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Workshop.Agent.Runner;

/// <summary>
/// Applies <see cref="Redaction"/> again to transcripts already written: for a study whose
/// transcripts were written before a redaction fix, so that they can pass the scan without being
/// run again or edited by hand (runbook step 8.1). Only <c>infraMessage</c> and <c>error</c> are
/// touched, the two fields that hold a failure's text; everything else in the file stays byte for
/// byte. Each file changed is logged, by name and field, never with the text replaced.
/// </summary>
public static class TranscriptRedaction
{
    /// <summary>The log, beside the transcripts and published with them.</summary>
    public const string LogFileName = "redactions.md";

    private static readonly string[] Fields = ["infraMessage", "error"];

    /// <summary>
    /// Re-redacts every transcript under <paramref name="dir"/>, its subdirectories too (a study's
    /// <c>repeats</c>), and appends a line to <see cref="LogFileName"/> for each one it changed,
    /// before it rewrites that file. Nothing is written until every file has parsed.
    /// A JSON file that is not a transcript, such as <c>eval-scores.json</c>, is left alone.
    /// </summary>
    /// <param name="today">The date each log line carries.</param>
    /// <returns>How many transcripts changed, of how many there are.</returns>
    /// <exception cref="JsonException">A <c>*.json</c> file is not JSON; nothing has been written.</exception>
    public static (int Changed, int Transcripts) Apply(string dir, DateOnly today)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        var files = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
            .Select(f => (Path: f, Relative: Path.GetRelativePath(dir, f).Replace('\\', '/')))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToList();

        // Every file is read and parsed before any is written: a file that is not JSON stops the
        // command with nothing rewritten, rather than with earlier files rewritten and never logged
        // (a second run would find nothing to change, so the edit would go unlogged for good).
        var rewrites = new List<(string Path, JsonObject Root, string Line)>();
        var transcripts = 0;
        foreach (var (path, relative) in files)
        {
            if (JsonNode.Parse(File.ReadAllBytes(path)) is not JsonObject root || !IsTranscript(root))
            {
                continue;
            }

            transcripts++;
            var changed = new List<string>();
            foreach (var field in Fields)
            {
                if (root[field] is JsonValue value && value.TryGetValue<string>(out var text) && Redaction.Redact(text) is { } redacted && redacted != text)
                {
                    root[field] = redacted;
                    changed.Add(field);
                }
            }

            if (changed.Count > 0)
            {
                rewrites.Add((path, root, string.Create(CultureInfo.InvariantCulture, $"- {today:yyyy-MM-dd}: {relative}: {string.Join(", ", changed)} re-redacted by Workshop.Agent transcripts redact.")));
            }
        }

        var log = Path.Combine(dir, LogFileName);
        foreach (var (path, root, line) in rewrites)
        {
            // The line goes in before the write: a write that then fails leaves a line for an edit
            // a second run makes (and logs) again, never an edit without a line.
            File.AppendAllText(log, line + "\n", new UTF8Encoding(false));

            // Written as the runner writes a transcript, so only the changed fields differ.
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(root.ToJsonString(Transcript.Json)));
        }

        return (rewrites.Count, transcripts);
    }

    private static bool IsTranscript(JsonObject root) =>
        root["scenarioId"] is JsonValue id && id.TryGetValue<string>(out _)
        && root.ContainsKey("engine")
        && root.ContainsKey("infraMessage");
}
