using System.Runtime.CompilerServices;

namespace Workshop.Surface;

/// <summary>
/// How the app tells the agent surface that a rule refused an action. A handler calls
/// <see cref="Fail"/> as well as showing the message on screen; the action executor reads the
/// message after a press. The message is kept per screen, beside it, so screens on other UI
/// threads never see each other's refusals.
/// </summary>
public static class SurfaceFeedback
{
    private static readonly ConditionalWeakTable<Control, string> Failures = [];

    /// <summary>
    /// Records that a rule refused the action on the screen holding <paramref name="anyControlOnScreen"/>.
    /// A later call on the same screen replaces the message.
    /// </summary>
    /// <exception cref="ArgumentException">The control is not on a screen marked with <see cref="Surface.Screen"/>.</exception>
    public static void Fail(Control anyControlOnScreen, string message)
    {
        ArgumentNullException.ThrowIfNull(anyControlOnScreen);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var screen = ScreenOf(anyControlOnScreen) ?? throw new ArgumentException(
            $"The control '{anyControlOnScreen.Name}' ({anyControlOnScreen.GetType().Name}) is not on a screen.",
            nameof(anyControlOnScreen));

        Failures.AddOrUpdate(screen, message);
    }

    /// <summary>Returns the screen's refusal, if any, and clears it.</summary>
    internal static string? Take(Control screen) =>
        Failures.TryGetValue(screen, out var message) && Failures.Remove(screen) ? message : null;

    private static Control? ScreenOf(Control control)
    {
        for (Control? current = control; current is not null; current = current.Parent)
        {
            if (Surface.ScreenOf(current) is not null)
            {
                return current;
            }
        }

        return null;
    }
}
