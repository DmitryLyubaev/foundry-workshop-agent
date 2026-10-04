using Dapper;

namespace Workshop.Core.Tests;

public sealed class JobServiceTests : IDisposable
{
    // A seeded job in each status. J-1006 (in repair) has only fitted parts, and J-1007
    // (waiting on parts) has one P-02 on order.
    private static readonly Dictionary<JobStatus, string> SeedJobIn = new()
    {
        [JobStatus.BookedIn] = "J-1013",
        [JobStatus.Diagnosing] = "J-1014",
        [JobStatus.WaitingOnParts] = "J-1007",
        [JobStatus.InRepair] = "J-1006",
        [JobStatus.Ready] = "J-1005",
        [JobStatus.Collected] = "J-1001",
        [JobStatus.Cancelled] = "J-1002",
    };

    private readonly TempDb _temp = new();
    private readonly JobService _service;

    public JobServiceTests() => _service = new JobService(_temp.Db);

    public void Dispose() => _temp.Dispose();

    // ---- The transition table, one test per row ----

    [Theory]
    [InlineData(JobStatus.BookedIn, false)]
    [InlineData(JobStatus.Diagnosing, true)]
    [InlineData(JobStatus.WaitingOnParts, false)]
    [InlineData(JobStatus.InRepair, false)]
    [InlineData(JobStatus.Ready, false)]
    [InlineData(JobStatus.Collected, false)]
    [InlineData(JobStatus.Cancelled, true)]
    public void From_booked_in(JobStatus to, bool allowed) => AssertTransition(JobStatus.BookedIn, to, allowed);

    [Theory]
    [InlineData(JobStatus.BookedIn, false)]
    [InlineData(JobStatus.Diagnosing, false)]
    [InlineData(JobStatus.WaitingOnParts, true)]
    [InlineData(JobStatus.InRepair, true)]
    [InlineData(JobStatus.Ready, false)]
    [InlineData(JobStatus.Collected, false)]
    [InlineData(JobStatus.Cancelled, true)]
    public void From_diagnosing(JobStatus to, bool allowed) => AssertTransition(JobStatus.Diagnosing, to, allowed);

    [Theory]
    [InlineData(JobStatus.BookedIn, false)]
    [InlineData(JobStatus.Diagnosing, false)]
    [InlineData(JobStatus.WaitingOnParts, false)]
    [InlineData(JobStatus.InRepair, true)]
    [InlineData(JobStatus.Ready, false)]
    [InlineData(JobStatus.Collected, false)]
    [InlineData(JobStatus.Cancelled, true)]
    public void From_waiting_on_parts(JobStatus to, bool allowed) =>
        AssertTransition(JobStatus.WaitingOnParts, to, allowed);

    [Theory]
    [InlineData(JobStatus.BookedIn, false)]
    [InlineData(JobStatus.Diagnosing, false)]
    [InlineData(JobStatus.WaitingOnParts, true)]
    [InlineData(JobStatus.InRepair, false)]
    [InlineData(JobStatus.Ready, true)]
    [InlineData(JobStatus.Collected, false)]
    [InlineData(JobStatus.Cancelled, true)]
    public void From_in_repair(JobStatus to, bool allowed) => AssertTransition(JobStatus.InRepair, to, allowed);

    [Theory]
    [InlineData(JobStatus.BookedIn, false)]
    [InlineData(JobStatus.Diagnosing, false)]
    [InlineData(JobStatus.WaitingOnParts, false)]
    [InlineData(JobStatus.InRepair, true)]
    [InlineData(JobStatus.Ready, false)]
    [InlineData(JobStatus.Collected, true)]
    [InlineData(JobStatus.Cancelled, false)]
    public void From_ready(JobStatus to, bool allowed) => AssertTransition(JobStatus.Ready, to, allowed);

    public static TheoryData<JobStatus, JobStatus> FinishedToAnything()
    {
        var data = new TheoryData<JobStatus, JobStatus>();
        foreach (var from in new[] { JobStatus.Collected, JobStatus.Cancelled })
        {
            foreach (var to in Enum.GetValues<JobStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FinishedToAnything))]
    public void From_collected_or_cancelled(JobStatus from, JobStatus to) => AssertTransition(from, to, allowed: false);

