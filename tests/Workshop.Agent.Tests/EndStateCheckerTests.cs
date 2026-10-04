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
        var scenario = With(new Expectation([], Unchanged: true, ReplyContains: [], UnchangedExcept: []));

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
        var scenario = With(new Expectation([], Unchanged: true, ReplyContains: [], UnchangedExcept: ["jobs", "notes"]));

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
        var scenario = With(new Expectation([], Unchanged: true, ReplyContains: ["diagnosing", "J-1008"], UnchangedExcept: []));

        Assert.True(EndStateChecker.Check(scenario, db, before, "The Henderson printer, job j-1008, is DIAGNOSING.").Passed);

        var missing = EndStateChecker.Check(scenario, db, before, "Job J-1008 is in repair.");
        Assert.False(missing.Passed);
        Assert.Equal(["The reply does not contain 'diagnosing'."], missing.Failures);

        var none = EndStateChecker.Check(scenario, db, before, null);
        Assert.Equal(["The reply does not contain 'diagnosing'.", "The reply does not contain 'J-1008'."], none.Failures);
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
            UnchangedExcept: []));

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
        var scenario = With(new Expectation([new Check("SELECT 1; DELETE FROM jobs", "1")], Unchanged: false, ReplyContains: [], UnchangedExcept: []));

        var result = EndStateChecker.Check(scenario, db, before, null);

        Assert.False(result.Passed);
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

            var before = EndStateChecker.Fingerprint(db);
            var result = EndStateChecker.Check(scenario, db, before, string.Join(" ", scenario.Expect.ReplyContains));

            // No check is broken SQL, whatever the state.
            Assert.DoesNotContain(result.Failures, f => f.Contains(" failed: ", StringComparison.Ordinal));

            // A scenario whose right end state is the seed passes on it; any other must not.
            var endsAsSeeded = scenario.Expect.Unchanged && scenario.Expect.UnchangedExcept.Length == 0;
            Assert.True(endsAsSeeded == result.Passed, $"{scenario.Id}: {string.Join(" ", result.Failures)}");
        }
    }

    private static Scenario With(Expectation expect) => new("s99", "update", "A task.", [], null, expect);

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
