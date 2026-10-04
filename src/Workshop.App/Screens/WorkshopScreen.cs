using Workshop.Core;
using Workshop.Surface;
using SurfaceMeta = Workshop.Surface.Surface;

namespace Workshop.App.Screens;

/// <summary>
/// One of the app's screens: a user control that <see cref="WorkshopNavigator"/> shows one at a
/// time. It carries the screen's surface marker, lays its controls out in rows in tab order, and
/// shows what came of a press in one inline message label. Each screen sets its controls' metadata
/// in its own constructor, with the helpers here.
/// </summary>
internal abstract class WorkshopScreen : UserControl
{
    private static readonly Color ErrorColour = Color.Firebrick;
    private static readonly Color InfoColour = Color.DarkGreen;

    private readonly TableLayoutPanel layout;
    private readonly Label message;
    private int nextTabIndex;

    protected WorkshopScreen(string id, string title, WorkshopNavigator navigator, JobService service)
    {
        Id = id;
        Title = title;
        Navigator = navigator;
        Service = service;

        Dock = DockStyle.Fill;
        Padding = new Padding(12);
        AutoScroll = true;
        SurfaceMeta.Screen(this, id, title);

        // Sized to its rows and docked to the top, so the rows keep their height and spare space stays below.
        layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        AddWide(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
            Margin = new Padding(3, 0, 3, 8),
        });
        message = new Label { AutoSize = true, MaximumSize = new Size(800, 0), Margin = new Padding(3, 0, 3, 8) };
        AddWide(message);
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>The message showing inline, or null when there is none.</summary>
    public string? Message => message.Text.Length == 0 ? null : message.Text;

    protected WorkshopNavigator Navigator { get; }

    protected JobService Service { get; }

    /// <summary>Called by the navigator each time the screen is shown: clears the message and reloads.</summary>
    public void Opened()
    {
        message.Text = "";
        OnOpened();
    }

    protected abstract void OnOpened();

    // ---- Outcomes ----

    /// <summary>
    /// Shows a rule's outcome. A refusal is shown inline and passed to the surface as a failure;
    /// a success with a message is shown inline and passed on as information. True if it succeeded.
    /// </summary>
    protected bool Report(RuleResult result)
    {
        if (!result.Ok)
        {
            Refuse(result.Message ?? "The workshop refused the change.");
            return false;
        }

        if (result.Message is { } information)
        {
            Show(information, InfoColour);
            SurfaceFeedback.Inform(this, information);
        }

        return true;
    }

    protected void Refuse(string refusal)
    {
        Show(refusal, ErrorColour);
        SurfaceFeedback.Fail(this, refusal);
    }

    /// <summary>Runs <paramref name="action"/> on each click, after clearing the last press's message.</summary>
    protected void OnPress(Button button, Action action) => button.Click += (_, _) =>
    {
        message.Text = "";
        action();
    };

    private void Show(string text, Color colour)
    {
        message.ForeColor = colour;
        message.Text = text;
    }

    // ---- Controls, with their metadata ----

    protected static TextBox TextField(string id, string label, int? maxLength = null, bool required = false, bool enabled = true)
    {
        var box = new TextBox { Width = 420, Enabled = enabled };
        if (maxLength is { } max)
        {
            box.MaxLength = max;
        }

        return box.Meta(id, label, required: required, maxLength: maxLength);
    }

    /// <summary>A disabled text box that only shows a value: read-only too, but never enabled while read-only.</summary>
    protected static TextBox ShownField(string id, string label) =>
        new TextBox { Width = 420, ReadOnly = true, Enabled = false }.Meta(id, label);

    protected static ComboBox ChoiceField(string id, string label, bool required = false) =>
        new ComboBox { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList }.Meta(id, label, required: required);

    protected static NumericUpDown NumberField(string id, string label, int minimum, int maximum) =>
        new NumericUpDown { Width = 70, Minimum = minimum, Maximum = maximum, Value = minimum }.Meta(id, label);

    /// <summary>A button. It never triggers validation, so no field's Validating handler can swallow a press.</summary>
    protected static Button ActionButton(string id, string label, bool destructive = false) =>
        new Button { Text = label, AutoSize = true, CausesValidation = false }.Meta(id, label, destructive: destructive);

    protected static ListView ListField(string id, string label, int height, params (string Name, int Width)[] columns)
    {
        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            GridLines = true,
            Height = height,
        };
        foreach (var (name, width) in columns)
        {
            list.Columns.Add(name, width);
        }

        return list.Meta(id, label);
    }

    // ---- Layout, in tab order ----

    /// <summary>Adds a row: a caption, then one control or several side by side. Tab order follows the calls.</summary>
    protected void AddRow(string? caption, params Control[] controls)
    {
        Control content;
        if (controls.Length == 1)
        {
            content = controls[0];
        }
        else
        {
            var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
            for (var index = 0; index < controls.Length; index++)
            {
                controls[index].TabIndex = index;
                flow.Controls.Add(controls[index]);
            }

            content = flow;
        }

        content.Anchor = content is ListView ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;

        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = caption ?? "", AutoSize = true, Anchor = AnchorStyles.Left, TabIndex = nextTabIndex++ }, 0, row);
        content.TabIndex = nextTabIndex++;
        layout.Controls.Add(content, 1, row);
    }

    private void AddWide(Control control)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.TabIndex = nextTabIndex++;
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, 2);
    }

    // ---- Lists and choices ----

    /// <summary>A choice's item: a record's key, shown as its text.</summary>
    protected sealed record Option(string Key, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>Replaces a list's rows, keeping the selected row selected if it is still there.</summary>
    protected static void Fill(ListView list, IEnumerable<(string Key, string[] Cells)> rows) => Fill(list, rows, SelectedKey(list));

    /// <summary>Replaces a list's rows, with the row keyed <paramref name="selected"/> selected if it is there.</summary>
    protected static void Fill(ListView list, IEnumerable<(string Key, string[] Cells)> rows, string? selected)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var (key, cells) in rows)
            {
                // The row's Name is its key: the agent selects rows by it, so it must be unique and non-empty.
                list.Items.Add(new ListViewItem(cells) { Name = key, Selected = key == selected });
            }
        }
        finally
        {
            list.EndUpdate();
        }
    }

    /// <summary>Replaces a choice's options, keeping the selected one selected if it is still there.</summary>
    protected static void Fill(ComboBox choice, IEnumerable<Option> options)
    {
        var selected = SelectedKey(choice);
        choice.BeginUpdate();
        try
        {
            choice.Items.Clear();
            foreach (var option in options)
            {
                choice.Items.Add(option);
            }

            choice.SelectedIndex = choice.Items.Cast<Option>().ToList().FindIndex(o => o.Key == selected);
        }
        finally
        {
            choice.EndUpdate();
        }
    }

    protected static string? SelectedKey(ListView list) => list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Name;

    protected static string? SelectedKey(ComboBox choice) => (choice.SelectedItem as Option)?.Key;

    /// <summary>A device as the screens show it: <c>Aster Book 14 (laptop)</c>.</summary>
    protected static string DeviceText(Device device) => $"{device.Model} ({device.Kind})";
}