    private void AssertTransition(JobStatus from, JobStatus to, bool allowed)
    {
        var jobId = SeedJobIn[from];
        Assert.Equal(from, _service.Job(jobId)!.Status);

        var result = _service.SetStatus(jobId, to);

        if (allowed)
        {
            Assert.True(result.Ok, result.Message);
            Assert.Equal(to, _service.Job(jobId)!.Status);
        }
        else
        {
            Assert.False(result.Ok);
            Assert.Equal(
                $"A job that is {JobStatusNames.Name(from)} cannot become {JobStatusNames.Name(to)}.",
                result.Message);
            Assert.Equal(from, _service.Job(jobId)!.Status);
        }
    }

    // ---- Status rules ----

    [Fact]
    public void Ready_refused_with_a_part_on_order()
    {
        Assert.True(_service.AddPart("J-1006", "P-02", 1).Ok);

        var result = _service.SetStatus("J-1006", JobStatus.Ready);

        Assert.False(result.Ok);
        Assert.Equal("Job J-1006 still has parts on order.", result.Message);
        Assert.Equal(JobStatus.InRepair, _service.Job("J-1006")!.Status);
    }

    [Fact]
    public void Cancel_refused_for_collected()
    {
        var result = _service.Cancel("J-1001");

        Assert.False(result.Ok);
        Assert.Equal("A job that is collected cannot become cancelled.", result.Message);
        Assert.Equal(JobStatus.Collected, _service.Job("J-1001")!.Status);
    }

    [Fact]
    public void Cancel_cancels_an_open_job()
    {
        Assert.True(_service.Cancel("J-1013").Ok);
        Assert.Equal(JobStatus.Cancelled, _service.Job("J-1013")!.Status);
    }

    // ---- Parts ----

    [Fact]
    public void AddPart_fits_and_decrements_when_in_stock()
    {
        var result = _service.AddPart("J-1006", "P-01", 2);

        Assert.True(result.Ok);
        Assert.Null(result.Message);
        Assert.Contains(new JobPart("J-1006", "P-01", 2, PartState.Fitted), _service.JobParts("J-1006"));
        Assert.Equal(2, Stock("P-01"));

        // Exactly the stock on hand still fits.
        Assert.True(_service.AddPart("J-1006", "P-05", 2).Ok);
        Assert.Contains(new JobPart("J-1006", "P-05", 2, PartState.Fitted), _service.JobParts("J-1006"));
        Assert.Equal(0, Stock("P-05"));
    }

