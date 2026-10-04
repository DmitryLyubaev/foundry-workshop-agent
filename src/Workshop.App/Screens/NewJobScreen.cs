using Workshop.Core;

namespace Workshop.App.Screens;

/// <summary>Books a customer's device in with its fault, then shows the new job.</summary>
internal sealed class NewJobScreen : WorkshopScreen
{
    private readonly ComboBox customer;
    private readonly ComboBox device;
    private readonly TextBox fault;

    public NewJobScreen(WorkshopNavigator navigator, JobService service)
        : base("new-job", "New job", navigator, service)
    {
        customer = ChoiceField("customer", "Customer", required: true);
        device = ChoiceField("device", "Device", required: true);
        fault = TextField("fault", "Fault", maxLength: JobService.FaultMaxLength, required: true);
        var bookIn = ActionButton("book-in", "Book in");

        AddRow("Customer", customer);
        AddRow("Device", device);
        AddRow("Fault", fault);
        AddRow(null, bookIn);

        // The devices on offer are only the chosen customer's.
        customer.SelectedIndexChanged += (_, _) => Fill(device, DevicesOf(SelectedKey(customer)));
        OnPress(bookIn, BookIn);
    }

    /// <summary>Each opening starts a new, empty job card.</summary>
    protected override void OnOpened()
    {
        Fill(customer, Service.Customers(null).Select(c => new Option(c.Id, $"{c.Id} {c.Name}")));
        customer.SelectedIndex = -1;
        Fill(device, []);
        fault.Text = "";
    }

    private IEnumerable<Option> DevicesOf(string? customerId) =>
        customerId is null ? [] : Service.DevicesOf(customerId).Select(d => new Option(d.Id, $"{d.Id} {DeviceText(d)}"));

    private void BookIn()
    {
        if (SelectedKey(customer) is null)
        {
            Refuse("Select a customer first.");
            return;
        }

        if (SelectedKey(device) is not { } deviceId)
        {
            Refuse("Select a device first.");
            return;
        }

        if (Report(Service.BookIn(deviceId, fault.Text, out var jobId)))
        {
            Navigator.OpenJob(jobId!);
        }
    }
}
