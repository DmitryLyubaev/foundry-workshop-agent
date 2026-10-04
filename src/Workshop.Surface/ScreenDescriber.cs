using System.Globalization;

namespace Workshop.Surface;

/// <summary>
/// Builds a screen's description from its controls and their metadata. Nothing is described by
/// hand: a control is described when it has metadata and is visible.
/// </summary>
public static class ScreenDescriber
{
    /// <summary>
    /// Describes the screen rooted at <paramref name="root"/>, which must have been marked with
    /// <see cref="Surface.Screen"/>. Visibility is the control's real <see cref="Control.Visible"/>,
    /// so the root must be shown; a hidden root describes no controls.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The root is not a screen, two controls share an ID, or a control with metadata is of a
    /// type the surface cannot describe.
    /// </exception>
    public static ScreenDescription Describe(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var (screenId, title) = Surface.ScreenOf(root)
            ?? throw new InvalidOperationException($"The control '{root.Name}' ({root.GetType().Name}) is not marked as a screen.");

        RefuseDuplicateIds(root, screenId);

        var fields = new List<FieldDescription>();
        var buttons = new List<ButtonDescription>();
        var lists = new List<ListDescription>();

        foreach (var (control, meta) in VisibleInTabOrder(root))
        {
            switch (control)
            {
                case Button:
                    buttons.Add(new ButtonDescription(meta.Id, meta.Label, control.Enabled, meta.Destructive));
                    break;
                case ListView { View: View.Details } list:
                    lists.Add(DescribeList(list, meta));
                    break;
                default:
                    fields.Add(DescribeField(control, meta));
                    break;
            }
        }

        return new ScreenDescription(screenId, title, fields, buttons, lists);
    }

    private static FieldDescription DescribeField(Control control, ControlMeta meta)
    {
        var (kind, value, options) = control switch
        {
            TextBox text => (FieldKinds.Text, text.Text, null),
            NumericUpDown number => (FieldKinds.Number, decimal.Truncate(number.Value).ToString(CultureInfo.InvariantCulture), null),
            ComboBox { DropDownStyle: ComboBoxStyle.DropDownList } choice => (
                FieldKinds.Choice,
                choice.SelectedIndex < 0 ? null : choice.GetItemText(choice.SelectedItem),
                OptionsOf(choice)),
            CheckBox check => (FieldKinds.Checkbox, check.Checked ? "true" : "false", null),
            DateTimePicker date => (FieldKinds.Date, date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null),
            _ => throw new InvalidOperationException(
                $"The control '{meta.Id}' is a {control.GetType().Name}, which the surface cannot describe."),
        };

        return new FieldDescription(meta.Id, meta.Label, kind, value, options, control.Enabled, meta.Required, meta.MaxLength);
    }

    private static IReadOnlyList<string>? OptionsOf(ComboBox choice) =>
        [.. choice.Items.Cast<object>().Select(item => choice.GetItemText(item) ?? "")];

    private static ListDescription DescribeList(ListView list, ControlMeta meta)
    {
        string[] columns = [.. list.Columns.Cast<ColumnHeader>().Select(c => c.Text)];

        RowDescription[] rows =
        [
            .. list.Items.Cast<ListViewItem>().Select(item => new RowDescription(
                item.Name,
                [.. item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text)],
                item.Selected)),
        ];

        return new ListDescription(meta.Id, meta.Label, columns, rows);
    }

    /// <summary>
    /// The visible controls with metadata, depth-first in tab order: a container's controls come
    /// at the container's place, as the Tab key visits them. Only a TabControl's selected page is
    /// entered. A described control's own children are its internal parts and are not entered.
    /// The action executor finds its targets through this walk, so it acts only on what is described.
    /// </summary>
    internal static IEnumerable<(Control Control, ControlMeta Meta)> VisibleInTabOrder(Control parent)
    {
        IEnumerable<Control> children = parent is TabControl tabs
            ? tabs.SelectedTab is { } selected ? [selected] : []
            : InTabOrder(parent.Controls);

        foreach (var child in children)
        {
            if (!child.Visible)
            {
                continue;
            }

            if (Surface.MetaOf(child) is { } meta)
            {
                yield return (child, meta);
                continue;
            }

            foreach (var described in VisibleInTabOrder(child))
            {
                yield return described;
            }
        }
    }

    // OrderBy is stable, so equal tab indexes keep the collection's order.
    private static IEnumerable<Control> InTabOrder(Control.ControlCollection controls) =>
        controls.Cast<Control>().OrderBy(c => c.TabIndex);

    /// <summary>
    /// IDs must be unique across the whole screen, hidden controls included, so a clash is caught
    /// whatever state the screen is in.
    /// </summary>
    private static void RefuseDuplicateIds(Control root, string screenId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var control in AllControls(root))
        {
            if (Surface.MetaOf(control) is { } meta && !seen.Add(meta.Id))
            {
                throw new InvalidOperationException($"Two controls on the screen '{screenId}' have the ID '{meta.Id}'.");
            }
        }
    }

    private static IEnumerable<Control> AllControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;

            foreach (var descendant in AllControls(child))
            {
                yield return descendant;
            }
        }
    }
}
