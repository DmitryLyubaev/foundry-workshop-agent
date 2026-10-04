namespace Workshop.Agent.Runner;

/// <summary>
/// The app did not come up: it could not be started, it exited early, or it wrote no session file
/// in time. An infrastructure error, never a task failure.
/// </summary>
public sealed class AppStartException(string message, Exception? innerException = null)
    : Exception(message, innerException);
