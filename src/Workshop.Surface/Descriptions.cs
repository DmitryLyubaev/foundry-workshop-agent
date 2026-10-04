using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workshop.Surface;

/// <summary>One screen as the agent sees it: its fields, buttons and lists, in tab order.</summary>
public sealed record ScreenDescription(
    string Id,
    string Title,
    IReadOnlyList<FieldDescription> Fields,
    IReadOnlyList<ButtonDescription> Buttons,
    IReadOnlyList<ListDescription> Lists);

/// <summary>An input control. <see cref="Kind"/> is one of the values in <see cref="FieldKinds"/>.</summary>
public sealed record FieldDescription(
    string Id,
    string Label,
    string Kind,
    string? Value,
    IReadOnlyList<string>? Options,
    bool Enabled,
    bool Required,
    int? MaxLength);

public sealed record ButtonDescription(string Id, string Label, bool Enabled, bool Destructive);

public sealed record ListDescription(
    string Id,
    string Label,
    IReadOnlyList<string> Columns,
    IReadOnlyList<RowDescription> Rows);

/// <summary>A list row. <see cref="Key"/> is the row's stable name, used to select it.</summary>
public sealed record RowDescription(string Key, IReadOnlyList<string> Cells, bool Selected);

public static class FieldKinds
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Choice = "choice";
    public const string Checkbox = "checkbox";
    public const string Date = "date";
}

public static class SurfaceJson
{
    /// <summary>camelCase names, null values left out. Read-only, so no caller can change it.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
