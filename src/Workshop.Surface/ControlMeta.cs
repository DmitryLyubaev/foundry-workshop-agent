namespace Workshop.Surface;

/// <summary>What the agent surface knows about one control: its stable ID, label and flags.</summary>
public sealed record ControlMeta(string Id, string Label, bool Required = false, bool Destructive = false, int? MaxLength = null);
