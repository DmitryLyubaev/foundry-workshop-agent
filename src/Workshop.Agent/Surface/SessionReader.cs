using System.Diagnostics;
using System.Text.Json;

namespace Workshop.Agent.Surface;

/// <summary>Where a running app's endpoint listens, and the token it takes.</summary>
public sealed record SessionInfo(int Port, string Token);

/// <summary>
/// Reads the app's <c>session.json</c>, <c>{"port","token","pid","startedAt"}</c>, which the app
/// writes once its port is bound (README, "Session and process").
/// </summary>
public static class SessionReader
{
    public const string FileName = "session.json";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Polls until the session file in <paramref name="sessionDir"/> exists and parses.</summary>
    /// <exception cref="TimeoutException">No readable session file appeared within <paramref name="timeout"/>.</exception>
    public static SessionInfo WaitFor(string sessionDir, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDir);

        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (TryRead(sessionDir) is { } session)
            {
                return session.Info;
            }

            if (waited.Elapsed >= timeout)
            {
                throw new TimeoutException($"No session file appeared in '{sessionDir}' within {timeout.TotalSeconds:0.#} seconds.");
            }

            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>The session file and the process that wrote it, or null while it is missing or unreadable.</summary>
    internal static SessionFileContent? TryRead(string sessionDir)
    {
        try
        {
            // The app writes a temporary file and renames it into place, so a file that is there is whole;
            // a read can still meet it locked, or meet another writer's file, and is then retried.
            var content = JsonSerializer.Deserialize<SessionFileContent>(File.ReadAllBytes(Path.Combine(sessionDir, FileName)), ContractJson.Options);
            return content is { Port: > 0, Token.Length: > 0 } ? content : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal sealed record SessionFileContent(int Port, string Token, int Pid)
    {
        public SessionInfo Info => new(Port, Token);
    }
}
