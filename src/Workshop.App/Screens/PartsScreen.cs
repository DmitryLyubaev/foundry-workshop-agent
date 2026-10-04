using System.Globalization;
using Workshop.Core;

namespace Workshop.App.Screens;

/// <summary>The parts and their stock, and stock received for a part.</summary>
internal sealed class PartsScreen : WorkshopScreen
{
    private readonly ListView parts;
    private readonly NumericUpDown quantity;

    public PartsScreen(WorkshopNavigator navigator, JobService service)
        : base("parts", "Parts", navigator, service)
    {
        parts = ListField("parts", "Parts", 320, ("Part", 70), ("Name", 240), ("Stock", 80));
        quantity = NumberField("quantity", "Quantity", 1, 100);
        var receiveStock = ActionButton("receive-stock", "Receive stock");

        AddRow("Parts", parts);
        AddRow("Quantity", quantity);
        AddRow(null, receiveStock);

        OnPress(receiveStock, ReceiveStock);
    }

    protected override void OnOpened()
    {
        quantity.Value = quantity.Minimum;
        Reload();
    }

    private void Reload() =>
        Fill(parts, Service.Parts().Select(p => (p.Id, new[] { p.Id, p.Name, p.Stock.ToString(CultureInfo.InvariantCulture) })));

    private void ReceiveStock()
    {
        if (SelectedKey(parts) is not { } partId)
        {
            Refuse("Select a part first.");
            return;
        }

        if (Report(Service.ReceiveStock(partId, (int)quantity.Value)))
        {
            Reload();
        }
    }
}
