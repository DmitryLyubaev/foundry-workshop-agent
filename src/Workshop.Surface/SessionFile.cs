using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Workshop.Surface;

/// <summary>What a running endpoint tells its clients: where it listens, its token, and who started it.</summary>
public sealed record SessionInfo(int Port, string Token, int Pid, DateTimeOffset StartedAt);

/// <summary>
/// The per-launch session file, <c>session.json</c>, through which a local client learns the
/// endpoint's port and token. Only the current Windows user can read it: that is what keeps the
/// token from other users.
/// </summary>
public static class SessionFile
{
    public const string FileName = "session.json";

    /// <summary><c>%LOCALAPPDATA%\FoundryWorkshopAgent</c>.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoundryWorkshopAgent");

    /// <summary>
    /// Writes the session file for this process, replacing any earlier one, and returns its path.
    /// The file grants the current user full control, with inheritance removed and no other entry.
    /// </summary>
    public static string Write(string directory, int port, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new SessionInfo(port, token, Environment.ProcessId, DateTimeOffset.UtcNow), SurfaceJson.Options);

        // Created with its ACL already in place, so the token is never readable by anyone else,
        // then renamed over the old file: the rename keeps the ACL and replaces a stale file whole.
        var temporary = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileInfo(temporary).Create(
                FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, bufferSize: 4096, FileOptions.None, OnlyTheCurrentUser()))
            {
                stream.Write(json);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        return path;
    }

    /// <exception cref="FileNotFoundException">There is no session file in <paramref name="directory"/>.</exception>
    /// <exception cref="InvalidDataException">The file is not a session.</exception>
    public static SessionInfo Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var path = Path.Combine(directory, FileName);
        try
        {
            return JsonSerializer.Deserialize<SessionInfo>(File.ReadAllBytes(path), SurfaceJson.Options) is { Token: not null } session
                ? session
                : throw new InvalidDataException($"The session file '{path}' holds no session.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The session file '{path}' is not valid.", e);
        }
    }

    /// <summary>Deletes the session file if it still holds <paramref name="token"/>; another instance's file is left alone.</summary>
    internal static void DeleteIfOwned(string directory, string token)
    {
        try
        {
            if (Read(directory).Token == token)
            {
                File.Delete(Path.Combine(directory, FileName));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Gone, replaced by something unreadable, or locked: in each case it is not ours to delete.
        }
    }

    private static FileSecurity OnlyTheCurrentUser()
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user has no security identifier.");

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
