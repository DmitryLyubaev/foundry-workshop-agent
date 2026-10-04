using Workshop.Core;

namespace Workshop.App;

/// <summary>
/// The app's window: a menu for a person to move between screens, and below it the one screen
/// showing. The agent moves between screens through <see cref="Navigator"/> instead.
/// </summary>
internal sealed class MainForm : Form
{
    private const string AppTitle = "Workshop";

    public MainForm(JobService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        Text = AppTitle;
        ClientSize = new Size(1000, 720);
        MinimumSize = new Size(720, 520);
        StartPosition = FormStartPosition.CenterScreen;

        var host = new Panel { Dock = DockStyle.Fill };
        var menu = new MenuStrip();

        // The fill panel goes in first, so the menu docks above it rather than over it.
        Controls.Add(host);
        Controls.Add(menu);
        MainMenuStrip = menu;

        Navigator = new WorkshopNavigator(host, service);
        foreach (var (id, text) in new[] { ("job-list", "&Jobs"), ("new-job", "&New job"), ("customer-list", "&Customers"), ("parts", "&Parts") })
        {
            menu.Items.Add(text, null, (_, _) => Navigator.Open(id));
        }

        Navigator.CurrentChanged += (_, _) => ShowTitle();
        ShowTitle();
    }

    public WorkshopNavigator Navigator { get; }

    private void ShowTitle() => Text = $"{AppTitle} - {Navigator.CurrentScreen.Title}";
}
