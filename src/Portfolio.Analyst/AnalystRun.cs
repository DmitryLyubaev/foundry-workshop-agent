using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;

namespace Portfolio.Analyst;

/// <summary>One frozen question.</summary>
/// <param name="Id">Its id, such as <c>q01</c>.</param>
/// <param name="Text">What the analyst is asked.</param>
/// <param name="Optional">Marked optional (q12): still asked here; the Fabric side decides whether it is scored.</param>
public sealed record AnalystQuestion(string Id, string Text, bool Optional);

/// <summary>
/// A run of the analyst: the prompt agent provisioned (<see cref="StartAsync"/>), then every frozen
/// question asked once per pass (<see cref="AskAllAsync"/>), each in a fresh conversation, with its
/// answer text, raw output, tool calls, tokens and wall time recorded.
/// </summary>
public static partial class AnalystRun
{
    /// <summary>
    /// Provisions the <c>portfolio-analyst</c> prompt agent (<see cref="AnalystAgent.EnsureAsync"/>)
    /// and returns the engine over the version it read back. <paramref name="transport"/> is for tests.
    /// </summary>
    public static async Task<IAnalystEngine> StartAsync(AnalystSetup setup, TokenCredential credential, PipelineTransport? transport, CancellationToken ct)
    {
        var version = await AnalystAgent.EnsureAsync(setup, credential, transport, ct).ConfigureAwait(false);
        return AnalystAgent.CreateEngine(setup, version, credential, transport);
    }

    /// <summary>
    /// The questions in <c>questions.json</c>, all of them and in their frozen order. Throws
    /// <see cref="InvalidDataException"/> (fixed text) when the file is not a non-empty array of
    /// questions with a lowercase id, unique, and a text.
    /// </summary>
    public static IReadOnlyList<AnalystQuestion> ReadQuestions(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("--questions: not valid JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                throw new InvalidDataException("--questions: not a non-empty array of questions");
            }

            var questions = new List<AnalystQuestion>();
            foreach (var (question, index) in document.RootElement.EnumerateArray().Select((q, i) => (q, i + 1)))
            {
                if (question.ValueKind != JsonValueKind.Object
                    || !question.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !QuestionId().IsMatch(id.GetString()!)
                    || !question.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString()))
                {
                    throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"--questions: question {index} needs an id such as q01 and a text"));
                }

                if (questions.Any(q => q.Id == id.GetString()))
                {
                    throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"--questions: question {index} repeats an id"));
                }

                var optional = question.TryGetProperty("optional", out var flag) && flag.ValueKind == JsonValueKind.True;
                questions.Add(new AnalystQuestion(id.GetString()!, text.GetString()!, optional));
            }

            return questions;
        }
    }

    /// <summary>
    /// Asks every question once per pass, in order, and writes each record as soon as its answer
    /// arrives. A failed question stops the run: it is not an answer, so it is never scored as a wrong one.
    /// The console gets one line per answer, its id, pass and wall time, never its text.
    /// </summary>
    public static async Task AskAllAsync(IAnalystEngine engine, IReadOnlyList<AnalystQuestion> questions, int passes, RecordWriter writer, TextWriter output, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(passes, 3);

        for (var pass = 1; pass <= passes; pass++)
        {
            foreach (var question in questions)
            {
                var started = time.GetUtcNow();
                var clock = time.GetTimestamp();
                var answer = await engine.AskAsync(question.Text, ct).ConfigureAwait(false);
                var wallMs = (long)Math.Round(time.GetElapsedTime(clock).TotalMilliseconds);
                writer.Write(new AnalystRecord(question.Id, pass, answer, wallMs, started));
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{question.Id} pass {pass}: {wallMs / 1000.0:0.0} s")).ConfigureAwait(false);
            }
        }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex QuestionId();
}
