using Microsoft.Data.Sqlite;
using Workshop.Agent.Scenarios;
using Workshop.Core;

namespace Workshop.Agent.Tests;

/// <summary>The end-state checker over fresh seeded databases: its checks, the fingerprint and the reply.</summary>
public sealed class EndStateCheckerTests
{
    /// <summary>One change per table, each to a single cell of a single row.</summary>
    private static readonly (string Table, string Sql)[] OneChangePerTable =
    [
        ("customers", "UPDATE customers SET phone = '555-0199' WHERE id = 'C-004'"),
        ("devices", "UPDATE devices SET serial = 'TM5-8824' WHERE id = 'D-006'"),
        ("jobs", "UPDATE jobs SET status = 'diagnosing' WHERE id = 'J-1013'"),
        ("job_parts", "UPDATE job_parts SET quantity = 2 WHERE job_id = 'J-1012'"),
        ("notes", "UPDATE notes SET text = 'Customer called; will collect on Monday.' WHERE job_id = 'J-1012'"),
        ("parts", "UPDATE parts SET stock = 1 WHERE id = 'P-04'"),
    ];

    [Fact]
    public void Fingerprint_is_stable_and_detects_any_row_change()
    {
        using var run = TempRun.Create();
        var seed = Fresh(run, "seed.db");
        var other = Fresh(run, "other.db");

        var fingerprint = EndStateChecker.Fingerprint(seed);
        Assert.Equal(fingerprint, EndStateChecker.Fingerprint(seed));
        Assert.Equal(fingerprint, EndStateChecker.Fingerprint(other));

        foreach (var (table, sql) in OneChangePerTable)
        {
            var changed = Fresh(run, table + ".db");
            Execute(changed, sql);
            Assert.NotEqual(fingerprint, EndStateChecker.Fingerprint(changed));
        }

        // An added row, and a removed one, in a table with no primary key.
        var added = Fresh(run, "added.db");
        Execute(added, "INSERT INTO notes (job_id, at, text) VALUES ('J-1010', '2026-10-04T09:00:00+00:00', 'Fan replaced.')");
        Assert.NotEqual(fingerprint, EndStateChecker.Fingerprint(added));
        var removed = Fresh(run, "removed.db");
        Execute(removed, "DELETE FROM job_parts WHERE job_id = 'J-1001'");
        Assert.NotEqual(fingerprint, EndStateChecker.Fingerprint(removed));

        // A value's boundaries count: moving a character from one cell to the next is a change.
        var moved = Fresh(run, "moved.db");
        Execute(moved, "UPDATE customers SET name = 'Tom Okafor5', phone = '55-0104' WHERE id = 'C-004'");
        Assert.NotEqual(fingerprint, EndStateChecker.Fingerprint(moved));

        // Reading the fingerprint changes nothing, and leaves the file free to delete.
        Assert.Equal(fingerprint, EndStateChecker.Fingerprint(seed));
        File.Delete(seed);
    }

    [Fact]
    public void Unchanged_passes_on_an_untouched_database_and_fails_on_any_change()
    {
        using var run = TempRun.Create();
        var db = Fresh(run, "workshop.db");
        var before = EndStateChecker.Fingerprint(db);
        var scenario = With(new Expectation([], Unchanged: true, ReplyContains: [], UnchangedExcept: [], ReplyMatches: []));

        var untouched = EndStateChecker.Check(scenario, db, before, "Done.");
        Assert.True(untouched.Passed);
        Assert.Empty(untouched.Failures);

        Execute(db, "UPDATE parts SET stock = 1 WHERE id = 'P-04'");
        var result = EndStateChecker.Check(scenario, db, before, "Done.");
        Assert.False(result.Passed);
        Assert.Equal(["The database changed: parts."], result.Failures);
    }

    [Fact]
    public void UnchangedExcept_ignores_only_named_tables()
    {
        using var run = TempRun.Create();
        var db = Fresh(run, "workshop.db");
        var before = EndStateChecker.Fingerprint(db);
        var scenario = With(new Expectation([], Unchanged: true, ReplyContains: [], UnchangedExcept: ["jobs", "notes"], ReplyMatches: []));

        Execute(db, "UPDATE jobs SET status = 'diagnosing' WHERE id = 'J-1013'");
        Execute(db, "INSERT INTO notes (job_id, at, text) VALUES ('J-1013', '2026-10-04T09:00:00+00:00', 'Started.')");
        Assert.True(EndStateChecker.Check(scenario, db, before, null).Passed);

        // Each other table still counts, and every changed one is named.
        foreach (var (table, sql) in OneChangePerTable.Where(c => c.Table is not ("jobs" or "notes")))
        {
            var changed = Fresh(run, table + ".db");
            Execute(changed, sql);
            var result = EndStateChecker.Check(scenario, changed, before, null);
            Assert.False(result.Passed, table);
            Assert.Equal([$"The database changed: {table}."], result.Failures);
        }

        Execute(db, "UPDATE parts SET stock = 1 WHERE id = 'P-04'");
        Execute(db, "UPDATE customers SET phone = '555-0199' WHERE id = 'C-004'");
        Assert.Equal(["The database changed: customers, parts."], EndStateChecker.Check(scenario, db, before, null).Failures);
    }

