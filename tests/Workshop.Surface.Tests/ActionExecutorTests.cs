using System.Text.Json;

namespace Workshop.Surface.Tests;

public sealed class ActionExecutorTests
{
    [Fact]
    public void Open_switches_screen_and_returns_its_description()
    {
        OnUi((navigator, executor) =>
        {
            var result = executor.Execute(Open("other"));

            Assert.Equal(Outcomes.Ok, result.Outcome);
            Assert.Null(result.Message);
            Assert.Same(navigator.OtherScreen, navigator.Current);
            Assert.Equal("other", result.Screen.Id);
            Assert.Equivalent(ScreenDescriber.Describe(navigator.OtherScreen), result.Screen, strict: true);
        });
    }

    [Fact]
    public void Open_unknown_is_not_found()
    {
        OnUi((navigator, executor) =>
        {
            var result = executor.Execute(Open("nowhere"));

            Assert.Equal(Outcomes.NotFound, result.Outcome);
            Assert.Equal("There is no screen 'nowhere'.", result.Message);
            Assert.Same(navigator.Editor, navigator.Current);
            Assert.Equal("editor", result.Screen.Id);
        });
    }

    [Fact]
    public void Set_text_number_choice_checkbox_date_round_trip()
    {
        OnUi((navigator, executor) =>
        {
            ActionResult? last = null;
            foreach (var (field, value) in new[] { ("name", "Bo"), ("quantity", "7"), ("colour", "Blue"), ("urgent", "false"), ("due", "2026-11-01") })
            {
                last = executor.Execute(Set(field, value));
                Assert.Equal(Outcomes.Ok, last.Outcome);
                Assert.Null(last.Message);
            }

            var fields = last!.Screen.Fields.ToDictionary(f => f.Id, f => f.Value);
            Assert.Equal("Bo", fields["name"]);
            Assert.Equal("7", fields["quantity"]);
            Assert.Equal("Blue", fields["colour"]);
            Assert.Equal("false", fields["urgent"]);
            Assert.Equal("2026-11-01", fields["due"]);

            // The values went into the real controls, through their normal change events.
            var editor = navigator.Editor;
            Assert.Equal("Bo", editor.CustomerName.Text);
            Assert.Equal(7m, editor.Quantity.Value);
            Assert.Equal(2, editor.Colour.SelectedIndex);
            Assert.False(editor.Urgent.Checked);
            Assert.Equal(new DateTime(2026, 11, 1), editor.Due.Value);
            Assert.Equal(["name", "quantity", "colour", "urgent", "due"], editor.Changes);
        });
    }

    [Theory]
    [InlineData("seven")]
    [InlineData("2.5")]
    [InlineData("")]
    [InlineData(null)]
    public void Set_number_that_is_not_whole_needs_a_whole_number(string? value) =>
        AssertRefused("quantity", value, "Quantity needs a whole number.", "1");

    [Theory]
    [InlineData("0")]
    [InlineData("21")]
    [InlineData("-3")]
    public void Set_number_outside_its_range_must_be_between(string value) =>
        AssertRefused("quantity", value, "Quantity must be between 1 and 20.", "1");

    [Theory]
    [InlineData("Purple")]
    [InlineData("blue")]
    [InlineData("B")]
    public void Set_choice_not_among_the_options_must_be_one_of(string value) =>
        AssertRefused("colour", value, "Colour must be one of: Red, Green, Blue.", "Red");

    [Theory]
    [InlineData("15/10/2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026-1-5")]
    [InlineData("tomorrow")]
    public void Set_date_not_as_yyyy_MM_dd_needs_a_date(string value) =>
        AssertRefused("due", value, "Due date needs a date as yyyy-MM-dd.", "2026-10-15");

    [Fact]
    public void Set_date_outside_the_pickers_range_must_be_between() =>
        AssertRefused("due", "2027-01-01", "Due date must be between 2026-01-01 and 2026-12-31.", "2026-10-15");

    [Fact]
    public void Set_text_over_its_max_length_can_be_at_most() =>
        AssertRefused("name", "Bartholomew", "Name can be at most 10 characters.", "Ada");

    [Theory]
    [InlineData("yes")]
    [InlineData("True")]
    [InlineData("1")]
    public void Set_checkbox_other_than_true_or_false_needs_true_or_false(string value) =>
        AssertRefused("urgent", value, "Urgent needs true or false.", "true");

    [Theory]
    [InlineData("Bo\nBo")]
    [InlineData("Bo\r\nBo")]
    [InlineData("Bo\rBo")]
    public void Set_a_line_break_in_a_one_line_text_must_be_on_one_line(string value) =>
        AssertRefused("name", value, "Name must be on one line.", "Ada");

    [Fact]
    public void Set_a_line_break_in_a_multiline_text_is_ok()
    {
        OnUi((navigator, executor) =>
        {
            navigator.Editor.CustomerName.Multiline = true;

            var result = executor.Execute(Set("name", "Bo\r\nBo"));

            Assert.Equal(Outcomes.Ok, result.Outcome);
            Assert.Equal("Bo\r\nBo", navigator.Editor.CustomerName.Text);
        });
    }

