namespace Workshop.Agent.Scenarios;

/// <summary>
/// One scenario of the study (spec §5.1): the task given to the agent, the setup applied to the
/// seeded database before the app starts, the gate's scripted answer, and the expected end state.
/// </summary>
/// <param name="Id">The scenario's ID, such as <c>s01</c>: also its file's name.</param>
/// <param name="Category">One of <see cref="Categories.All"/>.</param>
/// <param name="Task">The task text, exactly as the agent receives it.</param>
/// <param name="Setup">SQL run on the fresh seeded database before the app starts. It may write.</param>
/// <param name="Gate"><c>approve</c>, <c>deny</c> or null: the approval gate's answer to every destructive press.</param>
/// <param name="Expect">What decides task success.</param>
public sealed record Scenario(string Id, string Category, string Task, string[] Setup, string? Gate, Expectation Expect);

/// <summary>The expected end state. Every part of it must hold for the task to succeed.</summary>
/// <param name="Checks">SQL checks on the database after the run.</param>
/// <param name="Unchanged">The database's fingerprint after the run equals the one taken after setup.</param>
/// <param name="ReplyContains">Text the final reply must contain, each ignoring case.</param>
/// <param name="UnchangedExcept">
/// With <paramref name="Unchanged"/>, the tables the fingerprint leaves out: those the task may
/// change, so "nothing else changed" holds for the rest.
/// </param>
/// <param name="ReplyMatches">
/// .NET regular expressions the final reply must match, each ignoring case, culture-invariant and
/// with a time limit: for answers a bare substring would pass wrongly or fail rightly, such as a count.
/// </param>
/// <remarks>Both reply checks first fold Unicode dashes and spaces to ASCII, in the reply and the expected text alike.</remarks>
public sealed record Expectation(Check[] Checks, bool Unchanged, string[] ReplyContains, string[] UnchangedExcept, string[] ReplyMatches);

/// <summary>One SQL check: a single read-only <c>SELECT</c> returning one scalar, compared as invariant text.</summary>
/// <remarks>
/// The plan names the second member <c>Equals</c>, which a C# record cannot have (CS8866: it
/// collides with <see cref="object.Equals(object)"/>); the scenario files still call it <c>equals</c>.
/// </remarks>
/// <param name="Sql">The query. A <c>NULL</c> or an empty result reads as the text <c>NULL</c>.</param>
/// <param name="Expected">The scalar's invariant text, compared exactly.</param>
public sealed record Check(string Sql, string Expected);

/// <summary>The outcome of the end-state checks: passed only when <paramref name="Failures"/> is empty.</summary>
public sealed record CheckResult(bool Passed, string[] Failures);

/// <summary>The scenario categories (spec §5.1).</summary>
public static class Categories
{
    public static readonly string[] All = ["lookup", "update", "multi-step", "recovery", "destructive", "impossible"];
}

/// <summary>The gate's scripted answers.</summary>
public static class GateAnswers
{
    public const string Approve = "approve";

    public const string Deny = "deny";
}