    [Fact]
    public void ReplyContains_is_case_insensitive()
    {
        using var run = TempRun.Create();
        var db = Fresh(run, "workshop.db");
        var before = EndStateChecker.Fingerprint(db);
        var scenario = With(new Expectation([], Unchanged: true, ReplyContains: ["diagnosing", "J-1008"], UnchangedExcept: [], ReplyMatches: []));

        Assert.True(EndStateChecker.Check(scenario, db, before, "The Henderson printer, job j-1008, is DIAGNOSING.").Passed);

        var missing = EndStateChecker.Check(scenario, db, before, "Job J-1008 is in repair.");
        Assert.False(missing.Passed);
        Assert.Equal(["The reply does not contain 'diagnosing'."], missing.Failures);

        var none = EndStateChecker.Check(scenario, db, before, null);
        Assert.Equal(["The reply does not contain 'diagnosing'.", "The reply does not contain 'J-1008'."], none.Failures);
    }

    [Theory]
    [InlineData("We have 4 laptop batteries in stock.", true)]
    [InlineData("Four in stock.", true)]
    [InlineData("There are four.", true)]
    [InlineData("P-04 is out of stock.", false)]
    [InlineData("J-1004 is collected.", false)]
    [InlineData("We have 3 (checked 2026-10-04).", false)]
    [InlineData("We have 14 laptop batteries.", false)]
    [InlineData("Fourteen.", false)]
    public void ReplyMatches_s03_takes_the_count_four_not_a_4_inside_an_id(string reply, bool passes)
    {
        Assert.Equal(passes, CheckReply("s03", reply).Passed);
    }

    [Theory]
    [InlineData("It's being diagnosed.", true)]
    [InlineData("J-1008 is DIAGNOSING.", true)]
    [InlineData("It is under diagnosis.", true)]
    [InlineData("J-1004 has been collected.", false)]
    public void ReplyMatches_s01_takes_any_form_of_diagnosing(string reply, bool passes)
    {
        Assert.Equal(passes, CheckReply("s01", reply).Passed);
    }

    [Fact]
    public void ReplyMatches_names_the_pattern_and_fails_a_timeout_or_no_reply()
    {
        using var run = TempRun.Create();
        var db = Fresh(run, "workshop.db");
        var before = EndStateChecker.Fingerprint(db);

        Assert.Equal(["The reply does not match 'diagnos'."], CheckReply("s01", "In repair.").Failures);
        Assert.Equal(["The reply does not match 'diagnos'."], CheckReply("s01", null).Failures);

        // Catastrophic backtracking runs past the time limit, which is a failed check, not a hang.
        var slow = With(new Expectation([], Unchanged: false, ReplyContains: [], UnchangedExcept: [], ReplyMatches: ["^(a+)+$"]));
        var result = EndStateChecker.Check(slow, db, before, new string('a', 40) + "!");
        Assert.False(result.Passed);
        Assert.Equal(["The reply pattern '^(a+)+$' timed out after 1 s."], result.Failures);
    }

    [Fact]
    public void Reply_checks_fold_unicode_dashes_and_spaces()
    {
        // GPT models often write IDs with a non-breaking hyphen, U+2011.
        Assert.True(CheckReply("s02", "Two jobs: J\u20111007 and J\u20111009.").Passed);
        Assert.True(CheckReply("s02", "J\u20101007, J\u20131009").Passed);
        Assert.True(CheckReply("s02", "J\u22121007 and J\u20151009").Passed);
        Assert.False(CheckReply("s02", "J 1007 and J 1009").Passed);

        // The expected text is folded too, and so are non-breaking spaces.
        using var run = TempRun.Create();
        var db = Fresh(run, "workshop.db");
        var before = EndStateChecker.Fingerprint(db);
        var typographic = With(new Expectation([], Unchanged: false, ReplyContains: ["J\u20111008", "in\u00A0repair"], UnchangedExcept: [], ReplyMatches: ["P\u201301\u202Ffitted"]));
        Assert.True(EndStateChecker.Check(typographic, db, before, "J-1008 is in repair, with P-01\u2007fitted.").Passed);

        Assert.Equal("a-b-c-d-e-f-g h i j", ReplyText.Normalise("a\u2010b\u2011c\u2012d\u2013e\u2014f\u2015g\u00A0h\u202Fi\u2007j"));
        Assert.Equal("x-y", ReplyText.Normalise("x\u2212y"));
    }

