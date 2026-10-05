using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Workshop.Agent.Engines;

/// <summary>
/// The model settings every engine sends with every model call, and the limits every run is held to,
/// the same for every model, so the study compares the models and not their settings. They are
/// frozen with the scenarios in plan 3 (<see cref="Sha256"/>): a change after that changes what the
/// study measures, and a study run under the old freeze refuses to start.
/// </summary>
public static class AgentSettings
{
    /// <summary>The most output tokens one model call may write; an answer stopped by it ends the run as <see cref="EngineOutcome.Truncated"/>.</summary>
    public const int MaxOutputTokens = 4096;

    /// <summary>The sampling temperature: null, stated, so each model samples at its own default.</summary>
    public static float? Temperature => null;

    /// <summary>The tool calls one run may make (spec §4.4): a call past them ends the run as <see cref="EngineOutcome.ToolLimit"/>.</summary>
    public const int MaxToolCalls = 25;

    /// <summary>How long one run may take (spec §4.4): past it the run ends as <see cref="EngineOutcome.TimeLimit"/>.</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(5);

    /// <summary>The waits for throttling one run may spend in all: a wait past it ends the run as <see cref="EngineOutcome.Throttled"/>.</summary>
    public static readonly TimeSpan ThrottleBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The settings as canonical JSON: camelCase names in ordinal order, no white space, and a null
    /// written as <c>null</c>. What <see cref="Sha256"/> is taken over.
    /// </summary>
    public static string CanonicalJson { get; } = CreateCanonicalJson();

    /// <summary>The SHA-256 of <see cref="CanonicalJson"/> as UTF-8, in lower-case hex: each transcript names the settings it ran with.</summary>
    public static string Sha256 { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson)));

    private static string CreateCanonicalJson()
    {
        // Written by hand, not serialised from a type, so the order of the names cannot drift with the code.
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            // The run limits too: each decides outcomes that count as task failures, so the freeze covers them.
            json.WriteStartObject();
            json.WriteNumber("maxOutputTokens", MaxOutputTokens);
            json.WriteNumber("maxToolCalls", MaxToolCalls);
            if (Temperature is { } temperature)
            {
                json.WriteNumber("temperature", temperature);
            }
            else
            {
                json.WriteNull("temperature");
            }

            json.WriteNumber("throttleBudgetSeconds", ThrottleBudget.TotalSeconds);
            json.WriteNumber("timeLimitSeconds", TimeLimit.TotalSeconds);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
