namespace Workshop.Agent.Tools;

/// <summary>What the model is told each tool does: one sentence each, frozen with the scenarios.</summary>
public static class ToolDescriptions
{
    public const string ListScreens =
        "Lists the id and title of every screen the app can open, whichever screen is current.";

    public const string DescribeScreen =
        "Describes the current screen: its fields with their values, its buttons (destructive ones flagged), and its lists with their rows.";

    public const string OpenScreen =
        "Opens the screen with the given id, which becomes the current screen, and returns the outcome and the current screen.";

    public const string SetField =
        "Sets the field with the given id on the current screen to the given value, and returns the outcome and the current screen.";

    public const string SelectRow =
        "Selects the row with the given key in the list with the given id on the current screen, and returns the outcome and the current screen.";

    public const string PressButton =
        "Presses the button with the given id on the current screen, after the user's approval if it is destructive, and returns the outcome and the current screen.";
}
