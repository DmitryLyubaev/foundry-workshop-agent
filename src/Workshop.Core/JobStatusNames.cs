namespace Workshop.Core;

/// <summary>The names people see for each status. The database stores these names too.</summary>
public static class JobStatusNames
{
    public static IReadOnlyList<string> All { get; } = [.. Enum.GetValues<JobStatus>().Select(Name)];

    public static string Name(JobStatus status) => status switch
    {
        JobStatus.BookedIn => "booked in",
        JobStatus.Diagnosing => "diagnosing",
        JobStatus.WaitingOnParts => "waiting on parts",
        JobStatus.InRepair => "in repair",
        JobStatus.Ready => "ready",
        JobStatus.Collected => "collected",
        JobStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown job status."),
    };

    /// <summary>The status with exactly this name, or null.</summary>
    public static JobStatus? Parse(string name)
    {
        foreach (var status in Enum.GetValues<JobStatus>())
        {
            if (string.Equals(Name(status), name, StringComparison.Ordinal))
            {
                return status;
            }
        }

        return null;
    }
}
