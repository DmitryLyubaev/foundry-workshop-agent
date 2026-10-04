using Workshop.App.Screens;
using Workshop.Surface;

namespace Workshop.App.Tests;

/// <summary>
/// Review Focus 5: every control a person can use on any screen has metadata, so nothing usable is
/// invisible to the agent. The screens are the real app's, with a job and a customer open.
/// </summary>
public sealed class MetadataCoverageTests
{
    /// <summary>The control IDs of each screen, in tab order, as plan 2's scenarios use them.</summary>
    private static readonly Dictionary<string, Expected> Table = new()
    {
        ["job-list"] = new(
            Fields: ["search", "status-filter"],
            Buttons: ["open-job", "new-job"],
            Lists: ["jobs"]),
        ["job-detail"] = new(
            Fields: ["job", "status", "fault", "part", "quantity", "note"],
            Buttons: ["save-status", "add-part", "fit-ordered-parts", "add-note", "cancel-job"],
            Lists: ["parts", "notes"]),
        ["new-job"] = new(
            Fields: ["customer", "device", "fault"],
            Buttons: ["book-in"],
            Lists: []),
        ["customer-list"] = new(
            Fields: ["search"],
            Buttons: ["open-customer"],
            Lists: ["customers"]),
        ["customer-detail"] = new(
            Fields: ["name", "phone", "kind", "model", "serial"],
            Buttons: ["add-device"],
            Lists: ["devices"]),
        ["parts"] = new(
            Fields: ["quantity"],
            Buttons: ["receive-stock"],
            Lists: ["parts"]),
    };

    [Fact]
    public void Every_interactive_control_on_every_screen_has_metadata()
    {
        using var app = AppHost.Start();

        var missing = app.OnUi(form => EveryScreen(form)
            .SelectMany(screen => AllControls(screen).Where(IsInteractive).Where(c => Surface.Surface.MetaOf(c) is null)
                .Select(c => $"{screen.Id}: {c.GetType().Name} '{c.Name}' ({c.Text})"))
            .ToList());

        Assert.Empty(missing);
        Assert.Equal(Table.Keys.Order(), app.OnUi(form => form.Navigator.All.Select(s => s.Id).Order().ToList()));

        // Every control on an open screen is showing, so each one is described: none is left out.
        var counts = app.OnUi(form => EveryScreen(form).Select(screen =>
        {
            var described = ScreenDescriber.Describe(screen);
            return (screen.Id, Controls: AllControls(screen).Count(IsInteractive),
                Described: described.Fields.Count + described.Buttons.Count + described.Lists.Count);
        }).ToList());
        Assert.All(counts, c => Assert.Equal(c.Controls, c.Described));
        Assert.Equal(35, counts.Sum(c => c.Controls));
    }

    [Fact]
    public void Every_screen_describes_without_throwing_and_ids_match_the_table()
    {
        using var app = AppHost.Start();

        var described = app.OnUi(form => EveryScreen(form).ToDictionary(s => s.Id, ScreenDescriber.Describe));

        Assert.Equal(Table.Keys.Order(), described.Keys.Order());
        foreach (var (id, expected) in Table)
        {
            var screen = described[id];
            Assert.Equal(id, screen.Id);
            Assert.Equal(expected.Fields, screen.Fields.Select(f => f.Id));
            Assert.Equal(expected.Buttons, screen.Buttons.Select(b => b.Id));
            Assert.Equal(expected.Lists, screen.Lists.Select(l => l.Id));
        }

        // The details the table gives beyond the IDs.
        var jobList = described["job-list"];
        Assert.Equal(["all", "booked in", "diagnosing", "waiting on parts", "in repair", "ready", "collected", "cancelled"],
            Field(jobList, "status-filter").Options!);
        Assert.Equal(["Job", "Customer", "Device", "Status"], List(jobList, "jobs").Columns);

        var jobDetail = described["job-detail"];
        Assert.Equal(FieldKinds.Text, Field(jobDetail, "job").Kind);
        Assert.False(Field(jobDetail, "job").Enabled);
        Assert.Equal(FieldKinds.Choice, Field(jobDetail, "status").Kind);
        Assert.False(Field(jobDetail, "fault").Enabled);
        Assert.Equal(FieldKinds.Choice, Field(jobDetail, "part").Kind);
        Assert.Equal(FieldKinds.Number, Field(jobDetail, "quantity").Kind);
        Assert.Equal(1000, Field(jobDetail, "note").MaxLength);
        Assert.Equal(["cancel-job"], jobDetail.Buttons.Where(b => b.Destructive).Select(b => b.Id));

        var newJob = described["new-job"];
        Assert.Equal(500, Field(newJob, "fault").MaxLength);
        Assert.All(newJob.Fields, f => Assert.True(f.Required, f.Id));

        var customerDetail = described["customer-detail"];
        Assert.False(Field(customerDetail, "name").Enabled);
        Assert.False(Field(customerDetail, "phone").Enabled);
        Assert.Equal(["laptop", "desktop", "phone", "tablet", "printer", "other"], Field(customerDetail, "kind").Options!);

        Assert.Equal(["Part", "Name", "Stock"], List(described["parts"], "parts").Columns);
        Assert.Equal(FieldKinds.Number, Field(described["parts"], "quantity").Kind);
    }

    [Fact]
    public void Job_detail_quantity_runs_from_1_to_20()
    {
        using var app = AppHost.Start();

        var (minimum, maximum) = app.OnUi(form =>
        {
            var screen = EveryScreen(form).Single(s => s.Id == "job-detail");
            var quantity = AllControls(screen).OfType<NumericUpDown>().Single(c => Surface.Surface.MetaOf(c)!.Id == "quantity");
            return (quantity.Minimum, quantity.Maximum);
        });

        Assert.Equal(1m, minimum);
        Assert.Equal(20m, maximum);
    }

