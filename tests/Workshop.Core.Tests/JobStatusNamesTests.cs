namespace Workshop.Core.Tests;

public sealed class JobStatusNamesTests
{
    [Theory]
    [InlineData(JobStatus.BookedIn, "booked in")]
    [InlineData(JobStatus.Diagnosing, "diagnosing")]
    [InlineData(JobStatus.WaitingOnParts, "waiting on parts")]
    [InlineData(JobStatus.InRepair, "in repair")]
    [InlineData(JobStatus.Ready, "ready")]
    [InlineData(JobStatus.Collected, "collected")]
    [InlineData(JobStatus.Cancelled, "cancelled")]
    public void Each_status_has_its_spec_name_and_parses_back(JobStatus status, string name)
    {
        Assert.Equal(name, JobStatusNames.Name(status));
        Assert.Equal(status, JobStatusNames.Parse(name));
    }

    [Fact]
    public void All_lists_the_names_in_workflow_order() =>
        Assert.Equal(
            ["booked in", "diagnosing", "waiting on parts", "in repair", "ready", "collected", "cancelled"],
            JobStatusNames.All);

    [Theory]
    [InlineData("")]
    [InlineData("done")]
    [InlineData("InRepair")]
    public void Parse_returns_null_for_an_unknown_name(string name) => Assert.Null(JobStatusNames.Parse(name));
}
