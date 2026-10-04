using Dapper;

namespace Workshop.Core.Tests;

public sealed class WorkshopDbTests
{
    [Fact]
    public void CreateFresh_seeds_known_counts()
    {
        using var temp = new TempDb();
        using var conn = temp.Db.Open();

        Assert.Equal(8, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM customers"));
        Assert.Equal(12, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM devices"));
        Assert.Equal(15, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM jobs"));
        Assert.Equal(10, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM parts"));
    }

    [Fact]
    public void Seed_has_a_job_in_every_status_and_two_parts_out_of_stock()
    {
        using var temp = new TempDb();
        var service = new JobService(temp.Db);

        foreach (var status in Enum.GetValues<JobStatus>())
        {
            Assert.NotEmpty(service.Jobs(status, null));
        }

        Assert.Equal(2, service.Parts().Count(p => p.Stock == 0));
    }

    [Fact]
    public void Seed_has_the_spec_examples()
    {
        using var temp = new TempDb();
        var service = new JobService(temp.Db);

        var sam = Assert.Single(service.Customers("Sam Rivera"));
        Assert.Equal("Sam Rivera", sam.Name);
        Assert.Contains(service.DevicesOf(sam.Id), d => d.Kind == "laptop");

        var henderson = Assert.Single(service.Customers("Henderson Family"));
        Assert.Equal("Henderson Family", henderson.Name);
        var printer = Assert.Single(service.DevicesOf(henderson.Id), d => d.Kind == "printer");
        var job = Assert.Single(service.Jobs(null, null), j => j.DeviceId == printer.Id);
        Assert.Equal(JobStatus.Diagnosing, job.Status);
    }

    [Fact]
    public void Seed_never_has_a_ready_or_collected_job_with_a_part_on_order()
    {
        using var temp = new TempDb();
        var service = new JobService(temp.Db);

        var finished = service.Jobs(JobStatus.Ready, null).Concat(service.Jobs(JobStatus.Collected, null));
        foreach (var job in finished)
        {
            Assert.DoesNotContain(service.JobParts(job.Id), p => p.State == PartState.OnOrder);
        }
    }

    [Fact]
    public void CreateFresh_refuses_an_existing_file()
    {
        var path = TempDb.NewPath();
        try
        {
            File.WriteAllText(path, "not a database");

            Assert.Throws<IOException>(() => WorkshopDb.CreateFresh(path));
            Assert.Equal("not a database", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenExisting_opens_the_same_data()
    {
        using var temp = new TempDb();
        Assert.True(new JobService(temp.Db).BookIn("D-001", "Fan is very loud", out var jobId).Ok);

        var reopened = new JobService(WorkshopDb.OpenExisting(temp.Path));

        Assert.Equal("Fan is very loud", reopened.Job(jobId!)!.Fault);
    }

    [Fact]
    public void OpenExisting_refuses_a_missing_file_and_does_not_create_it()
    {
        var path = TempDb.NewPath();

        var refused = Assert.Throws<FileNotFoundException>(() => WorkshopDb.OpenExisting(path));

        Assert.Contains(path, refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Two_fresh_copies_are_independent()
    {
        using var first = new TempDb();
        using var second = new TempDb();

        var booked = new JobService(first.Db).BookIn("D-001", "Fan is very loud", out var jobId);

        Assert.True(booked.Ok);
        Assert.NotNull(new JobService(first.Db).Job(jobId!));
        Assert.Null(new JobService(second.Db).Job(jobId!));
        Assert.Equal(15, new JobService(second.Db).Jobs(null, null).Count);
    }
}
