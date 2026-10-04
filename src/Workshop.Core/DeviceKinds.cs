namespace Workshop.Core;

/// <summary>The kinds of device the workshop takes in.</summary>
public static class DeviceKinds
{
    public static IReadOnlyList<string> All { get; } = ["laptop", "desktop", "phone", "tablet", "printer", "other"];
}
