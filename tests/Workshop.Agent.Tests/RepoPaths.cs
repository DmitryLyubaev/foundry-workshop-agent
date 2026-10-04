namespace Workshop.Agent.Tests;

/// <summary>The committed scenarios and scripts, found from the solution root above the test's directory.</summary>
internal static class RepoPaths
{
    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary><c>scenarios/</c>: the 20 scenario files.</summary>
    public static string Scenarios => Path.Combine(Root.Value, "scenarios");

    /// <summary><c>tests/Workshop.Agent.Tests/Scripts/</c>: each scenario's correct and wrong scripted run.</summary>
    public static string Scripts => Path.Combine(Root.Value, "tests", "Workshop.Agent.Tests", "Scripts");

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FoundryWorkshopAgent.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"No solution root (FoundryWorkshopAgent.slnx) above '{AppContext.BaseDirectory}'.");
    }
}
