using Workshop.Core;

namespace Workshop.App.Screens;

/// <summary>The customers, filtered by a search, with a way into each.</summary>
internal sealed class CustomerListScreen : WorkshopScreen
{
    private readonly TextBox search;
    private readonly ListView customers;

    public CustomerListScreen(WorkshopNavigator navigator, JobService service)
        : base("customer-list", "Customers", navigator, service)
    {
        search = TextField("search", "Search");
        customers = ListField("customers", "Customers", 420, ("Customer", 80), ("Name", 240), ("Phone", 120));
        var openCustomer = ActionButton("open-customer", "Open customer");

        AddRow("Search", search);
        AddRow("Customers", customers);
        AddRow(null, openCustomer);

        search.TextChanged += (_, _) => Reload(SelectedCustomerId);

        // As on the job list: only a row becoming selected makes its customer current.
        customers.ItemSelectionChanged += (_, e) =>
        {
            if (e.IsSelected && e.Item is { } row)
            {
                Navigator.NoteCustomer(row.Name);
            }
        };
        OnPress(openCustomer, OpenSelected);
    }

    /// <summary>The customer whose row is selected, if any.</summary>
    private string? SelectedCustomerId => SelectedKey(customers);

    /// <summary>Shows the current customer selected, as the job list shows the current job.</summary>
    protected override void OnOpened() => Reload(Navigator.CurrentCustomerId);

    private void Reload(string? selected) =>
        Fill(customers, Service.Customers(search.Text).Select(c => (c.Id, new[] { c.Id, c.Name, c.Phone })), selected);

    private void OpenSelected()
    {
        if (SelectedCustomerId is not { } customerId)
        {
            Refuse("Select a customer first.");
            return;
        }

        Navigator.OpenCustomer(customerId);
    }
}
