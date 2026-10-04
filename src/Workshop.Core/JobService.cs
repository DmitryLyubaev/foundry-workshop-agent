using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Workshop.Core;

/// <summary>
/// The workshop's rules, and the only way to change its data. Each write runs in one transaction
/// and commits only when it succeeds. Refusals come back as <see cref="RuleResult.Fail"/>, never
/// as exceptions.
/// </summary>
public sealed class JobService(WorkshopDb db)
{
    public const int NoteMaxLength = 1000;
    public const int FaultMinLength = 3;
    public const int FaultMaxLength = 500;
    public const int DeviceTextMaxLength = 100;

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:sszzz";
    private const string FittedText = "fitted";
    private const string OnOrderText = "on order";

    // ---- Writes ----

    public RuleResult SetStatus(string jobId, JobStatus to) => Write((conn, tx) =>
    {
        var from = StatusOf(conn, tx, jobId);
        if (from is null)
        {
            return NoJob(jobId);
        }

        if (!JobTransitions.IsAllowed(from.Value, to))
        {
            return RuleResult.Fail(
                $"A job that is {JobStatusNames.Name(from.Value)} cannot become {JobStatusNames.Name(to)}.");
        }

        if (to == JobStatus.Ready && conn.ExecuteScalar<bool>(
                "SELECT EXISTS (SELECT 1 FROM job_parts WHERE job_id = @jobId AND state = @onOrder)",
                new { jobId, onOrder = OnOrderText },
                tx))
        {
            return RuleResult.Fail($"Job {jobId} still has parts on order.");
        }

        conn.Execute(
            "UPDATE jobs SET status = @status WHERE id = @jobId",
            new { status = JobStatusNames.Name(to), jobId },
            tx);
        return RuleResult.Success;
    });

    public RuleResult Cancel(string jobId) => SetStatus(jobId, JobStatus.Cancelled);

    public RuleResult AddPart(string jobId, string partId, int quantity) => Write((conn, tx) =>
    {
        var status = StatusOf(conn, tx, jobId);
        if (status is null)
        {
            return NoJob(jobId);
        }

        var stock = conn.QuerySingleOrDefault<long?>("SELECT stock FROM parts WHERE id = @partId", new { partId }, tx);
        if (stock is null)
        {
            return NoPart(partId);
        }

        if (!JobTransitions.PartsCanChange(status.Value))
        {
            return PartsLocked(status.Value);
        }

        if (quantity < 1)
        {
            return QuantityBelowOne();
        }

        var fits = quantity <= stock;
        if (fits)
        {
            conn.Execute("UPDATE parts SET stock = stock - @quantity WHERE id = @partId", new { quantity, partId }, tx);
        }

        conn.Execute(
            "INSERT INTO job_parts (job_id, part_id, quantity, state) VALUES (@jobId, @partId, @quantity, @state)",
            new { jobId, partId, quantity, state = fits ? FittedText : OnOrderText },
            tx);
        return fits ? RuleResult.Success : RuleResult.SuccessWith("Added on order: not enough in stock.");
    });

    public RuleResult FitOrderedParts(string jobId) => Write((conn, tx) =>
    {
        var status = StatusOf(conn, tx, jobId);
        if (status is null)
        {
            return NoJob(jobId);
        }

        if (!JobTransitions.PartsCanChange(status.Value))
        {
            return PartsLocked(status.Value);
        }

        var onOrder = conn.Query<OrderedPartRow>(
            """
            SELECT rowid AS RowId, part_id AS PartId, quantity AS Quantity
            FROM job_parts
            WHERE job_id = @jobId AND state = @onOrder
            ORDER BY rowid
            """,
            new { jobId, onOrder = OnOrderText },
            tx).ToList();
        if (onOrder.Count == 0)
        {
            return RuleResult.SuccessWith($"Job {jobId} has no parts on order.");
        }

        var fitted = 0;
        foreach (var part in onOrder)
        {
            // Taking the stock only where it covers the quantity is the check and the decrement
            // in one statement; earlier parts in this loop may already have used some of it.
            var taken = conn.Execute(
                "UPDATE parts SET stock = stock - @Quantity WHERE id = @PartId AND stock >= @Quantity",
                part,
                tx);
            if (taken == 1)
            {
                conn.Execute(
                    "UPDATE job_parts SET state = @fitted WHERE rowid = @rowId",
                    new { fitted = FittedText, rowId = part.RowId },
                    tx);
                fitted++;
            }
        }

        return RuleResult.SuccessWith($"Fitted {fitted} of {onOrder.Count} parts on order.");
    });

