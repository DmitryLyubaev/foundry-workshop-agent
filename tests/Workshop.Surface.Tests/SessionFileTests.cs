using System.Security.AccessControl;
using System.Security.Principal;

namespace Workshop.Surface.Tests;

public sealed class SessionFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SessionFileTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Acl_grants_only_the_current_user()
    {
        var path = SessionFile.Write(directory, 47811, "made-up-token");

        Assert.Equal(Path.Combine(directory, "session.json"), path);
        AssertOnlyTheCurrentUser(path);
    }

    [Fact]
    public void Write_then_read_round_trips()
    {
        var before = DateTimeOffset.UtcNow;

        SessionFile.Write(directory, 50123, "made-up-token");
        var session = SessionFile.Read(directory);

        Assert.Equal(50123, session.Port);
        Assert.Equal("made-up-token", session.Token);
        Assert.Equal(Environment.ProcessId, session.Pid);
        Assert.InRange(session.StartedAt, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void Write_replaces_an_existing_file_and_leaves_no_temporary_file()
    {
        SessionFile.Write(directory, 50123, "first");
        SessionFile.Write(directory, 50124, "second");

        Assert.Equal("second", SessionFile.Read(directory).Token);
        Assert.Equal(["session.json"], Directory.GetFiles(directory).Select(Path.GetFileName));
        AssertOnlyTheCurrentUser(Path.Combine(directory, "session.json"));
    }

    [Fact]
    public void Default_directory_is_under_local_app_data()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoundryWorkshopAgent");

        Assert.Equal(expected, SessionFile.DefaultDirectory);
    }

    /// <summary>One explicit allow-FullControl entry for the current user, nothing inherited.</summary>
    internal static void AssertOnlyTheCurrentUser(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        Assert.True(security.AreAccessRulesProtected, "Inheritance is not removed.");
        var rule = Assert.Single(rules);
        Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        Assert.False(rule.IsInherited);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
