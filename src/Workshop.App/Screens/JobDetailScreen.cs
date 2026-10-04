using System.ComponentModel;
using System.Globalization;
using Workshop.Core;

namespace Workshop.App.Screens;

/// <summary>
/// One job card: its status, parts and notes, and the changes the workshop's rules allow. Every
/// button reaches the rules, and a refusal is shown inline rather than prevented by the screen.
/// </summary>
internal sealed class JobDetailScreen : WorkshopScreen
{
    private readonly TextBox job;
    private readonly ComboBox status;
    private readonly TextBox fault;
    private readonly ListView parts;
    private readonly ComboBox part;
    private readonly NumericUpDown quantity;
    private readonly TextBox note;
    private readonly ListView notes;

    public JobDetailScreen(WorkshopNavigator navigator, JobService service)
        : base("job-detail", "Job", navigator, service)
    {
        job = ShownField("job", "Job");
        status = ChoiceField("status", "Status");
        status.Items.AddRange([.. JobStatusNames.All]);
        var saveStatus = ActionButton("save-status", "Save status");
        fault = TextField("fault", "Fault", enabled: false);
        parts = ListField("parts", "Parts", 130, ("Part", 70), ("Name", 220), ("Quantity", 80), ("State", 100));
        part = ChoiceField("part", "Part");
        quantity = NumberField("quantity", "Quantity", 1, 20);
        var addPart = ActionButton("add-part", "Add part");
        var fitOrderedParts = ActionButton("fit-ordered-parts", "Fit ordered parts");
        note = TextField("note", "Note", maxLength: JobService.NoteMaxLength);
        var addNote = ActionButton("add-note", "Add note");
        notes = ListField("notes", "Notes", 150, ("At (UTC)", 130), ("Note", 560));
        var cancelJob = ActionButton("cancel-job", "Cancel job", destructive: true);

        AddRow("Job", job);
        AddRow("Status", status, saveStatus);
        AddRow("Fault", fault);
        AddRow("Parts", parts);
        AddRow("Add a part", part, quantity, addPart, fitOrderedParts);
        AddRow("Note", note, addNote);
        AddRow("Notes", notes);
        AddRow(null, cancelJob);

        OnPress(saveStatus, SaveStatus);
        OnPress(addPart, AddPart);
        OnPress(fitOrderedParts, () => ApplyAndReload(Service.FitOrderedParts(JobIdOrThrow)));
        OnPress(addNote, AddNote);
        OnPress(cancelJob, () => ApplyAndReload(Service.Cancel(JobIdOrThrow)));
    }

    /// <summary>The job showing. The navigator sets it before showing the screen.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? JobId { get; set; }

    private string JobIdOrThrow => JobId ?? throw new InvalidOperationException("The job detail screen is showing no job.");

    protected override void OnOpened()
    {
        quantity.Value = quantity.Minimum;
        note.Text = "";
        part.SelectedIndex = -1;
        Reload();
    }

    private void Reload()
    {
        var card = Service.Job(JobIdOrThrow) ?? throw new InvalidOperationException($"There is no job {JobId}.");
        var device = Service.Device(card.DeviceId)!;
        var customer = Service.Customer(device.CustomerId)!;
        var stock = Service.Parts();
        var names = stock.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);

        job.Text = $"{card.Id}: {DeviceText(device)} for {customer.Name}";
        status.SelectedItem = JobStatusNames.Name(card.Status);
        fault.Text = card.Fault;
        Fill(part, stock.Select(p => new Option(p.Id, $"{p.Id} {p.Name}")));

        // A part can be on a job more than once, so its row key counts its occurrences: P-04#1, P-04#2.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        Fill(parts, Service.JobParts(card.Id).Select(p =>
        {
            var occurrence = seen[p.PartId] = seen.GetValueOrDefault(p.PartId) + 1;
            return ($"{p.PartId}#{occurrence}", new[]
            {
                p.PartId,
                names.GetValueOrDefault(p.PartId, ""),
                p.Quantity.ToString(CultureInfo.InvariantCulture),
                p.State == PartState.OnOrder ? "on order" : "fitted",
            });
        }));

        Fill(notes, Service.Notes(card.Id).Select((n, index) => (
            $"note-{index + 1}",
            new[] { n.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), n.Text })));
    }

    private void SaveStatus()
    {
        if (status.SelectedItem is not string name || JobStatusNames.Parse(name) is not { } to)
        {
            Refuse("Select a status first.");
            return;
        }

        ApplyAndReload(Service.SetStatus(JobIdOrThrow, to));
    }

    private void AddPart()
    {
        if (SelectedKey(part) is not { } partId)
        {
            Refuse("Select a part first.");
            return;
        }

        ApplyAndReload(Service.AddPart(JobIdOrThrow, partId, (int)quantity.Value));
    }

    private void AddNote()
    {
        if (Report(Service.AddNote(JobIdOrThrow, note.Text)))
        {
            note.Text = "";
            Reload();
        }
    }

    /// <summary>Reports the rule's outcome; on success, shows the job as it now is.</summary>
    private void ApplyAndReload(RuleResult result)
    {
        if (Report(result))
        {
            Reload();
        }
    }
}
