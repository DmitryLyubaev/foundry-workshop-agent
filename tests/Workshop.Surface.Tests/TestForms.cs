namespace Workshop.Surface.Tests;

/// <summary>Made-up forms for the describer tests. Build them on the STA thread.</summary>
internal static class TestForms
{
    /// <summary>A colour with a code and a display name, so display text differs from ToString.</summary>
    public sealed record Colour(string Code, string Name);

    /// <summary>
    /// One control of each kind, plus a control without metadata, hidden controls, disabled
    /// controls and a two-tab TabControl. Controls are added in reverse tab order so that output
    /// order can only come from TabIndex.
    /// </summary>
    public static Form KitchenSink()
    {
        var form = NewForm();
        Surface.Screen(form, "kitchen", "Kitchen sink");

        var name = new TextBox { TabIndex = 0, Text = "Ada Lovelace" }
            .Meta("customerName", "Customer name", required: true, maxLength: 40);

        var quantity = new NumericUpDown { TabIndex = 1, Minimum = 0, Maximum = 99, Value = 3 }
            .Meta("quantity", "Quantity");

        var colour = new ComboBox { TabIndex = 2, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(Colour.Name) }
            .Meta("colour", "Colour", required: true);
        var green = new Colour("G", "Green");
        colour.Items.AddRange([new Colour("R", "Red"), green, new Colour("B", "Blue")]);
        colour.SelectedItem = green;

        var urgent = new CheckBox { TabIndex = 3, Checked = true }.Meta("urgent", "Urgent");

        var due = new DateTimePicker { TabIndex = 4, Value = new DateTime(2026, 10, 15) }.Meta("due", "Due date");

        var reference = new TextBox { TabIndex = 5, Text = "R-100", Enabled = false }.Meta("reference", "Reference");

        // Disabled through its container, not itself.
        var warrantyBox = new GroupBox { TabIndex = 6, Enabled = false };
        warrantyBox.Controls.Add(new CheckBox { Checked = false }.Meta("warranty", "Under warranty"));

        var scratch = new TextBox { TabIndex = 7, Text = "no metadata" };

        var secret = new TextBox { TabIndex = 8, Visible = false }.Meta("secret", "Secret");

        var hiddenPanel = new Panel { TabIndex = 9, Visible = false };
        hiddenPanel.Controls.Add(new TextBox().Meta("inHiddenPanel", "In hidden panel"));

        var tabs = new TabControl { TabIndex = 10 };
        var details = new TabPage("Details");
        details.Controls.Add(new TextBox { Text = "Fan is noisy" }.Meta("notes", "Notes"));
        var history = new TabPage("History");
        history.Controls.Add(new TextBox().Meta("history", "History"));
        tabs.TabPages.AddRange([details, history]);
        tabs.SelectedTab = details;

        var items = new ListView { TabIndex = 11, View = View.Details }.Meta("items", "Items");
        items.Columns.Add("Number");
        items.Columns.Add("Device");
        items.Items.Add(new ListViewItem(["J-1", "Laptop"]) { Name = "J-1" });
        items.Items.Add(new ListViewItem(["J-2", "Printer"]) { Name = "J-2" }).Selected = true;

        var save = new Button { TabIndex = 12, Text = "Save" }.Meta("save", "Save");
        var delete = new Button { TabIndex = 13, Text = "Delete" }.Meta("delete", "Delete", destructive: true);
        var archive = new Button { TabIndex = 14, Text = "Archive", Enabled = false }.Meta("archive", "Archive");

        Control[] inTabOrder =
            [name, quantity, colour, urgent, due, reference, warrantyBox, scratch, secret, hiddenPanel, tabs, items, save, delete, archive];
        form.Controls.AddRange([.. Enumerable.Reverse(inTabOrder)]);
        return form;
    }

    /// <summary>Two controls of different kinds sharing one ID.</summary>
    public static Form DuplicateIds()
    {
        var form = NewForm();
        Surface.Screen(form, "dupes", "Duplicates");
        form.Controls.Add(new TextBox().Meta("customerName", "Customer name"));
        form.Controls.Add(new Button { Text = "Customer" }.Meta("customerName", "Customer"));
        return form;
    }

    /// <summary>A small screen with one of each section, for the exact JSON.</summary>
    public static Form Small()
    {
        var form = NewForm();
        Surface.Screen(form, "small", "Small");

        var name = new TextBox { TabIndex = 0, Text = "Ada" }.Meta("name", "Name", required: true, maxLength: 20);

        // No selection, so its value is null and must be left out of the JSON.
        var colour = new ComboBox { TabIndex = 1, DropDownStyle = ComboBoxStyle.DropDownList }.Meta("colour", "Colour");
        colour.Items.AddRange(["Red", "Green"]);

        var rows = new ListView { TabIndex = 2, View = View.Details }.Meta("rows", "Rows");
        rows.Columns.Add("Number");
        rows.Items.Add(new ListViewItem("J-1") { Name = "J-1" });

        var save = new Button { TabIndex = 3, Text = "Save" }.Meta("save", "Save");

        form.Controls.AddRange([name, colour, rows, save]);
        return form;
    }

