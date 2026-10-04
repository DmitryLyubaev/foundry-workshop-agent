using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Workshop.Agent.Tools;

/// <summary>
/// Reads the model's arguments without ever throwing. A model may leave an argument out, send it
/// empty, send null or send the wrong JSON type; each is a problem the tool answers as
/// <c>bad_arguments</c>, so the model can read it and try again (Review Focus 3).
/// </summary>
internal static class ToolArguments
{
    /// <summary>The one argument that may be empty, and may be a number or a boolean, as the endpoint allows.</summary>
    public const string ValueName = "value";

    // The model's arguments may hold any JSON, so the AI library's options write them; compactly, for the transcript.
    private static readonly JsonSerializerOptions SnapshotJson = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    /// <summary>
    /// Binds each string parameter leniently: the argument's text, or null when it has a problem.
    /// The default binding would throw on a missing or mistyped argument before the tool could
    /// answer; the tool checks <see cref="FirstProblem"/> before it uses a bound value.
    /// </summary>
    public static AIFunctionFactoryOptions.ParameterBindingOptions Bind(ParameterInfo parameter) =>
        parameter.ParameterType == typeof(string)
            ? new() { BindParameter = static (p, arguments) => Read(arguments, p.Name!).Text }
            : default;

    /// <summary>The first problem with the required arguments, in the order given, or null if there is none.</summary>
    public static string? FirstProblem(AIFunctionArguments arguments, IEnumerable<string> required) =>
        required.Select(name => Read(arguments, name).Problem).FirstOrDefault(problem => problem is not null);

    /// <summary>The arguments as the model sent them, as a JSON object, for the tool record.</summary>
    public static JsonElement Snapshot(AIFunctionArguments arguments)
    {
        try
        {
            return JsonSerializer.SerializeToElement<IDictionary<string, object?>>(arguments, SnapshotJson);
        }
        catch (Exception e) when (e is NotSupportedException or JsonException)
        {
            // Only a caller that passes objects the serialiser cannot write gets here; a model's arguments are JSON.
            return JsonSerializer.SerializeToElement(new Dictionary<string, string>(), SnapshotJson);
        }
    }

    private static (string? Text, string? Problem) Read(AIFunctionArguments arguments, string name)
    {
        var isValue = name == ValueName;
        if (!arguments.TryGetValue(name, out var raw) || raw is null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return (null, $"The argument '{name}' is missing.");
        }

        var text = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            // The endpoint takes a number or a boolean value as its JSON text; so does the tool.
            JsonElement { ValueKind: JsonValueKind.Number } e when isValue => e.GetRawText(),
            JsonElement { ValueKind: JsonValueKind.True } when isValue => "true",
            JsonElement { ValueKind: JsonValueKind.False } when isValue => "false",
            bool b when isValue => b ? "true" : "false",
            int or long or short or byte or sbyte or uint or ulong or ushort or decimal or double or float when isValue
                => ((IFormattable)raw).ToString(null, CultureInfo.InvariantCulture),
            _ => null,
        };

        if (text is null)
        {
            return (null, isValue
                ? $"The argument '{name}' must be a string, a number, true or false."
                : $"The argument '{name}' must be a string.");
        }

        // A value may be empty, to clear a field; the id of a screen, field, list or button, or a row's key, may not.
        return !isValue && string.IsNullOrWhiteSpace(text) ? (null, $"The argument '{name}' is empty.") : (text, null);
    }
}