    [Fact]
    public void Every_text_box_with_a_maximum_declares_it()
    {
        using var app = AppHost.Start();

        var mismatched = app.OnUi(form => EveryScreen(form)
            .SelectMany(screen => AllControls(screen).OfType<TextBox>()
                .Where(box => (box.MaxLength == DefaultMaxLength ? null : box.MaxLength) != Surface.Surface.MetaOf(box)!.MaxLength)
                .Select(box => $"{screen.Id}/{Surface.Surface.MetaOf(box)!.Id}"))
            .ToList());

        Assert.Empty(mismatched);
    }

    [Fact]
    public void No_text_box_is_enabled_and_read_only()
    {
        using var app = AppHost.Start();

        var offenders = app.OnUi(form => EveryScreen(form)
            .SelectMany(screen => AllControls(screen).OfType<TextBox>()
                .Where(box => box.ReadOnly && box.Enabled)
                .Select(box => $"{screen.Id}/{Surface.Surface.MetaOf(box)!.Id}"))
            .ToList());

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_button_skips_validation()
    {
        using var app = AppHost.Start();

        var offenders = app.OnUi(form => EveryScreen(form)
            .SelectMany(screen => AllControls(screen).OfType<Button>()
                .Where(button => button.CausesValidation)
                .Select(button => $"{screen.Id}/{Surface.Surface.MetaOf(button)!.Id}"))
            .ToList());

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_list_row_has_a_unique_non_empty_name()
    {
        using var app = AppHost.Start();

        // J-1006 has a part fitted; adding the same part again gives the parts list a repeated part.
        Assert.True(app.Jobs.AddPart("J-1006", "P-03", 1).Ok);
        Assert.True(app.Jobs.AddNote("J-1006", "First note.").Ok);
        Assert.True(app.Jobs.AddNote("J-1006", "Second note, likely in the same second.").Ok);

        var lists = app.OnUi(form => EveryScreen(form, jobId: "J-1006")
            .SelectMany(screen => AllControls(screen).OfType<ListView>()
                .Select(list => (Where: $"{screen.Id}/{Surface.Surface.MetaOf(list)!.Id}",
                    Names: list.Items.Cast<ListViewItem>().Select(item => item.Name).ToList())))
            .ToList());

        Assert.Equal(6, lists.Count);
        Assert.All(lists, list =>
        {
            Assert.NotEmpty(list.Names);
            Assert.All(list.Names, name => Assert.False(string.IsNullOrEmpty(name), list.Where));
            Assert.Equal(list.Names.Count, list.Names.Distinct(StringComparer.Ordinal).Count());
        });
        Assert.Equal(2, lists.Single(l => l.Where == "job-detail/parts").Names.Count);
    }

    [Fact]
    public void No_MessageBox_in_Workshop_App()
    {
        var source = Path.Combine(RepositoryRoot(), "src", "Workshop.App");
        var files = Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToList();

        Assert.True(files.Count >= 9, $"Expected the app's source files under {source}, found {files.Count}.");
        Assert.DoesNotContain(files, path => File.ReadAllText(path).Contains("MessageBox.", StringComparison.Ordinal));
    }

    [Fact]
    public void Program_ends_on_an_unhandled_exception_rather_than_opening_a_dialog()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Workshop.App", "Program.cs"));

        var mode = program.IndexOf("Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);", StringComparison.Ordinal);
        var initialize = program.IndexOf("ApplicationConfiguration.Initialize();", StringComparison.Ordinal);

        Assert.True(mode >= 0, "Program.cs must set UnhandledExceptionMode.ThrowException, so no error dialog can open.");
        Assert.True(initialize > mode, "The exception mode must be set before ApplicationConfiguration.Initialize(), while no window exists.");
    }

    private const int DefaultMaxLength = 32767;

    /// <summary>
    /// Opens each screen in turn, as the describer needs it shown, and yields it while open. The
    /// detail screens open on a seeded job and customer, so they show a record rather than the list.
    /// </summary>
    private static IEnumerable<WorkshopScreen> EveryScreen(MainForm form, string jobId = "J-1008", string customerId = "C-002")
    {
        foreach (var screen in form.Navigator.All)
        {
            switch (screen.Id)
            {
                case "job-detail":
                    form.Navigator.OpenJob(jobId);
                    break;
                case "customer-detail":
                    form.Navigator.OpenCustomer(customerId);
                    break;
                default:
                    Assert.True(form.Navigator.Open(screen.Id));
                    break;
            }

            Assert.Same(screen, form.Navigator.Current);
            yield return screen;
        }
    }

    private static bool IsInteractive(Control control) =>
        control is TextBox or ComboBox or NumericUpDown or CheckBox or DateTimePicker or Button or ListView;

    private static IEnumerable<Control> AllControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;

            // A NumericUpDown's own parts (its edit box and buttons) are not separate controls a person uses.
            if (child is NumericUpDown)
            {
                continue;
            }

            foreach (var descendant in AllControls(child))
            {
                yield return descendant;
            }
        }
    }

    private static FieldDescription Field(ScreenDescription screen, string id) => screen.Fields.Single(f => f.Id == id);

    private static ListDescription List(ScreenDescription screen, string id) => screen.Lists.Single(l => l.Id == id);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FoundryWorkshopAgent.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test's base directory.");
    }

    private sealed record Expected(string[] Fields, string[] Buttons, string[] Lists);
}
