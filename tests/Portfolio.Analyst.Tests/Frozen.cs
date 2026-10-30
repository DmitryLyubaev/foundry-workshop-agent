using System.Text;
using System.Text.Json.Nodes;

namespace Portfolio.Analyst.Tests;

/// <summary>
/// A frozen capstone in a temp directory: <c>questions.json</c> and the <c>freeze.json</c>
/// <c>capstone_freeze.py freeze</c> would write for it, over the analyst's real instructions.
/// </summary>
internal sealed class Frozen : IDisposable
{
    public const string Model = "gpt-5.6-luna";
    public const string McpUrl = "https://mcp-sentinel.example.invalid/v1/workspaces/ws-sentinel/dataagents/da-sentinel/mcp";
    public const string ConnectionId = "conn-sentinel-capstone";
    public const string ProjectEndpoint = "https://project-sentinel.services.ai.azure.com/api/projects/p-sentinel";
    public const string Guid = "0f8fad5b-d9cb-469f-a165-70867728950e";

    /// <summary>Every value no output may hold.</summary>
    public static readonly string[] Secrets = [McpUrl, "mcp-sentinel", ConnectionId, "conn-sentinel", ProjectEndpoint, "project-sentinel", Guid];

    private readonly TempDir dir = TempDir.Create();

    public Frozen(JsonArray? questions = null)
    {
        Questions = questions ?? TwoQuestions();
        QuestionsPath = Path.Combine(dir.Path, "questions.json");
        // CRLF, as a Windows checkout may have it: the freeze hashes it LF-normalised.
        File.WriteAllBytes(QuestionsPath, Encoding.UTF8.GetBytes(Questions.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }).Replace("\n", "\r\n", StringComparison.Ordinal)));
        var built = FreezeHash.Build(new FreezeInputs(
            Questions: File.ReadAllBytes(QuestionsPath),
            Expected: "{}\n"u8.ToArray(),
            Rules: "rules\n"u8.ToArray(),
            SnapshotTime: "2026-11-08T00:00:00Z",
            DataAgent: [],
            AnalystInstructions: TestFiles.Instructions(),
            ModelDirect: Model,
            ModelFoundry: Model));
        Short = built.Short;
        FreezePath = Path.Combine(dir.Path, "freeze.json");
        var components = new JsonObject();
        foreach (var (name, hex) in built.Components)
        {
            components[name] = hex;
        }

        File.WriteAllText(FreezePath, new JsonObject { ["components"] = components, ["hash"] = built.Hash, ["short"] = built.Short }.ToJsonString() + "\n");
        Out = Path.Combine(dir.Path, "out");
    }

    public JsonArray Questions { get; }

    public string QuestionsPath { get; }

    public string FreezePath { get; }

    public string Short { get; }

    public string Out { get; }

    /// <summary>The environment of a live run: the sentinels, nothing real.</summary>
    public static Func<string, string?> Environment(params (string Name, string? Value)[] overrides) => name =>
    {
        foreach (var (n, v) in overrides)
        {
            if (n == name)
            {
                return v;
            }
        }

        return name switch
        {
            "ANALYST_MCP_URL" => McpUrl,
            "ANALYST_CONNECTION_ID" => ConnectionId,
            "FWA_PROJECT_ENDPOINT" => ProjectEndpoint,
            _ => null,
        };
    };

    public static JsonArray TwoQuestions() =>
    [
        new JsonObject { ["id"] = "q01", ["text"] = "How much has project p2 spent in total, in its billing currency?", ["kind"] = "money" },
        new JsonObject { ["id"] = "q15", ["text"] = "How did the success rate of gpt-5.6-luna change from one model version to the next?", ["kind"] = "unanswerable", ["source"] = null },
    ];

    /// <summary>A scripted model that replies with <paramref name="replies"/> in turn, and keeps what it was sent.</summary>
    public static RecordingClient Scripted(params string[] replies) => RecordingClient.Replying(replies);

    public string[] RunArgs(int passes = 1) =>
        ["run", "--freeze", FreezePath, "--questions", QuestionsPath, "--passes", passes.ToString(System.Globalization.CultureInfo.InvariantCulture), "--out", Out, "--model", Model];

    public string[] OutFiles() => Directory.Exists(Out) ? [.. Directory.GetFiles(Out).Select(f => Path.GetFileName(f))] : [];

    public JsonObject[] Records() =>
        [.. File.ReadAllLines(Assert.Single(Directory.GetFiles(Out, "*.jsonl"))).Select(l => JsonNode.Parse(l)!.AsObject())];

    public void Dispose() => dir.Dispose();
}