    public RuleResult AddNote(string jobId, string text) => Write((conn, tx) =>
    {
        if (StatusOf(conn, tx, jobId) is null)
        {
            return NoJob(jobId);
        }

        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return RuleResult.Fail("A note cannot be empty.");
        }

        if (trimmed.Length > NoteMaxLength)
        {
            return RuleResult.Fail("A note can be at most 1,000 characters.");
        }

        conn.Execute(
            "INSERT INTO notes (job_id, at, text) VALUES (@jobId, @at, @text)",
            new { jobId, at = Now(), text = trimmed },
            tx);
        return RuleResult.Success;
    });

    public RuleResult BookIn(string deviceId, string fault, out string? jobId)
    {
        string? newId = null;
        var result = Write((conn, tx) =>
        {
            if (!Exists(conn, tx, "devices", deviceId))
            {
                return RuleResult.Fail($"There is no device {deviceId}.");
            }

            var trimmed = (fault ?? "").Trim();
            if (trimmed.Length < FaultMinLength)
            {
                return RuleResult.Fail("The fault must be at least 3 characters.");
            }

            if (trimmed.Length > FaultMaxLength)
            {
                return RuleResult.Fail("The fault can be at most 500 characters.");
            }

            var id = $"J-{Math.Max(LastNumber(conn, tx, "jobs"), 1000) + 1}";
            conn.Execute(
                "INSERT INTO jobs (id, device_id, fault, status, booked_at) VALUES (@id, @deviceId, @fault, @status, @at)",
                new { id, deviceId, fault = trimmed, status = JobStatusNames.Name(JobStatus.BookedIn), at = Now() },
                tx);
            newId = id;
            return RuleResult.Success;
        });
        jobId = result.Ok ? newId : null;
        return result;
    }

    public RuleResult ReceiveStock(string partId, int quantity) => Write((conn, tx) =>
    {
        if (!Exists(conn, tx, "parts", partId))
        {
            return NoPart(partId);
        }

        if (quantity < 1)
        {
            return QuantityBelowOne();
        }

        conn.Execute("UPDATE parts SET stock = stock + @quantity WHERE id = @partId", new { quantity, partId }, tx);
        return RuleResult.Success;
    });

    public RuleResult AddDevice(string customerId, string kind, string model, string serial, out string? deviceId)
    {
        string? newId = null;
        var result = Write((conn, tx) =>
        {
            if (!Exists(conn, tx, "customers", customerId))
            {
                return RuleResult.Fail($"There is no customer {customerId}.");
            }

            if (!DeviceKinds.All.Contains(kind, StringComparer.Ordinal))
            {
                return RuleResult.Fail($"The kind must be one of: {string.Join(", ", DeviceKinds.All)}.");
            }

            var trimmedModel = (model ?? "").Trim();
            var trimmedSerial = (serial ?? "").Trim();
            var refusal = CheckDeviceText("model", trimmedModel) ?? CheckDeviceText("serial", trimmedSerial);
            if (refusal is not null)
            {
                return refusal;
            }

            var id = $"D-{LastNumber(conn, tx, "devices") + 1:000}";
            conn.Execute(
                "INSERT INTO devices (id, customer_id, kind, model, serial) VALUES (@id, @customerId, @kind, @model, @serial)",
                new { id, customerId, kind, model = trimmedModel, serial = trimmedSerial },
                tx);
            newId = id;
            return RuleResult.Success;
        });
        deviceId = result.Ok ? newId : null;
        return result;
    }

    // ---- Reads, for the screens ----

    /// <summary>
    /// Jobs, optionally of one status, whose ID, fault, customer name or device kind, model or
    /// serial contains <paramref name="search"/> (ignoring case).
    /// </summary>
    public IReadOnlyList<JobCard> Jobs(JobStatus? status, string? search) => Read(conn => conn.Query<JobRow>(
        """
        SELECT j.id AS Id, j.device_id AS DeviceId, j.fault AS Fault, j.status AS Status, j.booked_at AS BookedAt
        FROM jobs j
        JOIN devices d ON d.id = j.device_id
        JOIN customers c ON c.id = d.customer_id
        WHERE (@status IS NULL OR j.status = @status)
          AND (@search IS NULL
               OR instr(lower(j.id || ' ' || j.fault || ' ' || c.name || ' ' || d.kind || ' ' || d.model || ' ' || d.serial),
                        lower(@search)) > 0)
        ORDER BY j.id
        """,
        new { status = status is null ? null : JobStatusNames.Name(status.Value), search = SearchOrNull(search) })
        .Select(r => r.ToRecord())
        .ToList());

    public JobCard? Job(string id) => Read(conn => conn.QuerySingleOrDefault<JobRow>(
        "SELECT id AS Id, device_id AS DeviceId, fault AS Fault, status AS Status, booked_at AS BookedAt FROM jobs WHERE id = @id",
        new { id })?.ToRecord());

    public IReadOnlyList<JobPart> JobParts(string id) => Read(conn => conn.Query<JobPartRow>(
        """
        SELECT job_id AS JobId, part_id AS PartId, quantity AS Quantity, state AS State
        FROM job_parts WHERE job_id = @id ORDER BY rowid
        """,
        new { id })
        .Select(r => r.ToRecord())
        .ToList());

    public IReadOnlyList<Note> Notes(string id) => Read(conn => conn.Query<NoteRow>(
        "SELECT job_id AS JobId, at AS At, text AS Text FROM notes WHERE job_id = @id ORDER BY at, rowid",
        new { id })
        .Select(r => r.ToRecord())
        .ToList());

    /// <summary>Customers whose ID, name or phone contains <paramref name="search"/> (ignoring case).</summary>
    public IReadOnlyList<Customer> Customers(string? search) => Read(conn => conn.Query<Customer>(
        """
        SELECT id AS Id, name AS Name, phone AS Phone
        FROM customers
        WHERE @search IS NULL OR instr(lower(id || ' ' || name || ' ' || phone), lower(@search)) > 0
        ORDER BY id
        """,
        new { search = SearchOrNull(search) })
        .ToList());

    public Customer? Customer(string id) => Read(conn => conn.QuerySingleOrDefault<Customer>(
        "SELECT id AS Id, name AS Name, phone AS Phone FROM customers WHERE id = @id",
        new { id }));

    public IReadOnlyList<Device> DevicesOf(string customerId) => Read(conn => conn.Query<Device>(
        """
        SELECT id AS Id, customer_id AS CustomerId, kind AS Kind, model AS Model, serial AS Serial
        FROM devices WHERE customer_id = @customerId ORDER BY id
        """,
        new { customerId })
        .ToList());

    public Device? Device(string id) => Read(conn => conn.QuerySingleOrDefault<Device>(
        "SELECT id AS Id, customer_id AS CustomerId, kind AS Kind, model AS Model, serial AS Serial FROM devices WHERE id = @id",
        new { id }));

    public IReadOnlyList<Part> Parts() => Read(conn => conn.Query<PartRow>(
        "SELECT id AS Id, name AS Name, stock AS Stock FROM parts ORDER BY id")
        .Select(r => r.ToRecord())
        .ToList());

    // ---- Plumbing ----

    private RuleResult Write(Func<SqliteConnection, SqliteTransaction, RuleResult> work)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var result = work(conn, tx);
        if (result.Ok)
        {
            tx.Commit();
        }

        return result;
    }

    private T Read<T>(Func<SqliteConnection, T> query)
    {
        using var conn = db.Open();
        return query(conn);
    }

    private static JobStatus? StatusOf(SqliteConnection conn, SqliteTransaction tx, string jobId)
    {
        var name = conn.QuerySingleOrDefault<string?>("SELECT status FROM jobs WHERE id = @jobId", new { jobId }, tx);
        return name is null ? null : ParseStatus(name);
    }

    // The table name is always one of this class's literals, never input.
    private static bool Exists(SqliteConnection conn, SqliteTransaction tx, string table, string id) =>
        conn.ExecuteScalar<bool>($"SELECT EXISTS (SELECT 1 FROM {table} WHERE id = @id)", new { id }, tx);

    /// <summary>The highest number after the two-character prefix (J-, D-) in a table's IDs, or 0.</summary>
    private static long LastNumber(SqliteConnection conn, SqliteTransaction tx, string table) =>
        conn.ExecuteScalar<long>($"SELECT COALESCE(MAX(CAST(substr(id, 3) AS INTEGER)), 0) FROM {table}", transaction: tx);

    private static RuleResult? CheckDeviceText(string field, string value) =>
        value.Length == 0 ? RuleResult.Fail($"The {field} cannot be empty.")
        : value.Length > DeviceTextMaxLength ? RuleResult.Fail($"The {field} can be at most 100 characters.")
        : null;

    private static string? SearchOrNull(string? search) => string.IsNullOrWhiteSpace(search) ? null : search.Trim();

    private static string Now() => DateTimeOffset.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string text) =>
        DateTimeOffset.ParseExact(text, TimestampFormat, CultureInfo.InvariantCulture);

    private static JobStatus ParseStatus(string name) =>
        JobStatusNames.Parse(name) ?? throw new InvalidDataException($"Unknown job status '{name}' in the database.");

    private static PartState ParsePartState(string text) => text switch
    {
        FittedText => PartState.Fitted,
        OnOrderText => PartState.OnOrder,
        _ => throw new InvalidDataException($"Unknown part state '{text}' in the database."),
    };

    private static RuleResult NoJob(string jobId) => RuleResult.Fail($"There is no job {jobId}.");

    private static RuleResult NoPart(string partId) => RuleResult.Fail($"There is no part {partId}.");

    private static RuleResult QuantityBelowOne() => RuleResult.Fail("The quantity must be at least 1.");

    // A ready job can reopen, so its refusal names the way back; a closed job's cannot.
    private static RuleResult PartsLocked(JobStatus status) =>
        RuleResult.Fail(status == JobStatus.Ready
            ? $"The parts of a job that is {JobStatusNames.Name(status)} cannot change. Set the status to {JobStatusNames.Name(JobStatus.InRepair)} first."
            : $"The parts of a job that is {JobStatusNames.Name(status)} cannot change.");

    // SQLite hands back INTEGER as long and timestamps and statuses as text, which Dapper cannot
    // bind to the records' constructors, so rows are read into these and converted.

    private sealed class JobRow
    {
        public string Id { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string Fault { get; set; } = "";
        public string Status { get; set; } = "";
        public string BookedAt { get; set; } = "";

        public JobCard ToRecord() => new(Id, DeviceId, Fault, ParseStatus(Status), ParseTimestamp(BookedAt));
    }

    private sealed class JobPartRow
    {
        public string JobId { get; set; } = "";
        public string PartId { get; set; } = "";
        public long Quantity { get; set; }
        public string State { get; set; } = "";

        public JobPart ToRecord() => new(JobId, PartId, checked((int)Quantity), ParsePartState(State));
    }

    private sealed class NoteRow
    {
        public string JobId { get; set; } = "";
        public string At { get; set; } = "";
        public string Text { get; set; } = "";

        public Note ToRecord() => new(JobId, ParseTimestamp(At), Text);
    }

    private sealed class PartRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long Stock { get; set; }

        public Part ToRecord() => new(Id, Name, checked((int)Stock));
    }

    private sealed class OrderedPartRow
    {
        public long RowId { get; set; }
        public string PartId { get; set; } = "";
        public long Quantity { get; set; }
    }
}
