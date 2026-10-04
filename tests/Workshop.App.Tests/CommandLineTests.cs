using System.Globalization;
using Workshop.Surface;

namespace Workshop.App.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void No_arguments_use_the_defaults()
    {
        var options = AppOptions.Parse([]);

        Assert.Equal(new AppOptions(null, SurfaceEndpoint.DefaultPort, SessionFile.DefaultDirectory), options);
    }

    [Fact]
    public void Every_argument_is_read_in_any_order()
    {
        var options = AppOptions.Parse(["--session-dir", @"C:\runs\one", "--port", "50123", "--db", @"C:\runs\one\workshop.db"]);

        Assert.Equal(new AppOptions(@"C:\runs\one\workshop.db", 50123, @"C:\runs\one"), options);
    }

    [Theory]
    [InlineData("Unknown argument '--verbose'.", "--verbose")]
    [InlineData("Unknown argument 'workshop.db'.", "workshop.db")]
    [InlineData("--db needs a value.", "--db")]
    [InlineData("--db needs a value.", "--db", " ")]
    [InlineData("--port is given more than once.", "--port", "50000", "--port", "50001")]
    [InlineData("--port needs a whole number from 1 to 65535, not 'abc'.", "--port", "abc")]
    [InlineData("--port needs a whole number from 1 to 65535, not '0'.", "--port", "0")]
    [InlineData("--port needs a whole number from 1 to 65535, not '65536'.", "--port", "65536")]
    [InlineData("--port needs a whole number from 1 to 65535, not '-1'.", "--port", "-1")]
    public void Bad_arguments_are_refused_with_the_reason(string reason, params string[] args)
    {
        var refused = Assert.Throws<ArgumentException>(() => AppOptions.Parse(args));

        Assert.Equal(reason, refused.Message);
    }

    [Fact]
    public void A_missing_database_fails_the_launch_and_writes_no_session()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WorkshopAppTests", Guid.NewGuid().ToString("N"));
        var options = new AppOptions(Path.Combine(directory, "missing.db"), SurfaceEndpoint.DefaultPort, directory);

        var refused = Assert.Throws<FileNotFoundException>(() => Program.Launch(options));

        Assert.Contains("missing.db", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void A_second_launch_on_the_same_port_fails_and_leaves_the_first_session()
    {
        using var first = AppHost.Start();
        var session = File.ReadAllText(Path.Combine(first.SessionDirectory, SessionFile.FileName));
        var copy = Path.Combine(first.Directory, "second.db");
        File.Copy(first.DatabasePath, copy);

        var refused = first.OnUi(_ => Assert.Throws<InvalidOperationException>(
            () => Program.Launch(new AppOptions(copy, first.Port, first.SessionDirectory))));

        Assert.Contains(first.Port.ToString(CultureInfo.InvariantCulture), refused.Message, StringComparison.Ordinal);
        Assert.Equal(session, File.ReadAllText(Path.Combine(first.SessionDirectory, SessionFile.FileName)));
        Assert.True(File.Exists(copy), "A database given with --db is never deleted.");
    }
}
