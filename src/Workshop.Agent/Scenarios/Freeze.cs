using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Workshop.Agent.Engines;
using Workshop.Agent.Tools;

namespace Workshop.Agent.Scenarios;

/// <summary>The study's pre-registered decision rule: written into the freeze, and checked against it.</summary>
/// <param name="Threshold">A difference in task success counts only at this size or more, either way.</param>
/// <param name="Seed">The bootstrap's seed.</param>
/// <param name="Resamples">The bootstrap's resamples.</param>
/// <param name="Passes">The passes of every scenario in a study run.</param>
public sealed record DecisionRule(double Threshold, int Seed, int Resamples, int Passes)
{
    /// <summary>The rule as pre-registered: 0.10, seed 20261004, 10,000 resamples, 3 passes.</summary>
    public static DecisionRule PreRegistered { get; } = new(0.10, 20261004, 10_000, 3);
}

/// <summary>The freeze no longer matches what a study run would use.</summary>
public sealed class FreezeBrokenException(string message) : Exception(message)
{
}

/// <summary>
/// The freeze (<c>freeze.json</c> beside the scenarios): a SHA-256 for each scenario file and for
/// the instructions, the settings and the tools, with the decision rule, taken once before the study.
/// A study run verifies it first and refuses to start on the first thing that differs, so the study
/// measures what was pre-registered and nothing edited since.
/// </summary>
public static class Freeze
{
    public const string FileName = "freeze.json";

    // The short hash names a study's output directory: enough to tell one freeze from another.
    private const int ShortHashLength = 12;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        NewLine = "\n",
    };

    /// <summary>
    /// Writes <c>freeze.json</c> into <paramref name="dir"/> for the scenarios in it, which must load.
    /// </summary>
    /// <param name="frozenOn">The date written; today (UTC) when null.</param>
    /// <returns>The path written.</returns>
    /// <exception cref="InvalidDataException">The scenarios are not valid, or <paramref name="dir"/> already holds a freeze: a freeze is not rewritten by accident, so delete it to freeze again.</exception>
    public static string Write(string dir, DateOnly? frozenOn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        var path = Path.Combine(dir, FileName);
        // Valid first: a set the loader refuses is not worth freezing.
        _ = ScenarioLoader.LoadAll(dir);
        if (File.Exists(path))
        {
            throw new InvalidDataException($"'{path}' already exists. A freeze is not rewritten: delete it first to freeze again.");
        }

        var freeze = new FreezeFile
        {
            FrozenOn = (frozenOn ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Scenarios = ScenarioHashes(dir),
            InstructionsSha256 = AgentInstructions.Sha256,
            SettingsSha256 = AgentSettings.Sha256,
            ToolsSha256 = ToolSchemas.Sha256(WorkshopTools.Declarations),
            DecisionRule = DecisionRule.PreRegistered,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(freeze, Json) + "\n", new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// Checks the scenario files in <paramref name="dir"/> and the code's instructions, settings and
    /// tools against the freeze there. Null when all match; otherwise the first thing that does not,
    /// as a sentence: scenarios first, in file-name order, then the instructions, the settings, the
    /// tools and the decision rule.
    /// </summary>
    public static string? Verify(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path))
        {
            return $"There is no {FileName} in '{dir}': freeze the scenarios first (Workshop.Agent scenarios freeze <dir>).";
        }

        FreezeFile? frozen;
        try
        {
            frozen = JsonSerializer.Deserialize<FreezeFile>(File.ReadAllText(path), Json);
        }
        catch (JsonException e)
        {
            return $"The freeze is broken: {FileName} cannot be read: {e.Message}";
        }

        if (frozen?.Scenarios is null || frozen.InstructionsSha256 is null || frozen.SettingsSha256 is null || frozen.ToolsSha256 is null || frozen.DecisionRule is null)
        {
            return $"The freeze is broken: {FileName} is missing a field.";
        }

        var now = ScenarioHashes(dir);
        foreach (var name in frozen.Scenarios.Keys.Union(now.Keys).Order(StringComparer.Ordinal))
        {
            var frozenHash = frozen.Scenarios.GetValueOrDefault(name);
            var nowHash = now.GetValueOrDefault(name);
            if (frozenHash is null)
            {
                return $"The freeze is broken: {name} is not in the freeze; it was added since.";
            }

            if (nowHash is null)
            {
                return $"The freeze is broken: {name} is in the freeze but not in '{dir}'; it was removed since.";
            }

            if (frozenHash != nowHash)
            {
                return $"The freeze is broken: {name} was edited since the freeze (frozen {Short(frozenHash)}, now {Short(nowHash)}).";
            }
        }

        return Differs("the instructions", frozen.InstructionsSha256, AgentInstructions.Sha256)
            ?? Differs("the settings", frozen.SettingsSha256, AgentSettings.Sha256)
            ?? Differs("the tools", frozen.ToolsSha256, ToolSchemas.Sha256(WorkshopTools.Declarations))
            ?? (frozen.DecisionRule == DecisionRule.PreRegistered
                ? null
                : "The freeze is broken: the decision rule in the freeze is not the pre-registered one (threshold 0.10, seed 20261004, 10000 resamples, 3 passes).");
    }

    /// <summary>The freeze's short hash: the first characters of the SHA-256 of <c>freeze.json</c>'s bytes.</summary>
    public static string ShortHash(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, FileName))))[..ShortHashLength];
    }

    // Raw bytes: .gitattributes pins the scenarios to LF, so a checkout cannot change a hash, and any edit does.
    private static SortedDictionary<string, string> ScenarioHashes(string dir) =>
        new(
            ScenarioLoader.Files(dir).ToDictionary(f => Path.GetFileName(f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static string? Differs(string what, string frozen, string now) =>
        frozen == now ? null : $"The freeze is broken: {what} differ from the freeze (frozen {Short(frozen)}, now {Short(now)}).";

    private static string Short(string hash) => hash.Length > ShortHashLength ? hash[..ShortHashLength] : hash;

    // The file's shape; also what the Python analysis reads.
    private sealed class FreezeFile
    {
        public string? FrozenOn { get; set; }

        public SortedDictionary<string, string>? Scenarios { get; set; }

        public string? InstructionsSha256 { get; set; }

        public string? SettingsSha256 { get; set; }

        public string? ToolsSha256 { get; set; }

        public DecisionRule? DecisionRule { get; set; }
    }
}
