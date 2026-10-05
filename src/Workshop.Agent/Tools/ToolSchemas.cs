using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Workshop.Agent.Tools;

/// <summary>
/// The one definition of "the tools" the study holds still: each tool's name, description and JSON
/// schema, as canonical JSON and its SHA-256. The GPT prompt agent's version is checked against it,
/// and the freeze records it.
/// </summary>
/// <remarks>
/// Canonical JSON: the tools sorted by name, each <c>{"description","name","parameters"}</c>; in
/// every schema the names in ordinal order and no white space. An empty <c>required</c> list is left
/// out, as JSON Schema reads it the same as none, and an SDK or a service may write either.
/// </remarks>
public static class ToolSchemas
{
    // Descriptions are read by models, never put into a page: apostrophes stay as they are.
    private static readonly JsonWriterOptions Writer = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The parameters schema the study gives a tool: its JSON schema as a closed object
    /// (<c>additionalProperties: false</c>), as both model SDKs send it on their own.
    /// </summary>
    public static JsonElement Parameters(AIFunctionDeclaration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var schema = JsonNode.Parse(tool.JsonSchema.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException($"The tool '{tool.Name}' has a schema that is not a JSON object.");
        schema.TryAdd("additionalProperties", false);
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>The tools as canonical JSON.</summary>
    public static string CanonicalJson(IReadOnlyList<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return CanonicalJson(tools.Select(Entry));
    }

    /// <summary>The SHA-256 of <see cref="CanonicalJson(IReadOnlyList{AIFunction})"/> as UTF-8, in lower-case hex.</summary>
    public static string Sha256(IReadOnlyList<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return Sha256(tools.Select(Entry));
    }

    /// <summary>Tools given as their parts, as an agent definition or a request holds them.</summary>
    internal static string CanonicalJson(IEnumerable<(string Name, string? Description, JsonElement Parameters)> tools)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Writer))
        {
            json.WriteStartArray();
            foreach (var (name, description, parameters) in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                json.WriteStartObject();
                json.WriteString("description", description);
                json.WriteString("name", name);
                json.WritePropertyName("parameters");
                WriteCanonical(json, parameters);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The SHA-256 of tools given as their parts.</summary>
    internal static string Sha256(IEnumerable<(string Name, string? Description, JsonElement Parameters)> tools) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson(tools))));

    /// <summary>Tools as the loop holds them, as their parts; null when one of them is not a function.</summary>
    internal static string? Sha256OrNull(IEnumerable<AITool> tools)
    {
        var list = tools.ToList();
        return list.All(t => t is AIFunctionDeclaration)
            ? Sha256(list.Cast<AIFunctionDeclaration>().Select(Entry))
            : null;
    }

    private static (string Name, string? Description, JsonElement Parameters) Entry(AIFunctionDeclaration tool) =>
        (tool.Name, tool.Description, Parameters(tool));

    private static void WriteCanonical(Utf8JsonWriter json, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                json.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (property.Name == "required" && property.Value is { ValueKind: JsonValueKind.Array } required && required.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    json.WritePropertyName(property.Name);
                    WriteCanonical(json, property.Value);
                }

                json.WriteEndObject();
                break;
            case JsonValueKind.Array:
                json.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(json, item);
                }

                json.WriteEndArray();
                break;
            default:
                element.WriteTo(json);
                break;
        }
    }
}
