using System.Text.RegularExpressions;

namespace Workshop.Agent.Scenarios;

/// <summary>
/// The rule for a scenario's checks: one statement, and a <c>SELECT</c>. In SQLite a <c>SELECT</c>
/// cannot write, so the rule only has to keep a second statement out: any <c>;</c> but one at the
/// end refuses the text, even one inside a string literal. The checker also opens the database
/// read-only, so a check that got past this would still change nothing.
/// </summary>
public static partial class ReadOnlySql
{
    public static bool IsSingleSelect(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        var text = sql.Trim();
        if (text.EndsWith(';'))
        {
            text = text[..^1].TrimEnd();
        }

        return !text.Contains(';', StringComparison.Ordinal) && StartsWithSelect().IsMatch(text);
    }

    [GeneratedRegex(@"\Aselect\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartsWithSelect();
}
