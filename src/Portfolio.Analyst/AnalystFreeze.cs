using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Portfolio.Analyst;

/// <summary>The inputs of the capstone's freeze, as bytes or text (see <see cref="FreezeHash"/>).</summary>
/// <param name="Questions">The raw bytes of <c>capstone/questions.json</c>.</param>
/// <param name="Expected">The raw bytes of <c>capstone/expected.json</c>.</param>
/// <param name="Rules">The raw bytes of the Fabric repo's <c>src/lakehouse/capstone/score.py</c>.</param>
/// <param name="SnapshotTime">The snapshot time, as given.</param>
/// <param name="DataAgent">The data agent folder's bytes, from <see cref="FreezeHash.DataAgentBytes(string)"/>.</param>
/// <param name="AnalystInstructions">The raw bytes of this project's <c>instructions.md</c>.</param>
/// <param name="ModelDirect">The direct route's model name.</param>
/// <param name="ModelFoundry">The Foundry route's model name: this analyst's.</param>
public sealed record FreezeInputs(
    byte[] Questions,
    byte[] Expected,
    byte[] Rules,
    string SnapshotTime,
    byte[] DataAgent,
    byte[] AnalystInstructions,
    string ModelDirect,
    string ModelFoundry);

/// <summary>A freeze: each component's SHA-256, in <see cref="FreezeHash.Components"/> order, the overall hash and its short form.</summary>
public sealed record FreezeHashes(IReadOnlyDictionary<string, string> Components, string Hash, string Short);

/// <summary>A frozen component no longer matches; <see cref="Component"/> names it (<c>hash</c> is the overall hash).</summary>
public sealed class FreezeBrokenException(string component) : Exception($"the freeze is broken: {component} changed")
{
    public string Component { get; } = component;
}

/// <summary>The data agent folder is missing, is not a directory, or holds a link or a junction.</summary>
public sealed class DataAgentException(string message) : Exception(message);

/// <summary>The freeze file is not a freeze; the message is fixed text that names no value.</summary>
public sealed class FreezeFormatException(string message) : Exception(message);

/// <summary>
/// The capstone freeze's hashing, ported byte for byte from the Fabric repo's
/// <c>src/lakehouse/capstone/freeze.py</c>, whose module docstring is the specification; the shared
/// vector <c>tests/vectors/freeze-vector.json</c> pins the two together. Every hash is SHA-256 in
/// lowercase hex:
/// <list type="bullet">
/// <item>files are raw bytes (<see cref="File.ReadAllBytes(string)"/>, never decoded text), so a BOM
/// is hashed like any other bytes;</item>
/// <item>file content is LF-normalised in one pass (<see cref="Lf"/>); names and the snapshot time are
/// hashed as their UTF-8 bytes, with no Unicode normalisation;</item>
/// <item>the data agent folder is every file beneath it, hidden ones too, as <c>path LF content LF</c>
/// in the order of the paths' UTF-8 bytes; a symbolic link or any other reparse point (a junction)
/// is refused;</item>
/// <item>the overall hash is over the lines <c>name=hex LF</c>, sorted; the short hash is its first 12 characters.</item>
/// </list>
/// </summary>
public static class FreezeHash
{
    public const int ShortLength = 12;

    /// <summary>The eight components, in the order <c>verify_freeze</c> checks them.</summary>
    public static IReadOnlyList<string> Components { get; } =
        ["questions", "expected", "rules", "snapshot_time", "data_agent", "analyst_instructions", "model_direct", "model_foundry"];