    /// <summary>A plain, borderless host for screens that are user controls, as the app's shell is.</summary>
    public static Form Host() => NewForm();

    /// <summary>The executor's second screen: just a search box and a button.</summary>
    public static UserControl Other()
    {
        var screen = new UserControl { Dock = DockStyle.Fill };
        Surface.Screen(screen, "other", "Other");
        screen.Controls.Add(new Button { TabIndex = 1, Text = "Go" }.Meta("go", "Go"));
        screen.Controls.Add(new TextBox { TabIndex = 0 }.Meta("search", "Search"));
        return screen;
    }

    /// <summary>
    /// The executor's main screen: one field of each kind with a range or a limit to break, a
    /// multi-select list, a disabled list, and buttons whose handlers record that they ran.
    /// </summary>
    public sealed class Editor : UserControl
    {
        public Editor()
        {
            Dock = DockStyle.Fill;
            Surface.Screen(this, "editor", "Editor");

            CustomerName = new TextBox { TabIndex = 0, Text = "Ada" }.Meta("name", "Name", maxLength: 10);
            Quantity = new NumericUpDown { TabIndex = 1, Minimum = 1, Maximum = 20, Value = 1 }.Meta("quantity", "Quantity");

            Colour = new ComboBox { TabIndex = 2, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(TestForms.Colour.Name) }
                .Meta("colour", "Colour");
            Colour.Items.AddRange([new TestForms.Colour("R", "Red"), new TestForms.Colour("G", "Green"), new TestForms.Colour("B", "Blue")]);
            Colour.SelectedIndex = 0;

            Urgent = new CheckBox { TabIndex = 3, Checked = true }.Meta("urgent", "Urgent");

            Due = new DateTimePicker
            {
                TabIndex = 4,
                MinDate = new DateTime(2026, 1, 1),
                MaxDate = new DateTime(2026, 12, 31),
                Value = new DateTime(2026, 10, 15),
            }.Meta("due", "Due date");

            Reference = new TextBox { TabIndex = 5, Text = "R-100", Enabled = false }.Meta("reference", "Reference");
            var secret = new TextBox { TabIndex = 6, Visible = false }.Meta("secret", "Secret");

            Items = new ListView { TabIndex = 7, View = View.Details, MultiSelect = true }.Meta("items", "Items");
            Items.Columns.Add("Number");
            Items.Items.Add(new ListViewItem("J-1") { Name = "J-1" });
            Items.Items.Add(new ListViewItem("J-2") { Name = "J-2" });
            Items.Items.Add(new ListViewItem("J-3") { Name = "J-3" });

            var locked = new ListView { TabIndex = 8, View = View.Details, Enabled = false }.Meta("locked", "Locked");
            locked.Columns.Add("Number");
            locked.Items.Add(new ListViewItem("L-1") { Name = "L-1" });

            var save = new Button { TabIndex = 9, Text = "Save" }.Meta("save", "Save");
            save.Click += (_, _) => SaveClicks++;

            var refuse = new Button { TabIndex = 10, Text = "Refuse" }.Meta("refuse", "Refuse");
            refuse.Click += (_, _) => SurfaceFeedback.Fail(refuse, "Select a job first.");

            var archive = new Button { TabIndex = 11, Text = "Archive", Enabled = false }.Meta("archive", "Archive");
            archive.Click += (_, _) => ArchiveClicks++;

            var inform = new Button { TabIndex = 12, Text = "Inform" }.Meta("inform", "Inform");
            inform.Click += (_, _) => SurfaceFeedback.Inform(inform, "Added on order: not enough in stock.");

            // Informs, then refuses, then informs again: the refusal is the press's outcome.
            var mixed = new Button { TabIndex = 13, Text = "Mixed" }.Meta("mixed", "Mixed");
            mixed.Click += (_, _) =>
            {
                SurfaceFeedback.Inform(mixed, "Checked the stock.");
                SurfaceFeedback.Fail(mixed, "The parts of a job that is ready cannot change.");
                SurfaceFeedback.Inform(mixed, "Checked again.");
            };

            CustomerName.TextChanged += (_, _) => Changes.Add("name");
            Quantity.ValueChanged += (_, _) => Changes.Add("quantity");
            Colour.SelectedIndexChanged += (_, _) => Changes.Add("colour");
            Urgent.CheckedChanged += (_, _) => Changes.Add("urgent");
            Due.ValueChanged += (_, _) => Changes.Add("due");

            Controls.AddRange([CustomerName, Quantity, Colour, Urgent, Due, Reference, secret, Items, locked, save, refuse, archive, inform, mixed]);
        }

        public TextBox CustomerName { get; }
        public NumericUpDown Quantity { get; }
        public ComboBox Colour { get; }
        public CheckBox Urgent { get; }
        public DateTimePicker Due { get; }
        public TextBox Reference { get; }
        public ListView Items { get; }

        /// <summary>The IDs of the fields whose change event fired, in order.</summary>
        public List<string> Changes { get; } = [];

        public int SaveClicks { get; private set; }
        public int ArchiveClicks { get; private set; }
    }

    private static Form NewForm() => new()
    {
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.Manual,
        Location = new Point(-2000, -2000),
    };
}
