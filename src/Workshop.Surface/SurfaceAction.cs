namespace Workshop.Surface;

/// <summary>
/// One action from the agent. <see cref="Type"/> is one of <see cref="ActionTypes"/>, and only the
/// target that type needs is read: a screen to open, a field and value to set, a list and row to
/// select, or a button to press.
/// </summary>
public sealed record SurfaceAction(string Type, string? Screen, string? Field, string? Value, string? List, string? Row, string? Button);

public static class ActionTypes
{
    public const string Open = "open";
    public const string Set = "set";
    public const string Select = "select";
    public const string Press = "press";
}
