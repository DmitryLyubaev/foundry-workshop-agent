namespace Workshop.Core;

/// <summary>The status changes a job may make (spec §3.1). Anything not listed is refused.</summary>
internal static class JobTransitions
{
    private static readonly Dictionary<JobStatus, JobStatus[]> Allowed = new()
    {
        [JobStatus.BookedIn] = [JobStatus.Diagnosing, JobStatus.Cancelled],
        [JobStatus.Diagnosing] = [JobStatus.WaitingOnParts, JobStatus.InRepair, JobStatus.Cancelled],
        [JobStatus.WaitingOnParts] = [JobStatus.InRepair, JobStatus.Cancelled],
        [JobStatus.InRepair] = [JobStatus.WaitingOnParts, JobStatus.Ready, JobStatus.Cancelled],
        [JobStatus.Ready] = [JobStatus.Collected, JobStatus.InRepair],
        [JobStatus.Collected] = [],
        [JobStatus.Cancelled] = [],
    };

    public static bool IsAllowed(JobStatus from, JobStatus to) => Allowed[from].Contains(to);

    /// <summary>
    /// Parts change only before a job is ready. A ready job reopens through "in repair" first, so
    /// a part can never go on order behind a ready job's back.
    /// </summary>
    public static bool PartsCanChange(JobStatus status) =>
        status is JobStatus.BookedIn or JobStatus.Diagnosing or JobStatus.WaitingOnParts or JobStatus.InRepair;
}
