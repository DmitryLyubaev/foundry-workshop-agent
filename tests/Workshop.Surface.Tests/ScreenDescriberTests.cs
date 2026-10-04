using System.Text.Json;

namespace Workshop.Surface.Tests;

public sealed class ScreenDescriberTests
{
    [Fact]
    public void Describes_each_kind_with_value_options_and_flags()
    {
        var screen = DescribeShown(TestForms.KitchenSink);

        Assert.Equal("kitchen", screen.Id);
        Assert.Equal("Kitchen sink", screen.Title);

        FieldDescription[] expected =
        [
            new("customerName", "Customer name", "text", "Ada Lovelace", null, Enabled: true, Required: true, MaxLength: 40),
            new("quantity", "Quantity", "number", "3", null, Enabled: true, Required: false, MaxLength: null),
            new("colour", "Colour", "choice", "Green", ["Red", "Green", "Blue"], Enabled: true, Required: true, MaxLength: null),
            new("urgent", "Urgent", "checkbox", "true", null, Enabled: true, Required: false, MaxLength: null),
            new("due", "Due date", "date", "2026-10-15", null, Enabled: true, Required: false, MaxLength: null),
            new("reference", "Reference", "text", "R-100", null, Enabled: false, Required: false, MaxLength: null),
            new("warranty", "Under warranty", "checkbox", "false", null, Enabled: false, Required: false, MaxLength: null),
            new("notes", "Notes", "text", "Fan is noisy", null, Enabled: true, Required: false, MaxLength: null),
        ];

        // Tab order, then each record exactly (Equivalent, because records compare lists by reference).
        Assert.Equal(expected.Select(f => f.Id), screen.Fields.Select(f => f.Id));
        foreach (var (want, got) in expected.Zip(screen.Fields))
        {
            Assert.Equivalent(want, got, strict: true);
        }

        // Equivalent ignores collection order; the options' order is what the person sees.
        Assert.Equal(["Red", "Green", "Blue"], screen.Fields.Single(f => f.Id == "colour").Options!);

        ButtonDescription[] buttons =
        [
            new("save", "Save", Enabled: true, Destructive: false),
            new("delete", "Delete", Enabled: true, Destructive: true),
            new("archive", "Archive", Enabled: false, Destructive: false),
        ];
        Assert.Equal(buttons, screen.Buttons);

        Assert.Equal(["items"], screen.Lists.Select(l => l.Id));
    }

    [Fact]
    public void Leaves_out_controls_without_metadata()
    {
        var screen = DescribeShown(TestForms.KitchenSink);

        Assert.DoesNotContain(screen.Fields, f => f.Value == "no metadata");
        Assert.Equal(8, screen.Fields.Count);
    }

    [Fact]
    public void Leaves_out_hidden_and_unselected_tab_controls()
    {
        var ids = AllIds(DescribeShown(TestForms.KitchenSink));

        Assert.DoesNotContain("secret", ids);
        Assert.DoesNotContain("inHiddenPanel", ids);
        Assert.DoesNotContain("history", ids);
        Assert.Contains("notes", ids);
    }

    [Fact]
    public void Disabled_controls_are_described_as_disabled()
    {
        var screen = DescribeShown(TestForms.KitchenSink);

        Assert.False(screen.Fields.Single(f => f.Id == "reference").Enabled);
        Assert.False(screen.Fields.Single(f => f.Id == "warranty").Enabled);
        Assert.False(screen.Buttons.Single(b => b.Id == "archive").Enabled);
        Assert.True(screen.Fields.Single(f => f.Id == "customerName").Enabled);
        Assert.True(screen.Buttons.Single(b => b.Id == "save").Enabled);
    }

    [Fact]
    public void List_rows_carry_key_cells_and_selection()
    {
        var list = DescribeShown(TestForms.KitchenSink).Lists.Single();

        Assert.Equal("items", list.Id);
        Assert.Equal("Items", list.Label);
        Assert.Equal(["Number", "Device"], list.Columns);
        Assert.Collection(
            list.Rows,
            row =>
            {
                Assert.Equal("J-1", row.Key);
                Assert.Equal(["J-1", "Laptop"], row.Cells);
                Assert.False(row.Selected);
            },
            row =>
            {
                Assert.Equal("J-2", row.Key);
                Assert.Equal(["J-2", "Printer"], row.Cells);
                Assert.True(row.Selected);
            });
    }

    [Fact]
    public void Duplicate_ids_throw_naming_the_id()
    {
        InvalidOperationException? thrown = null;

        StaRunner.Run(() =>
        {
            using var form = TestForms.DuplicateIds();
            form.Show();
            thrown = Assert.Throws<InvalidOperationException>(() => ScreenDescriber.Describe(form));
        });

        Assert.NotNull(thrown);
        Assert.Contains("'customerName'", thrown.Message);
    }

    [Fact]
    public void Json_is_camelCase_and_omits_nulls()
    {
        var screen = DescribeShown(TestForms.Small);

        var json = JsonSerializer.Serialize(screen, SurfaceJson.Options);

        Assert.Equal(
            """{"id":"small","title":"Small","fields":[{"id":"name","label":"Name","kind":"text","value":"Ada","enabled":true,"required":true,"maxLength":20},{"id":"colour","label":"Colour","kind":"choice","options":["Red","Green"],"enabled":true,"required":false}],"buttons":[{"id":"save","label":"Save","enabled":true,"destructive":false}],"lists":[{"id":"rows","label":"Rows","columns":["Number"],"rows":[{"key":"J-1","cells":["J-1"],"selected":false}]}]}""",
            json);
    }

    private static ScreenDescription DescribeShown(Func<Form> build)
    {
        ScreenDescription? screen = null;

        StaRunner.Run(() =>
        {
            using var form = build();
            form.Show();
            screen = ScreenDescriber.Describe(form);
        });

        return screen!;
    }

    private static HashSet<string> AllIds(ScreenDescription screen) =>
    [
        .. screen.Fields.Select(f => f.Id),
        .. screen.Buttons.Select(b => b.Id),
        .. screen.Lists.Select(l => l.Id),
    ];
}
