using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Workshop.Agent.Scenarios;

/// <summary>
/// Decides a run's end state from the app's database after the app has closed (spec §5.2): the
/// scenario's SQL checks, its unchanged fingerprint, and the text its final reply must contain.
/// The database is only ever opened read-only.
/// </summary>
public static class EndStateChecker
{
    private const char TableSeparator = ';';
    private const char HashSeparator = '=';

    /// <summary>Runs every part of <paramref name="s"/>'s expectation, and reports each that fails.</summary>
    /// <param name="dbFingerprintBefore">The <see cref="Fingerprint"/> taken after setup, before the app started.</param>
    public static CheckResult Check(Scenario s, string dbPath, string dbFingerprintBefore, string? finalReply)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(dbFingerprintBefore);

        var failures = new List<string>();
        using (var conn = OpenReadOnly(dbPath))
        {
            for (var i = 0; i < s.Expect.Checks.Length; i++)
            {
                if (RunCheck(conn, s.Expect.Checks[i]) is { } problem)
                {
                    failures.Add($"Check {i + 1} ({s.Expect.Checks[i].Sql}) {problem}");
                }
            }
        }

        if (s.Expect.Unchanged)
        {
            var changed = ChangedTables(dbFingerprintBefore, Fingerprint(dbPath), s.Expect.UnchangedExcept);
            if (changed.Length > 0)
            {
                failures.Add($"The database changed: {string.Join(", ", changed)}.");
            }
        }

        var reply = finalReply is null ? null : ReplyText.Normalise(finalReply);
        foreach (var text in s.Expect.ReplyContains)
        {
            if (reply is null || !reply.Contains(ReplyText.Normalise(text), StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"The reply does not contain '{text}'.");
            }
        }

        foreach (var pattern in s.Expect.ReplyMatches)
        {
            if (MatchReply(reply, pattern) is { } problem)
            {
                failures.Add(problem);
            }
        }

        return new CheckResult(failures.Count == 0, [.. failures]);
    }

    /// <summary>
    /// A SHA-256 of each table's rows, in primary-key order (row order for a table without one), as
    /// <c>&lt;table&gt;=&lt;hex&gt;</c> joined by <c>;</c> in table-name order. Kept per table, so
    /// <see cref="Expectation.UnchangedExcept"/> can leave tables out of the comparison.
    /// </summary>
    public static string Fingerprint(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        using var conn = OpenReadOnly(dbPath);
        var tables = Column(conn, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name");
        return string.Join(TableSeparator, tables.Select(t => $"{t}{HashSeparator}{HashTable(conn, t)}"));
    }

    private static string? RunCheck(SqliteConnection conn, Check check)
    {
        // The loader refuses these; a scenario built in code reaches here unchecked.
        if (!ReadOnlySql.IsSingleSelect(check.Sql))
        {
            return "is not a single read-only SELECT.";
        }

        string actual;
        try
        {
            using var command = conn.CreateCommand();
            command.CommandText = check.Sql;
            using var reader = command.ExecuteReader();

            // One scalar: taking the first cell of a wider answer would hide a check that is wrong.
            if (reader.FieldCount != 1)
            {
                return $"returns {reader.FieldCount} columns, not one scalar.";
            }

            actual = reader.Read() ? Text(reader.GetValue(0)) : Text(null);
            if (reader.Read())
            {
                return "returns more than one row, not one scalar.";
            }
        }
        catch (SqliteException e)
        {
            return $"failed: {e.Message}";
        }

        return actual == check.Expected ? null : $"gave '{actual}', expected '{check.Expected}'.";
    }

    private static string? MatchReply(string? reply, string pattern)
    {
        Regex regex;
        try
        {
            regex = ReplyText.Pattern(pattern);
        }
        catch (ArgumentException e)
        {
            // The loader refuses these; a scenario built in code reaches here unchecked.
            return $"The reply pattern '{pattern}' is not a valid regular expression: {e.Message}";
        }

        try
        {
            return reply is not null && regex.IsMatch(reply) ? null : $"The reply does not match '{pattern}'.";
        }
        catch (RegexMatchTimeoutException)
        {
            return $"The reply pattern '{pattern}' timed out after {ReplyText.MatchTimeout.TotalSeconds:0.#} s.";
        }
    }

    private static string Text(object? value) => value switch
    {
        null or DBNull => "NULL",
        byte[] blob => Convert.ToHexString(blob),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
    };

    private static string[] ChangedTables(string before, string after, string[] except)
    {
        var was = Parse(before);
        var now = Parse(after);
        return [.. was.Keys.Union(now.Keys)
            .Where(t => !except.Contains(t, StringComparer.Ordinal))
            .Where(t => was.GetValueOrDefault(t) != now.GetValueOrDefault(t))
            .Order(StringComparer.Ordinal)];
    }

    private static Dictionary<string, string> Parse(string fingerprint) =>
        fingerprint.Split(TableSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split(HashSeparator, 2))
            .ToDictionary(parts => parts[0], parts => parts.Length == 2 ? parts[1] : "", StringComparer.Ordinal);

    private static string HashTable(SqliteConnection conn, string table)
    {
        // Table and column names come from the database's own catalogue, quoted, never from input.
        var keys = Column(conn, $"SELECT name FROM pragma_table_info({Literal(table)}) WHERE pk > 0 ORDER BY pk");
        var order = keys.Length > 0 ? string.Join(", ", keys.Select(Quote)) : "rowid";

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var command = conn.CreateCommand();
        command.CommandText = $"SELECT * FROM {Quote(table)} ORDER BY {order}";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hash.AppendData("R"u8);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                AppendValue(hash, reader.GetValue(i));
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Each value as its type and its length-prefixed bytes, so neither a type nor a boundary can blur.</summary>
    private static void AppendValue(IncrementalHash hash, object value)
    {
        var (tag, bytes) = value switch
        {
            DBNull => ((byte)'N', Array.Empty<byte>()),
            long l => ((byte)'I', BitConverter.GetBytes(l)),
            double d => ((byte)'F', BitConverter.GetBytes(d)),
            string s => ((byte)'T', Encoding.UTF8.GetBytes(s)),
            byte[] b => ((byte)'B', b),
            _ => throw new InvalidDataException($"An unexpected SQLite value of type {value.GetType()}."),
        };
        hash.AppendData([tag]);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static string[] Column(SqliteConnection conn, string sql)
    {
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>The only way this class opens a database: read-only, so no check can write.</summary>
    internal static SqliteConnection OpenReadOnly(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            throw new FileNotFoundException($"There is no database at '{dbPath}'.", dbPath);
        }

        // Without pooling, the file is free as soon as the connection closes, for the runner to delete.
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }
}