    [Fact]
    public void Set_text_at_its_max_length_is_ok()
    {
        OnUi((_, executor) => Assert.Equal(Outcomes.Ok, executor.Execute(Set("name", "Bartholome")).Outcome));
    }

    [Fact]
    public void Set_disabled_is_disabled()
    {
        OnUi((navigator, executor) =>
        {
            var result = executor.Execute(Set("reference", "R-200"));

            Assert.Equal(Outcomes.Disabled, result.Outcome);
            Assert.Equal("Reference is disabled.", result.Message);
            Assert.Equal("R-100", navigator.Editor.Reference.Text);
        });
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("secret")] // hidden
    [InlineData("save")] // a button, not a field
    [InlineData("search")] // on the other screen
    [InlineData(null)]
    public void Set_unknown_field_is_not_found(string? field)
    {
        OnUi((_, executor) =>
        {
            var result = executor.Execute(Set(field, "x"));

            Assert.Equal(Outcomes.NotFound, result.Outcome);
            Assert.Equal($"There is no field '{field}' on this screen.", result.Message);
        });
    }

    [Fact]
    public void Select_row_selects_only_that_row()
    {
        OnUi((navigator, executor) =>
        {
            var items = navigator.Editor.Items;
            items.Items["J-1"]!.Selected = true;
            items.Items["J-3"]!.Selected = true;

            var result = executor.Execute(Select("items", "J-2"));

            Assert.Equal(Outcomes.Ok, result.Outcome);
            Assert.Equal(["J-2"], items.SelectedItems.Cast<ListViewItem>().Select(i => i.Name));
            Assert.Same(items.Items["J-2"], items.FocusedItem);

            var rows = result.Screen.Lists.Single(l => l.Id == "items").Rows;
            Assert.Equal(["J-2"], rows.Where(r => r.Selected).Select(r => r.Key));
        });
    }

    [Theory]
    [InlineData("items", "J-9", "Items has no row 'J-9'.")]
    [InlineData("items", "j-2", "Items has no row 'j-2'.")]
    [InlineData("items", null, "Items has no row ''.")]
    [InlineData("rows", "J-1", "There is no list 'rows' on this screen.")]
    [InlineData("name", "J-1", "There is no list 'name' on this screen.")]
    public void Select_unknown_list_or_row_is_not_found(string list, string? row, string message)
    {
        OnUi((navigator, executor) =>
        {
            var result = executor.Execute(Select(list, row));

            Assert.Equal(Outcomes.NotFound, result.Outcome);
            Assert.Equal(message, result.Message);
            Assert.Empty(navigator.Editor.Items.SelectedItems);
        });
    }

    [Fact]
    public void Select_in_a_disabled_list_is_disabled()
    {
        OnUi((_, executor) =>
        {
            var result = executor.Execute(Select("locked", "L-1"));

            Assert.Equal(Outcomes.Disabled, result.Outcome);
            Assert.Equal("Locked is disabled.", result.Message);
            Assert.False(result.Screen.Lists.Single(l => l.Id == "locked").Rows.Single().Selected);
        });
    }

    [Fact]
    public void Press_runs_the_handler()
    {
        OnUi((navigator, executor) =>
        {
            var result = executor.Execute(Press("save"));

            Assert.Equal(Outcomes.Ok, result.Outcome);
            Assert.Null(result.Message);
            Assert.Equal(1, navigator.Editor.SaveClicks);
            Assert.Equal("editor", result.Screen.Id);
        });
    }

    [Fact]
    public void Press_with_feedback_is_validation_failed_with_its_message()
    {
        OnUi((_, executor) =>
        {
            var result = executor.Execute(Press("refuse"));

            Assert.Equal(Outcomes.ValidationFailed, result.Outcome);
            Assert.Equal("Select a job first.", result.Message);
        });
    }

    [Fact]
    public void Press_with_information_is_ok_with_its_message()
    {
        OnUi((_, executor) =>
        {
            var result = executor.Execute(Press("inform"));

            Assert.Equal(Outcomes.Ok, result.Outcome);
            Assert.Equal("Added on order: not enough in stock.", result.Message);
        });
    }

    [Fact]
    public void Press_with_a_refusal_and_information_is_validation_failed_with_the_refusal()
    {
        OnUi((_, executor) =>
        {
            var result = executor.Execute(Press("mixed"));

            Assert.Equal(Outcomes.ValidationFailed, result.Outcome);
            Assert.Equal("The parts of a job that is ready cannot change.", result.Message);
        });
    }

