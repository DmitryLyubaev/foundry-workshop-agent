using System.Text;
using System.Text.RegularExpressions;

namespace Workshop.Agent.Scenarios;

/// <summary>
/// How a final reply is compared. Models differ in typography, not in answers: one writes
/// <c>J‑1007</c> with a non-breaking hyphen, another with a plain one. So the reply and the expected
/// text both fold Unicode dashes and spaces to ASCII before any reply check.
/// </summary>
public static class ReplyText
{
    /// <summary>How long one <see cref="Expectation.ReplyMatches"/> pattern may run; past it, the check fails.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>U+2010–U+2015 and U+2212 become <c>-</c>; U+00A0, U+202F and U+2007 become a space.</summary>
    public static string Normalise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var folded = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            folded.Append(c switch
            {
                >= '‐' and <= '―' or '−' => '-',
                ' ' or ' ' or ' ' => ' ',
                _ => c,
            });
        }

        return folded.ToString();
    }

    /// <summary>A <see cref="Expectation.ReplyMatches"/> pattern, normalised, case-insensitive, culture-invariant and time-limited.</summary>
    /// <exception cref="ArgumentException">The pattern is not a valid .NET regular expression.</exception>
    public static Regex Pattern(string pattern) =>
        new(Normalise(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
}