    [Fact]
    public void AddPart_goes_on_order_when_short()
    {
        var result = _service.AddPart("J-1006", "P-08", 3);

        Assert.True(result.Ok);
        Assert.Equal("Added on order: not enough in stock.", result.Message);
        Assert.Contains(new JobPart("J-1006", "P-08", 3, PartState.OnOrder), _service.JobParts("J-1006"));
        Assert.Equal(2, Stock("P-08"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddPart_refuses_quantity_below_one(int quantity)
    {
        var before = _service.JobParts("J-1006").Count;

        var result = _service.AddPart("J-1006", "P-01", quantity);

        Assert.False(result.Ok);
        Assert.Equal("The quantity must be at least 1.", result.Message);
        Assert.Equal(before, _service.JobParts("J-1006").Count);
        Assert.Equal(4, Stock("P-01"));
    }

    [Theory]
    [InlineData("J-1005", "The parts of a job that is ready cannot change. Set the status to in repair first.")]
    [InlineData("J-1001", "The parts of a job that is collected cannot change.")]
    [InlineData("J-1002", "The parts of a job that is cancelled cannot change.")]
    public void AddPart_refused_once_a_job_is_ready_or_closed(string jobId, string message)
    {
        var result = _service.AddPart(jobId, "P-01", 1);

        Assert.False(result.Ok);
        Assert.Equal(message, result.Message);
        Assert.Equal(4, Stock("P-01"));
    }

    [Fact]
    public void FitOrderedParts_fits_after_ReceiveStock()
    {
        // J-1007 is waiting on one P-02, and P-02 is out of stock, as is P-04.
        Assert.True(_service.AddPart("J-1007", "P-04", 2).Ok);
        Assert.True(_service.FitOrderedParts("J-1007").Ok);
        Assert.All(_service.JobParts("J-1007"), p => Assert.Equal(PartState.OnOrder, p.State));

        Assert.True(_service.ReceiveStock("P-02", 3).Ok);
        Assert.True(_service.ReceiveStock("P-04", 1).Ok);
        var result = _service.FitOrderedParts("J-1007");

        Assert.True(result.Ok);
        var parts = _service.JobParts("J-1007");
        Assert.Contains(new JobPart("J-1007", "P-02", 1, PartState.Fitted), parts);
        Assert.Contains(new JobPart("J-1007", "P-04", 2, PartState.OnOrder), parts);
        Assert.Equal(2, Stock("P-02"));
        Assert.Equal(1, Stock("P-04"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ReceiveStock_refuses_quantity_below_one(int quantity)
    {
        var result = _service.ReceiveStock("P-01", quantity);

        Assert.False(result.Ok);
        Assert.Equal("The quantity must be at least 1.", result.Message);
        Assert.Equal(4, Stock("P-01"));
    }

    // ---- Notes ----

    [Fact]
    public void AddNote_refuses_blank_and_over_1000()
    {
        var before = _service.Notes("J-1006").Count;

        Assert.Equal("A note cannot be empty.", _service.AddNote("J-1006", "").Message);
        Assert.Equal("A note cannot be empty.", _service.AddNote("J-1006", "   ").Message);
        var tooLong = _service.AddNote("J-1006", new string('x', 1001));
        Assert.False(tooLong.Ok);
        Assert.Equal("A note can be at most 1,000 characters.", tooLong.Message);
        Assert.Equal(before, _service.Notes("J-1006").Count);

        Assert.True(_service.AddNote("J-1006", new string('x', 1000)).Ok);
        Assert.True(_service.AddNote("J-1006", "Keyboard swapped; testing every key.").Ok);
        var notes = _service.Notes("J-1006");
        Assert.Equal(before + 2, notes.Count);
        Assert.Equal("Keyboard swapped; testing every key.", notes[^1].Text);
        Assert.Equal("J-1006", notes[^1].JobId);
    }

    // ---- Booking in ----

    [Fact]
    public void BookIn_assigns_the_next_job_id()
    {
        var first = _service.BookIn("D-001", "Screen hinge cracked", out var firstId);
        var second = _service.BookIn("D-001", "Trackpad does not click", out var secondId);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal("J-1016", firstId);
        Assert.Equal("J-1017", secondId);
        var job = _service.Job("J-1016")!;
        Assert.Equal("D-001", job.DeviceId);
        Assert.Equal("Screen hinge cracked", job.Fault);
        Assert.Equal(JobStatus.BookedIn, job.Status);
    }

    [Fact]
    public void BookIn_refuses_a_fault_shorter_than_3_or_longer_than_500()
    {
        var tooShort = _service.BookIn("D-001", "ab", out var shortId);
        var tooLong = _service.BookIn("D-001", new string('x', 501), out var longId);

        Assert.False(tooShort.Ok);
        Assert.Equal("The fault must be at least 3 characters.", tooShort.Message);
        Assert.Null(shortId);
        Assert.False(tooLong.Ok);
        Assert.Equal("The fault can be at most 500 characters.", tooLong.Message);
        Assert.Null(longId);
        Assert.Equal(15, _service.Jobs(null, null).Count);

        Assert.True(_service.BookIn("D-001", "abc", out _).Ok);
        Assert.True(_service.BookIn("D-001", new string('x', 500), out _).Ok);
    }

    // ---- Devices ----

    [Fact]
    public void AddDevice_assigns_the_next_device_id()
    {
        var result = _service.AddDevice("C-001", "tablet", "Slate 11", "SL11-9001", out var deviceId);

        Assert.True(result.Ok);
        Assert.Equal("D-013", deviceId);
        Assert.Equal(new Device("D-013", "C-001", "tablet", "Slate 11", "SL11-9001"), _service.Device("D-013"));
        Assert.Contains(_service.DevicesOf("C-001"), d => d.Id == "D-013");
    }

    [Fact]
    public void AddDevice_refuses_an_unknown_kind_and_blank_text()
    {
        Assert.Equal(
            "The kind must be one of: laptop, desktop, phone, tablet, printer, other.",
            _service.AddDevice("C-001", "toaster", "T1", "S1", out var id).Message);
        Assert.Null(id);
        Assert.Equal("The model cannot be empty.", _service.AddDevice("C-001", "laptop", " ", "S1", out _).Message);
        Assert.Equal("The serial cannot be empty.", _service.AddDevice("C-001", "laptop", "M1", "", out _).Message);
        Assert.Equal(
            "The model can be at most 100 characters.",
            _service.AddDevice("C-001", "laptop", new string('m', 101), "S1", out _).Message);
        Assert.Equal(
            "The serial can be at most 100 characters.",
            _service.AddDevice("C-001", "laptop", "M1", new string('s', 101), out _).Message);
        Assert.Equal(12, DeviceCount());
    }

    // ---- Unknown records are refused, not thrown ----

    [Fact]
    public void Unknown_ids_are_refused()
    {
        Assert.Equal("There is no job J-9999.", _service.SetStatus("J-9999", JobStatus.Diagnosing).Message);
        Assert.Equal("There is no job J-9999.", _service.Cancel("J-9999").Message);
        Assert.Equal("There is no job J-9999.", _service.AddPart("J-9999", "P-01", 1).Message);
        Assert.Equal("There is no job J-9999.", _service.FitOrderedParts("J-9999").Message);
        Assert.Equal("There is no job J-9999.", _service.AddNote("J-9999", "Hello").Message);
        Assert.Equal("There is no part P-99.", _service.AddPart("J-1006", "P-99", 1).Message);
        Assert.Equal("There is no part P-99.", _service.ReceiveStock("P-99", 1).Message);
        Assert.Equal("There is no device D-999.", _service.BookIn("D-999", "Broken", out _).Message);
        Assert.Equal("There is no customer C-999.", _service.AddDevice("C-999", "laptop", "M", "S", out _).Message);
        Assert.Null(_service.Job("J-9999"));
        Assert.Null(_service.Customer("C-999"));
        Assert.Null(_service.Device("D-999"));
    }

    // ---- Reads ----

    [Fact]
    public void Jobs_filters_by_status_and_searches_customer_device_and_fault()
    {
        Assert.Equal(["J-1008"], _service.Jobs(null, "henderson").Select(j => j.Id));
        Assert.Equal(["J-1008"], _service.Jobs(JobStatus.Diagnosing, "Inkwell").Select(j => j.Id));
        Assert.Empty(_service.Jobs(JobStatus.Ready, "henderson"));
        Assert.All(_service.Jobs(JobStatus.InRepair, "  "), j => Assert.Equal(JobStatus.InRepair, j.Status));
        Assert.Contains(_service.Jobs(null, "paper jams"), j => j.Id == "J-1008");
        Assert.Equal(15, _service.Jobs(null, null).Count);
    }

    [Fact]
    public void Reads_return_the_seeded_records()
    {
        Assert.Equal(new Customer("C-001", "Sam Rivera", "555-0101"), _service.Customer("C-001"));
        Assert.Equal(8, _service.Customers(null).Count);
        Assert.Equal(new Device("D-003", "C-002", "printer", "Inkwell 300", "IW300-5512"), _service.Device("D-003"));
        Assert.Equal(new Part("P-02", "Phone screen assembly", 0), _service.Parts().Single(p => p.Id == "P-02"));
        Assert.NotEmpty(_service.Notes("J-1008"));
    }

    private int Stock(string partId) => _service.Parts().Single(p => p.Id == partId).Stock;

    private int DeviceCount()
    {
        using var conn = _temp.Db.Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM devices");
    }
}