    [Fact]
    public void Checks_compare_one_scalar_as_invariant_text()
    {
        using var run = TempRun.Create();
        var db = Fresh(run, "workshop.db");
        var before = EndStateChecker.Fingerprint(db);
        var scenario = With(new Expectation(
            [
                new Check("SELECT count(*) FROM jobs", "15"),
                new Check("SELECT status FROM jobs WHERE id = 'J-1008'", "diagnosing"),
                new Check("SELECT 1.5", "1.5"),
                new Check("SELECT NULL", "NULL"),
                new Check("SELECT stock FROM parts WHERE id = 'P-01'", "3"),
                new Check("SELECT nothing FROM jobs", "1"),
            ],
            Unchanged: false,
            ReplyContains: [],
            UnchangedExcept: [], ReplyMatches: []));

        var result = EndStateChecker.Check(scenario, db, before, null);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Failures.Length);
        Assert.Equal("Check 5 (SELECT stock FROM parts WHERE id = 'P-01') gave '4', expected '3'.", result.Failures[0]);
        Assert.StartsWith("Check 6 (SELECT nothing FROM jobs) failed: ", result.Failures[1]);
    }

    [Fact]
    public void Checks_cannot_write_even_past_the_loader()
    {
        using var run = TempRun.Create();
        var db = Fresh(run, "workshop.db");
        var before = EndStateChecker.Fingerprint(db);
        var scenario = With(new Expectation([new Check("SELECT 1; DELETE FROM jobs", "1")], Unchanged: false, ReplyContains: [], UnchangedExcept: [], ReplyMatches: []));

        var result = EndStateChecker.Check(scenario, db, before, null);

        Assert.False(result.Passed);
        Assert.Equal(before, EndStateChecker.Fingerprint(db));
    }

    [Fact]
    public void Checks_refuse_more_than_one_row_or_column()
    {
        using var run = TempRun.Create();
        var db = Fresh(run, "workshop.db");
        var before = EndStateChecker.Fingerprint(db);
        var scenario = With(new Expectation(
            [
                new Check("SELECT status FROM jobs WHERE id IN ('J-1008', 'J-1014')", "diagnosing"),
                new Check("SELECT id, status FROM jobs WHERE id = 'J-1008'", "J-1008"),
                new Check("SELECT status FROM jobs WHERE id = 'J-9999'", "NULL"),
            ],
            Unchanged: false,
            ReplyContains: [],
            UnchangedExcept: [],
            ReplyMatches: []));

        var result = EndStateChecker.Check(scenario, db, before, null);

        // Both first cells equal the expected text, yet neither answer is one scalar; no row reads as NULL.
        Assert.Equal(
            [
                "Check 1 (SELECT status FROM jobs WHERE id IN ('J-1008', 'J-1014')) returns more than one row, not one scalar.",
                "Check 2 (SELECT id, status FROM jobs WHERE id = 'J-1008') returns 2 columns, not one scalar.",
            ],
            result.Failures);
    }

    [Fact]
    public void The_checkers_connection_is_read_only()
    {
        using var run = TempRun.Create();
        var db = Fresh(run, "workshop.db");
        var before = EndStateChecker.Fingerprint(db);

        using (var conn = EndStateChecker.OpenReadOnly(db))
        {
            using var command = conn.CreateCommand();
            command.CommandText = "UPDATE parts SET stock = 99 WHERE id = 'P-01'";
            var e = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            Assert.Equal(8, e.SqliteErrorCode); // SQLITE_READONLY
        }

        Assert.Equal(before, EndStateChecker.Fingerprint(db));
    }

    [Fact]
    public void Every_scenarios_checks_run_and_untouched_ones_pass_on_the_seed()
    {
        using var run = TempRun.Create();
        foreach (var scenario in ScenarioLoader.LoadAll(RepoPaths.Scenarios))
        {
            var db = Fresh(run, scenario.Id + ".db");
            foreach (var setup in scenario.Setup)
            {
                Execute(db, setup);
            }

            // The database's part only: the reply checks have their own tests.
            var databaseOnly = scenario with { Expect = scenario.Expect with { ReplyContains = [], ReplyMatches = [] } };
            var before = EndStateChecker.Fingerprint(db);
            var result = EndStateChecker.Check(databaseOnly, db, before, null);

            // No check is broken SQL, whatever the state.
            Assert.DoesNotContain(result.Failures, f => f.Contains(" failed: ", StringComparison.Ordinal));

            // A scenario whose right end state is the seed passes on it; any other must not.
            var endsAsSeeded = scenario.Expect.Unchanged && scenario.Expect.UnchangedExcept.Length == 0;
            Assert.True(endsAsSeeded == result.Passed, $"{scenario.Id}: {string.Join(" ", result.Failures)}");
        }
    }

    private static Scenario With(Expectation expect) => new("s99", "update", "A task.", [], null, expect);

    /// <summary>A committed scenario's check of <paramref name="reply"/> on an untouched seed, where its database part passes.</summary>
    private static CheckResult CheckReply(string id, string? reply)
    {
        var scenario = ScenarioLoader.LoadAll(RepoPaths.Scenarios).Single(s => s.Id == id);
        using var run = TempRun.Create();
        var db = Fresh(run, "workshop.db");
        return EndStateChecker.Check(scenario, db, EndStateChecker.Fingerprint(db), reply);
    }

    private static string Fresh(TempRun run, string name)
    {
        var path = Path.Combine(run.Directory, name);
        _ = WorkshopDb.CreateFresh(path);
        return path;
    }

    private static void Execute(string dbPath, string sql)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
