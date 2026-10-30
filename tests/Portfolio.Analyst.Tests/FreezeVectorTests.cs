using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Portfolio.Analyst.Tests;

/// <summary>
/// The freeze's hashes agree with the Fabric repo's <c>lakehouse.capstone.freeze</c>, byte for byte:
/// every component, the overall hash and the short hash of the shared vector
/// (<c>tests/vectors/freeze-vector.json</c>, committed in both repos) are rebuilt from its inline inputs.
/// </summary>
public sealed class FreezeVectorTests
{
    private static readonly JsonObject Vector = TestFiles.Vector();
    private static readonly JsonObject Inputs = Vector["inputs"]!.AsObject();
    private static readonly JsonObject Expected = Vector["expected"]!.AsObject();

    private static string ExpectedComponent(string name) => Expected["components"]![name]!.GetValue<string>();

    private static Dictionary<string, byte[]> DataAgentFiles(Func<string, bool>? keep = null) =>
        Inputs["data_agent"]!.AsObject()
            .Where(f => keep?.Invoke(f.Key) ?? true)
            .ToDictionary(f => f.Key, f => TestFiles.Utf8(f.Value), StringComparer.Ordinal);

    private static FreezeInputs VectorInputs() => new(
        Questions: TestFiles.Utf8(Inputs["questions"]),
        Expected: TestFiles.Utf8(Inputs["expected"]),
        Rules: TestFiles.Utf8(Inputs["rules"]),
        SnapshotTime: Inputs["snapshot_time"]!.GetValue<string>(),
        DataAgent: FreezeHash.DataAgentBytes(DataAgentFiles()),
        AnalystInstructions: TestFiles.Utf8(Inputs["analyst_instructions"]),
        ModelDirect: Inputs["model_direct"]!.GetValue<string>(),
        ModelFoundry: Inputs["model_foundry"]!.GetValue<string>());

    [Fact]
    public void Every_component_matches_the_vector()
    {
        var built = FreezeHash.Build(VectorInputs());

        Assert.Equal(FreezeHash.Components, built.Components.Keys);
        foreach (var name in FreezeHash.Components)
        {
            Assert.True(ExpectedComponent(name) == built.Components[name], $"component {name} differs from the vector");
        }

        Assert.Equal(Expected["components"]!.AsObject().Select(c => c.Key).Order(StringComparer.Ordinal), built.Components.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_overall_hash_and_the_short_hash_match_the_vector()
    {
        var built = FreezeHash.Build(VectorInputs());

        Assert.Equal(Expected["hash"]!.GetValue<string>(), built.Hash);
        Assert.Equal(Expected["short"]!.GetValue<string>(), built.Short);
        Assert.Equal(12, built.Short.Length);
    }

    [Fact]
    public void The_overall_hash_is_over_the_sorted_name_hex_lines()
    {
        var components = Expected["components"]!.AsObject().ToDictionary(c => c.Key, c => c.Value!.GetValue<string>(), StringComparer.Ordinal);
        var lines = string.Concat(components.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}\n"));

        Assert.Equal(FreezeHash.Sha256(Encoding.UTF8.GetBytes(lines)), FreezeHash.Overall(components));
        Assert.Equal(Expected["hash"]!.GetValue<string>(), FreezeHash.Overall(components));
    }

    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\r\r\nb", "a\n\nb")]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r", "a\n")]
    [InlineData("\r\n\r\n", "\n\n")]
    [InlineData("\n\r", "\n\n")]
    [InlineData("no line break", "no line break")]
    [InlineData("", "")]
    public void Line_breaks_become_lf_in_one_pass(string raw, string normalised)
    {
        Assert.Equal(Encoding.UTF8.GetBytes(normalised), FreezeHash.Lf(Encoding.UTF8.GetBytes(raw)));
    }

    [Fact]
    public void A_bom_is_hashed_as_it_is()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n'];

