using System.ComponentModel;
using Workshop.Core;

namespace Workshop.App.Screens;

/// <summary>One customer, their devices, and a new device for them.</summary>
internal sealed class CustomerDetailScreen : WorkshopScreen
{
    private readonly TextBox name;
    private readonly TextBox phone;
    private readonly ListView devices;
    private readonly ComboBox kind;
    private readonly TextBox model;
    private readonly TextBox serial;

    public CustomerDetailScreen(WorkshopNavigator navigator, JobService service)
        : base("customer-detail", "Customer", navigator, service)
    {
        name = TextField("name", "Name", enabled: false);
        phone = TextField("phone", "Phone", enabled: false);
        devices = ListField("devices", "Devices", 200, ("Device", 70), ("Kind", 90), ("Model", 200), ("Serial", 140));
        kind = ChoiceField("kind", "Kind", required: true);
        kind.Items.AddRange([.. DeviceKinds.All]);
        model = TextField("model", "Model", maxLength: JobService.DeviceTextMaxLength, required: true);
        serial = TextField("serial", "Serial", maxLength: JobService.DeviceTextMaxLength, required: true);
        var addDevice = ActionButton("add-device", "Add device");

        AddRow("Name", name);
        AddRow("Phone", phone);
        AddRow("Devices", devices);
        AddRow("Kind", kind);
        AddRow("Model", model);
        AddRow("Serial", serial);
        AddRow(null, addDevice);

        OnPress(addDevice, AddDevice);
    }

    /// <summary>The customer showing. The navigator sets it before showing the screen.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? CustomerId { get; set; }

    private string CustomerIdOrThrow => CustomerId ?? throw new InvalidOperationException("The customer detail screen is showing no customer.");

    protected override void OnOpened()
    {
        ClearNewDevice();
        Reload();
    }

    private void Reload()
    {
        var customer = Service.Customer(CustomerIdOrThrow) ?? throw new InvalidOperationException($"There is no customer {CustomerId}.");
        name.Text = customer.Name;
        phone.Text = customer.Phone;
        Fill(devices, Service.DevicesOf(customer.Id).Select(d => (d.Id, new[] { d.Id, d.Kind, d.Model, d.Serial })));
    }

    private void AddDevice()
    {
        // With no kind chosen the rule refuses, and names the kinds.
        if (Report(Service.AddDevice(CustomerIdOrThrow, kind.SelectedItem as string ?? "", model.Text, serial.Text, out _)))
        {
            ClearNewDevice();
            Reload();
        }
    }

    private void ClearNewDevice()
    {
        kind.SelectedIndex = -1;
        model.Text = "";
        serial.Text = "";
    }
}
