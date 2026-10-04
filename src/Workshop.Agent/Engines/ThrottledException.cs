namespace Workshop.Agent.Engines;

/// <summary>The model is throttled (HTTP 429), and says to try again after <see cref="RetryAfter"/>.</summary>
public sealed class ThrottledException(TimeSpan retryAfter)
    : Exception($"The model is throttled; it asks to retry after {retryAfter.TotalSeconds:0.###} s.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