        Assert.NotEqual(FreezeHash.OfFile(withBom), FreezeHash.OfFile("a\n"u8.ToArray()));
        Assert.Equal(FreezeHash.Sha256(withBom), FreezeHash.OfFile(withBom));
    }

    [Fact]
    public void Paths_sort_by_their_utf8_bytes_not_by_utf16_code_units()
    {
        // U+FF5E is EF BD 9E in UTF-8; U+1D518 is F0 9D 94 98. UTF-16 puts the surrogate (D835) first.
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["unicode/\U0001D518.md"] = "s\n"u8.ToArray(),
            ["unicode/～.md"] = "b\n"u8.ToArray(),
            ["a/b.md"] = [],
            ["a.json"] = [],
        };

        var text = Encoding.UTF8.GetString(FreezeHash.DataAgentBytes(files));

        Assert.Equal("a.json\n\na/b.md\n\nunicode/～.md\nb\n\nunicode/\U0001D518.md\ns\n\n", text);
        Assert.True(string.CompareOrdinal("unicode/\U0001D518.md", "unicode/～.md") < 0, "UTF-16 order is the reverse, which is why it is not used");
    }

    [Fact]
    public void An_empty_data_agent_hashes_the_empty_byte_string()
    {
        using var dir = TempDir.Create();
        Directory.CreateDirectory(Path.Combine(dir.Path, "empty", "deeper"));

        Assert.Empty(FreezeHash.DataAgentBytes(dir.Path));
    }

    [Fact]
    public void The_folder_walk_hashes_as_the_vector_says_hidden_files_included()
    {
        // "Readme.md" and "readme.md" cannot both exist where names are case-insensitive, so this walk
        // leaves "readme.md" out; the exact vector hash of a walk is the next test's.
        using var dir = TempDir.Create();
        var files = DataAgentFiles(name => name != "readme.md");
        Write(dir.Path, files);
        var hidden = new FileInfo(Path.Combine(dir.Path, ".gitignore"));
        hidden.Attributes |= FileAttributes.Hidden;
        var hiddenDir = new DirectoryInfo(Path.Combine(dir.Path, ".config"));
        hiddenDir.Attributes |= FileAttributes.Hidden;

        Assert.Equal(FreezeHash.DataAgentBytes(files), FreezeHash.DataAgentBytes(dir.Path));
    }

    [Fact]
    public void The_folder_walk_of_the_whole_vector_matches_its_data_agent_hash()
    {
        using var dir = TempDir.Create();
        Assert.SkipUnless(CaseSensitive(dir.Path), "This file system cannot make a directory case-sensitive.");
        Write(dir.Path, DataAgentFiles());

        Assert.Equal(ExpectedComponent("data_agent"), FreezeHash.Sha256(FreezeHash.DataAgentBytes(dir.Path)));
    }

    [Fact]
    public void A_junction_in_the_data_agent_folder_is_rejected()
    {
        using var dir = TempDir.Create();
        dir.File("a.json", "{}\n");
        var target = Directory.CreateDirectory(Path.Combine(dir.Path, "target")).FullName;
        var junction = Path.Combine(dir.Path, "sub", "linked");
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        Assert.SkipUnless(Run("cmd.exe", "/c", "mklink", "/J", junction, target), "mklink /J is not available.");

        var e = Assert.Throws<DataAgentException>(() => FreezeHash.DataAgentBytes(dir.Path));
        Assert.Contains("sub/linked", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_symbolic_link_in_the_data_agent_folder_is_rejected()
    {
        using var dir = TempDir.Create();
        var target = dir.File("a.json", "{}\n");
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir.Path, "sub", "link.json"), target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links need Developer Mode or elevation here.");
        }

        var error = Assert.Throws<DataAgentException>(() => FreezeHash.DataAgentBytes(dir.Path));
        Assert.Contains("sub/link.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_folder_or_a_file_is_an_error_not_an_empty_hash()
    {
        using var dir = TempDir.Create();
        var file = dir.File("a.json", "{}");

        Assert.Throws<DataAgentException>(() => FreezeHash.DataAgentBytes(Path.Combine(dir.Path, "missing")));
        Assert.Throws<DataAgentException>(() => FreezeHash.DataAgentBytes(file));
    }

    private static void Write(string root, Dictionary<string, byte[]> files)
    {
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
    }

    /// <summary>Makes <paramref name="dir"/> case-sensitive (its new subdirectories inherit it); false when that cannot be done.</summary>
    private static bool CaseSensitive(string dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (!Run("fsutil.exe", "file", "setCaseSensitiveInfo", dir, "enable"))
        {
            return false;
        }

        File.WriteAllText(Path.Combine(dir, "probe"), "");
        return !File.Exists(Path.Combine(dir, "PROBE")) && Delete(Path.Combine(dir, "probe"));
    }

    private static bool Delete(string path)
    {
        File.Delete(path);
        return true;
    }

    private static bool Run(string file, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
