using System.Globalization;

namespace Workshop.Surface;

/// <summary>
/// Carries out the agent's actions through the real controls, as a person would: it sets a
/// control's value, selects a list row or clicks a button, so the app's own events and handlers
/// run. It acts only on what the current screen's description shows, and a value that will not
/// parse is refused with a message, never thrown.
/// </summary>
/// <remarks>Not thread-safe: <see cref="Execute"/> must run on the UI thread.</remarks>
public sealed class ActionExecutor(IScreenNavigator navigator)
{
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly (string Outcome, string? Message) Ok = (Outcomes.Ok, null);

    private readonly IScreenNavigator navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));

    /// <summary>Runs one action and describes the current screen after it.</summary>
    /// <exception cref="ArgumentException">The action's type is not one of <see cref="ActionTypes"/>.</exception>
    public ActionResult Execute(SurfaceAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var (outcome, message) = action.Type switch
        {
            ActionTypes.Open => Open(action.Screen),
            ActionTypes.Set => Set(action.Field, action.Value ?? ""),
            ActionTypes.Select => Select(action.List, action.Row),
            ActionTypes.Press => Press(action.Button),
            _ => throw new ArgumentException($"The action type '{action.Type}' is not one of open, set, select or press.", nameof(action)),
        };

        return new ActionResult(outcome, message, ScreenDescriber.Describe(navigator.Current));
    }

    private (string, string?) Open(string? id)
    {
        if (!navigator.Screens.Any(s => s.Id == id))
        {
            return (Outcomes.NotFound, $"There is no screen '{id}'.");
        }

        return navigator.Open(id!) ? Ok : (Outcomes.NotFound, $"The screen '{id}' could not be opened.");
    }

    private (string, string?) Set(string? id, string value)
    {
        var screen = navigator.Current;
        var field = ScreenDescriber.Describe(screen).Fields.FirstOrDefault(f => f.Id == id);

        if (field is null)
        {
            return (Outcomes.NotFound, $"There is no field '{id}' on this screen.");
        }

        if (!field.Enabled)
        {
            return (Outcomes.Disabled, $"{field.Label} is disabled.");
        }

        // The describer has inferred the kind from the control's type, so each cast holds.
        var control = ControlOf(screen, field.Id);
        var refusal = field.Kind switch
        {
            FieldKinds.Text => SetText((TextBox)control, field, value),
            FieldKinds.Number => SetNumber((NumericUpDown)control, field, value),
            FieldKinds.Choice => SetChoice((ComboBox)control, field, value),
            FieldKinds.Checkbox => SetCheckbox((CheckBox)control, field, value),
            FieldKinds.Date => SetDate((DateTimePicker)control, field, value),
            _ => throw new InvalidOperationException($"The field '{field.Id}' has the kind '{field.Kind}', which cannot be set."),
        };

        return refusal is null ? Ok : (Outcomes.ValidationFailed, refusal);
    }

    // Each setter returns the refusal message, or sets the value and returns null.

    private static string? SetText(TextBox text, FieldDescription field, string value)
    {
        if (field.MaxLength is { } max && value.Length > max)
        {
            return $"{field.Label} can be at most {max} characters.";
        }

        text.Text = value;
        return null;
    }

    private static string? SetNumber(NumericUpDown number, FieldDescription field, string value)
    {
        if (!decimal.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return $"{field.Label} needs a whole number.";
        }

        // Setting Value outside the range would throw, so it is refused here first.
        if (parsed < number.Minimum || parsed > number.Maximum)
        {
            return $"{field.Label} must be between {Format(number.Minimum)} and {Format(number.Maximum)}.";
        }

        number.Value = parsed;
        return null;
    }

    private static string? SetChoice(ComboBox choice, FieldDescription field, string value)
    {
        var options = field.Options ?? [];

        // The options are the items' display text, in the items' order, so the index is the item's.
        for (var index = 0; index < options.Count; index++)
        {
            if (options[index] == value)
            {
                choice.SelectedIndex = index;
                return null;
            }
        }

        return $"{field.Label} must be one of: {string.Join(", ", options)}.";
    }

    private static string? SetCheckbox(CheckBox check, FieldDescription field, string value)
    {
        bool? parsed = value switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };

        if (parsed is not { } isChecked)
        {
            return $"{field.Label} needs true or false.";
        }

        check.Checked = isChecked;
        return null;
    }

    private static string? SetDate(DateTimePicker picker, FieldDescription field, string value)
    {
        if (!DateTime.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return $"{field.Label} needs a date as {DateFormat}.";
        }

        // Setting Value outside the picker's range would throw, so it is refused here first.
        if (date < picker.MinDate || date > picker.MaxDate)
        {
            return $"{field.Label} must be between {Format(picker.MinDate)} and {Format(picker.MaxDate)}.";
        }

        picker.Value = date;
        return null;
    }

    private (string, string?) Select(string? id, string? key)
    {
        var screen = navigator.Current;
        var list = ScreenDescriber.Describe(screen).Lists.FirstOrDefault(l => l.Id == id);

        if (list is null)
        {
            return (Outcomes.NotFound, $"There is no list '{id}' on this screen.");
        }

        var view = (ListView)ControlOf(screen, list.Id);

        if (!view.Enabled)
        {
            return (Outcomes.Disabled, $"{list.Label} is disabled.");
        }

        // Matched by hand: ListView's own key lookup ignores case, and row keys are exact.
        var items = view.Items.Cast<ListViewItem>().ToList();
        var row = string.IsNullOrEmpty(key) ? null : items.FirstOrDefault(item => item.Name == key);

        if (row is null)
        {
            return (Outcomes.NotFound, $"{list.Label} has no row '{key}'.");
        }

        // Deselect the others first, so a multi-select list never shows two rows selected at once.
        foreach (var other in items.Where(item => item != row && item.Selected))
        {
            other.Selected = false;
        }

        row.Selected = true;
        row.Focused = true;
        row.EnsureVisible();
        return Ok;
    }

    private (string, string?) Press(string? id)
    {
        var screen = navigator.Current;
        var button = ScreenDescriber.Describe(screen).Buttons.FirstOrDefault(b => b.Id == id);

        if (button is null)
        {
            return (Outcomes.NotFound, $"There is no button '{id}' on this screen.");
        }

        if (!button.Enabled)
        {
            return (Outcomes.Disabled, $"{button.Label} is disabled.");
        }

        // A message left by a person's own click before this press is not this press's outcome.
        _ = SurfaceFeedback.Take(screen);

        SurfaceFeedback.Feedback? feedback;
        try
        {
            ((Button)ControlOf(screen, button.Id)).PerformClick();
        }
        finally
        {
            feedback = SurfaceFeedback.Take(screen);
        }

        return feedback switch
        {
            null => Ok,
            { Failed: true } => (Outcomes.ValidationFailed, feedback.Message),
            _ => (Outcomes.Ok, feedback.Message),
        };
    }

    /// <summary>The described control with this ID; the description it came from guarantees it exists once.</summary>
    private static Control ControlOf(Control screen, string id) =>
        ScreenDescriber.VisibleInTabOrder(screen).First(c => c.Meta.Id == id).Control;

    private static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string Format(DateTime value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);
}