    [Fact]
    public void Press_disabled_is_disabled()
    {
        OnUi((navigator, executor) =>
        {
            var result = executor.Execute(Press("archive"));

            Assert.Equal(Outcomes.Disabled, result.Outcome);
            Assert.Equal("Archive is disabled.", result.Message);
            Assert.Equal(0, navigator.Editor.ArchiveClicks);
        });
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("go")] // on the other screen
    [InlineData("name")] // a field, not a button
    public void Press_unknown_is_not_found(string button)
    {
        OnUi((_, executor) =>
        {
            var result = executor.Execute(Press(button));

            Assert.Equal(Outcomes.NotFound, result.Outcome);
            Assert.Equal($"There is no button '{button}' on this screen.", result.Message);
        });
    }

    [Fact]
    public void Feedback_is_cleared_after_each_press()
    {
        OnUi((navigator, executor) =>
        {
            Assert.Equal(Outcomes.ValidationFailed, executor.Execute(Press("refuse")).Outcome);

            var next = executor.Execute(Press("save"));
            Assert.Equal(Outcomes.Ok, next.Outcome);
            Assert.Null(next.Message);

            // A refusal from a person's own click, before the press, is not this press's outcome.
            SurfaceFeedback.Fail(navigator.Editor.CustomerName, "Left over from a person's click.");
            Assert.Equal(Outcomes.Ok, executor.Execute(Press("save")).Outcome);
            Assert.Equal(2, navigator.Editor.SaveClicks);
        });
    }

    [Fact]
    public void Information_is_cleared_after_each_press()
    {
        OnUi((navigator, executor) =>
        {
            Assert.Equal("Added on order: not enough in stock.", executor.Execute(Press("inform")).Message);
            Assert.Null(executor.Execute(Press("save")).Message);

            // Information left by a person's own click, before the press, is not this press's message.
            SurfaceFeedback.Inform(navigator.Editor.CustomerName, "Left over from a person's click.");
            var next = executor.Execute(Press("save"));
            Assert.Equal(Outcomes.Ok, next.Outcome);
            Assert.Null(next.Message);
        });
    }

    [Fact]
    public void Feedback_from_a_control_outside_any_screen_throws()
    {
        StaRunner.Run(() =>
        {
            using var loose = new TextBox();
            Assert.Throws<ArgumentException>(() => SurfaceFeedback.Fail(loose, "No screen to report on."));
            Assert.Throws<ArgumentException>(() => SurfaceFeedback.Inform(loose, "No screen to report on."));
        });
    }

    [Fact]
    public void Unknown_action_type_throws()
    {
        OnUi((_, executor) =>
            Assert.Throws<ArgumentException>(() => executor.Execute(new SurfaceAction("click", null, null, null, null, null, "save"))));
    }

    [Fact]
    public void Action_json_uses_the_field_names()
    {
        var action = JsonSerializer.Deserialize<SurfaceAction>(
            """{"type":"select","screen":"s","field":"f","value":"v","list":"l","row":"r","button":"b"}""",
            SurfaceJson.Options);

        Assert.Equal(new SurfaceAction("select", "s", "f", "v", "l", "r", "b"), action);
    }

    private static void AssertRefused(string field, string? value, string message, string unchanged)
    {
        OnUi((navigator, executor) =>
        {
            var result = executor.Execute(Set(field, value));

            Assert.Equal(Outcomes.ValidationFailed, result.Outcome);
            Assert.Equal(message, result.Message);
            Assert.Equal(unchanged, result.Screen.Fields.Single(f => f.Id == field).Value);
            Assert.Empty(navigator.Editor.Changes);
        });
    }

    private static SurfaceAction Open(string screen) => new(ActionTypes.Open, screen, null, null, null, null, null);
    private static SurfaceAction Set(string? field, string? value) => new(ActionTypes.Set, null, field, value, null, null, null);
    private static SurfaceAction Select(string list, string? row) => new(ActionTypes.Select, null, null, null, list, row, null);
    private static SurfaceAction Press(string button) => new(ActionTypes.Press, null, null, null, null, null, button);

    private static void OnUi(Action<TwoScreenNavigator, ActionExecutor> test) =>
        StaRunner.Run(() =>
        {
            using var navigator = new TwoScreenNavigator();
            test(navigator, new ActionExecutor(navigator));
        });

    /// <summary>A shown host with two screens, one visible at a time, as the app's shell does.</summary>
    private sealed class TwoScreenNavigator : IScreenNavigator, IDisposable
    {
        private readonly Form host = TestForms.Host();

        public TwoScreenNavigator()
        {
            OtherScreen.Visible = false;
            host.Controls.AddRange([Editor, OtherScreen]);
            Current = Editor;
            host.Show();
        }

        public TestForms.Editor Editor { get; } = new();
        public UserControl OtherScreen { get; } = TestForms.Other();

        public IReadOnlyList<(string Id, string Title)> Screens { get; } = [("editor", "Editor"), ("other", "Other")];

        public Control Current { get; private set; }

        public bool Open(string id)
        {
            Control next = id == "editor" ? Editor : OtherScreen;
            Current.Visible = false;
            next.Visible = true;
            Current = next;
            return true;
        }

        public void Dispose() => host.Dispose();
    }
}
