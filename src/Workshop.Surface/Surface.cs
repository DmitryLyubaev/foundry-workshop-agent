using System.Runtime.CompilerServices;

namespace Workshop.Surface;

/// <summary>
/// Attaches agent-surface metadata to controls and screens. It is kept beside the control, not in
/// <see cref="Control.Tag"/>, so a form stays free to use Tag for its own purposes, and it goes
/// away with the control.
/// </summary>
public static class Surface
{
    private static readonly ConditionalWeakTable<Control, ControlMeta> Metas = [];
    private static readonly ConditionalWeakTable<Control, ScreenInfo> Screens = [];

    /// <summary>Sets the control's metadata, replacing any earlier one, and returns the control.</summary>
    public static T Meta<T>(this T control, string id, string label, bool required = false, bool destructive = false, int? maxLength = null)
        where T : Control
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        Metas.AddOrUpdate(control, new ControlMeta(id, label, required, destructive, maxLength));
        return control;
    }

    /// <summary>Marks <paramref name="root"/> as a screen the describer can describe.</summary>
    public static void Screen(Control root, string id, string title)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        Screens.AddOrUpdate(root, new ScreenInfo(id, title));
    }

    public static ControlMeta? MetaOf(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return Metas.TryGetValue(control, out var meta) ? meta : null;
    }

    public static (string Id, string Title)? ScreenOf(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return Screens.TryGetValue(control, out var screen) ? (screen.Id, screen.Title) : null;
    }

    private sealed record ScreenInfo(string Id, string Title);
}
