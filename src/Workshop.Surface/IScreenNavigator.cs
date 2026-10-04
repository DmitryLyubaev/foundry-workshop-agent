namespace Workshop.Surface;

/// <summary>The app's shell, as the agent surface sees it: the screens it can open and the one showing.</summary>
public interface IScreenNavigator
{
    /// <summary>Every screen that can be opened, by ID and title.</summary>
    IReadOnlyList<(string Id, string Title)> Screens { get; }

    /// <summary>The screen showing now: a control marked with <see cref="Surface.Screen"/>, and shown.</summary>
    Control Current { get; }

    /// <summary>Opens the screen with this ID; false if it could not be opened.</summary>
    bool Open(string id);
}