    // Strict: a string that is not valid UTF-16 has no UTF-8 bytes to hash, as Python's encode refuses it.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Every CR LF and every lone CR becomes one LF, in a single pass; nothing else changes.</summary>
    public static byte[] Lf(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        var n = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == (byte)'\r')
            {
                output[n++] = (byte)'\n';
                if (i + 1 < data.Length && data[i + 1] == (byte)'\n')
                {
                    i++;
                }
            }
            else
            {
                output[n++] = data[i];
            }
        }

        return output[..n];
    }

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>A file's component: the SHA-256 of its LF-normalised raw bytes.</summary>
    public static string OfFile(ReadOnlySpan<byte> raw) => Sha256(Lf(raw));

    /// <summary>A name's component (a model, the snapshot time): the SHA-256 of its UTF-8 bytes, as given.</summary>
    public static string OfText(string text) => Sha256(Utf8.GetBytes(text));

    /// <summary>
    /// The data agent's bytes from its files, each a <c>/</c>-separated relative path and its raw
    /// content: <c>path LF content LF</c> per file, the content LF-normalised, in the order of the
    /// paths' UTF-8 bytes (not <see cref="StringComparer.Ordinal"/>, which puts a supplementary
    /// character's surrogates before U+E000..U+FFFF, the reverse of UTF-8).
    /// </summary>
    public static byte[] DataAgentBytes(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        using var output = new MemoryStream();
        foreach (var (path, content) in files
            .Select(f => (Path: Utf8.GetBytes(f.Key), Content: f.Value))
            .OrderBy(f => f.Path, Utf8Order.Instance))
        {
            output.Write(path);
            output.WriteByte((byte)'\n');
            output.Write(Lf(content));
            output.WriteByte((byte)'\n');
        }

        return output.ToArray();
    }

    /// <summary>
    /// The data agent folder's bytes: every file beneath it, hidden and system files and dot-folders
    /// included. The folder itself may be a link; anything beneath it that is a reparse point (a
    /// symbolic link to a file or a directory, or a junction) is a <see cref="DataAgentException"/>.
    /// </summary>
    public static byte[] DataAgentBytes(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (File.Exists(folder))
        {
            throw new DataAgentException("the data agent folder is not a directory");
        }

        if (!Directory.Exists(folder))
        {
            throw new DataAgentException("the data agent folder does not exist");
        }

        var files = new List<KeyValuePair<string, byte[]>>();
        Walk(new DirectoryInfo(folder), "", files);
        return DataAgentBytes(files);
    }

    /// <summary>The overall hash: the SHA-256 of the lines <c>name=hex LF</c>, sorted by their UTF-8 bytes.</summary>
    public static string Overall(IReadOnlyDictionary<string, string> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var lines = components.Select(c => Utf8.GetBytes($"{c.Key}={c.Value}\n")).OrderBy(l => l, Utf8Order.Instance);
        return Sha256(lines.SelectMany(l => l).ToArray());
    }

    /// <summary>The whole freeze, as <c>build_freeze</c> builds it.</summary>
    public static FreezeHashes Build(FreezeInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var components = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["questions"] = OfFile(inputs.Questions),
            ["expected"] = OfFile(inputs.Expected),
            ["rules"] = OfFile(inputs.Rules),
            ["snapshot_time"] = OfText(inputs.SnapshotTime),
            ["data_agent"] = Sha256(inputs.DataAgent),
            ["analyst_instructions"] = OfFile(inputs.AnalystInstructions),
            ["model_direct"] = OfText(inputs.ModelDirect),
            ["model_foundry"] = OfText(inputs.ModelFoundry),
        };
        var overall = Overall(components);
        return new FreezeHashes(components, overall, overall[..ShortLength]);
    }

    private static void Walk(DirectoryInfo directory, string prefix, List<KeyValuePair<string, byte[]>> files)
    {
        // AttributesToSkip = 0: hidden and system files count. Not recursive: each directory is
        // checked for being a reparse point before anything beneath it is read.
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, MatchType = MatchType.Simple };
        foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
        {
            var relative = prefix + entry.Name;
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
            {
                throw new DataAgentException($"the data agent folder holds a symbolic link or junction: {relative}");
            }

            if (entry is DirectoryInfo sub)
            {
                Walk(sub, relative + "/", files);
            }
            else
            {
                files.Add(new(relative, File.ReadAllBytes(entry.FullName)));
            }
        }
    }

    /// <summary>Byte arrays in ordinal byte order: UTF-8 bytes sort as their code points do.</summary>
    private sealed class Utf8Order : IComparer<byte[]>
    {
        public static readonly Utf8Order Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}

/// <summary>
/// The analyst's check of the freeze (Review Focus 3): before a run it recomputes the three
/// components it owns, the questions it asks, its instructions and its model, exactly as
/// <c>freeze.py</c> does, and refuses on the first that differs. The other five are the Fabric
/// repo's to recompute; here each must be present, and the overall hash and the short hash must be
/// those of the components as frozen, so the short hash a record carries is the freeze's own.
/// </summary>
public static partial class AnalystFreeze
{
    /// <summary>The components the analyst recomputes, in <see cref="FreezeHash.Components"/> order.</summary>
    public static IReadOnlyList<string> Verified { get; } = ["questions", "analyst_instructions", "model_foundry"];

    /// <summary>
    /// Checks <paramref name="freezeJson"/> (the text of <c>freeze.json</c>) against the questions'
    /// and the instructions' raw bytes and the Foundry model's name. Components are checked in
    /// <see cref="FreezeHash.Components"/> order, then any unknown one, then the overall hash.
    /// </summary>
    /// <returns>The freeze's short hash.</returns>
    /// <exception cref="FreezeBrokenException">Names the first component that differs, or <c>hash</c>.</exception>
    /// <exception cref="FreezeFormatException">The text is not a freeze object.</exception>
    public static string Verify(string freezeJson, byte[] questions, byte[] instructions, string model)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(model);
        var (frozen, hash, shortHash) = Parse(freezeJson);

        var recomputed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["questions"] = FreezeHash.OfFile(questions),
            ["analyst_instructions"] = FreezeHash.OfFile(instructions),
            ["model_foundry"] = FreezeHash.OfText(model),
        };
        foreach (var name in FreezeHash.Components)
        {
            var given = frozen.GetValueOrDefault(name);
            var matches = recomputed.TryGetValue(name, out var now) ? given == now : given is not null && Sha256Hex().IsMatch(given);
            if (!matches)
            {
                throw new FreezeBrokenException(name);
            }
        }

        if (frozen.Keys.Where(n => !FreezeHash.Components.Contains(n)).Order(StringComparer.Ordinal).FirstOrDefault() is { } unknown)
        {
            throw new FreezeBrokenException(unknown);
        }

        var overall = FreezeHash.Overall(frozen);
        if (hash != overall || shortHash != overall[..FreezeHash.ShortLength])
        {
            throw new FreezeBrokenException("hash");
        }

        return shortHash;
    }

    private static (Dictionary<string, string> Components, string? Hash, string? Short) Parse(string freezeJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(freezeJson ?? "");
        }
        catch (JsonException)
        {
            throw new FreezeFormatException("--freeze: not valid JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("components", out var components)
                || components.ValueKind != JsonValueKind.Object
                || components.EnumerateObject().Any(c => c.Value.ValueKind != JsonValueKind.String))
            {
                throw new FreezeFormatException("--freeze: not a freeze object (components, hash and short)");
            }

            // A repeated name keeps its last value, as Python's json.loads does.
            var frozen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var component in components.EnumerateObject())
            {
                frozen[component.Name] = component.Value.GetString()!;
            }

            return (
                frozen,
                Text(root, "hash"),
                Text(root, "short"));
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();
}
