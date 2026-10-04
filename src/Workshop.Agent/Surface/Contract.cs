using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workshop.Agent.Surface;

// The client's copy of the endpoint's JSON shapes (README, "Contract for clients"). The client
// keeps its own records rather than referencing Workshop.Surface, a WinForms library, and
// ContractTests checks that the two agree. An absent property reads as null.

/// <summary>One entry of <c>GET /screens</c>.</summary>
public sealed record ScreenInfo(string Id, string Title);

/// <summary>A screen as the agent sees it: its fields, buttons and lists, each in tab order.</summary>
public sealed record Screen(string Id, string Title, Field[] Fields, Button[] Buttons, ListView[] Lists);

/// <summary>An input control. <see cref="Kind"/> is <c>text</c>, <c>number</c>, <c>choice</c>, <c>checkbox</c> or <c>date</c>.</summary>
public sealed record Field(string Id, string Label, string Kind, string? Value, string[]? Options, bool Enabled, bool Required, int? MaxLength);

public sealed record Button(string Id, string Label, bool Enabled, bool Destructive);

public sealed record ListView(string Id, string Label, string[] Columns, Row[] Rows);

/// <summary>A list row. <see cref="Key"/> is the row's stable name, used to select it.</summary>
public sealed record Row(string Key, string[] Cells, bool Selected);

/// <summary>
/// One <c>POST /actions</c> body. <see cref="Type"/> is <c>open</c>, <c>set</c>, <c>select</c> or
/// <c>press</c>, and only that type's targets are read; the factories set exactly those.
/// </summary>
public sealed record ActionRequest(string Type, string? Screen, string? Field, string? Value, string? List, string? Row, string? Button)
{
    public static ActionRequest Open(string screen) => new("open", screen, null, null, null, null, null);

    public static ActionRequest Set(string field, string value) => new("set", null, field, value, null, null, null);

    public static ActionRequest Select(string list, string row) => new("select", null, null, null, list, row, null);

    public static ActionRequest Press(string button) => new("press", null, null, null, null, null, button);
}

/// <summary>What an action did: its outcome, the app's message if it has one, and the current screen after it.</summary>
public sealed record ActionReply(string Outcome, string? Message, Screen Screen);

public static class ContractJson
{
    /// <summary>
    /// The endpoint's conventions: camelCase names, and null values left out, so an action sends
    /// only its own targets. A null where the contract has no null (a missing screen, say) is
    /// refused rather than read. Read-only, so no caller can change it.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
