using System.Runtime.CompilerServices;

namespace Workshop.Surface;

/// <summary>
/// How the app tells the agent surface what came of a press. A handler calls <see cref="Fail"/>
/// when a rule refused the action, or <see cref="Inform"/> when the action succeeded with something
/// to say, as well as showing the message on screen; the action executor reads the message after a
/// press. The message is kept per screen, beside it, so screens on other UI threads never see each
/// other's messages.
/// </summary>
public static class SurfaceFeedback
{
    private static readonly ConditionalWeakTable<Control, Feedback> Pending = [];

    /// <summary>
    /// Records that a rule refused the action on the screen holding <paramref name="anyControlOnScreen"/>.
    /// It replaces any earlier message on the same screen.
    /// </summary>
    /// <exception cref="ArgumentException">The control is not on a screen marked with <see cref="Surface.Screen"/>.</exception>
    public static void Fail(Control anyControlOnScreen, string message) =>
        Pending.AddOrUpdate(ScreenOf(anyControlOnScreen, message), new Feedback(Failed: true, message));

    /// <summary>
    /// Records the app's message for an action that succeeded on the screen holding
    /// <paramref name="anyControlOnScreen"/>. It replaces earlier information on the same screen, but
    /// never a refusal: a press that was refused reports the refusal.
    /// </summary>
    /// <exception cref="ArgumentException">The control is not on a screen marked with <see cref="Surface.Screen"/>.</exception>
    public static void Inform(Control anyControlOnScreen, string message)
    {
        var screen = ScreenOf(anyControlOnScreen, message);
        if (!(Pending.TryGetValue(screen, out var earlier) && earlier.Failed))
        {
            Pending.AddOrUpdate(screen, new Feedback(Failed: false, message));
        }
    }

    /// <summary>Returns the screen's message, if any, and clears it.</summary>
    internal static Feedback? Take(Control screen) =>
        Pending.TryGetValue(screen, out var feedback) && Pending.Remove(screen) ? feedback : null;

    private static Control ScreenOf(Control anyControlOnScreen, string message)
    {
        ArgumentNullException.ThrowIfNull(anyControlOnScreen);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        for (Control? current = anyControlOnScreen; current is not null; current = current.Parent)
        {
            if (Surface.ScreenOf(current) is not null)
            {
                return current;
            }
        }

        throw new ArgumentException(
            $"The control '{anyControlOnScreen.Name}' ({anyControlOnScreen.GetType().Name}) is not on a screen.",
            nameof(anyControlOnScreen));
    }

    /// <summary>A press's message: a refusal if <see cref="Failed"/>, otherwise information.</summary>
    internal sealed record Feedback(bool Failed, string Message);
}
