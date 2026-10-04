using Workshop.Core;

namespace Workshop.App.Screens;

/// <summary>The job cards, filtered by a search and a status, with a way into each.</summary>
internal sealed class JobListScreen : WorkshopScreen
{
    private const string AllStatuses = "all";

    private readonly TextBox search;
    private readonly ComboBox statusFilter;
    private readonly ListView jobs;

    public JobListScreen(WorkshopNavigator navigator, JobService service)
        : base("job-list", "Jobs", navigator, service)
    {
        search = TextField("search", "Search");
        statusFilter = ChoiceField("status-filter", "Status");
        statusFilter.Items.AddRange([AllStatuses, .. JobStatusNames.All]);
        statusFilter.SelectedIndex = 0;
        jobs = ListField("jobs", "Jobs", 420, ("Job", 80), ("Customer", 200), ("Device", 240), ("Status", 130));
        var openJob = ActionButton("open-job", "Open job");
        var newJob = ActionButton("new-job", "New job");

        AddRow("Search", search);
        AddRow("Status", statusFilter);
        AddRow("Jobs", jobs);
        AddRow(null, openJob, newJob);

        search.TextChanged += (_, _) => Reload();
        statusFilter.SelectedIndexChanged += (_, _) => Reload();
        OnPress(openJob, OpenSelected);
        OnPress(newJob, () => Navigator.Open("new-job"));
    }

    /// <summary>The job whose row is selected, if any.</summary>
    public string? SelectedJobId => SelectedKey(jobs);

    protected override void OnOpened() => Reload();

    private void Reload()
    {
        var status = statusFilter.SelectedItem is string name ? JobStatusNames.Parse(name) : null;
        var devices = new Dictionary<string, Device>(StringComparer.Ordinal);
        var customers = new Dictionary<string, Customer>(StringComparer.Ordinal);

        Fill(jobs, Service.Jobs(status, search.Text).Select(job =>
        {
            var device = Lookup(devices, job.DeviceId, Service.Device);
            var customer = Lookup(customers, device.CustomerId, Service.Customer);
            return (job.Id, new[] { job.Id, customer.Name, DeviceText(device), JobStatusNames.Name(job.Status) });
        }));
    }

    private void OpenSelected()
    {
        if (SelectedJobId is not { } jobId)
        {
            Refuse("Select a job first.");
            return;
        }

        Navigator.OpenJob(jobId);
    }

    /// <summary>A record by ID, read once per reload: the foreign keys guarantee it exists.</summary>
    private static T Lookup<T>(Dictionary<string, T> seen, string id, Func<string, T?> read)
        where T : class
    {
        if (!seen.TryGetValue(id, out var record))
        {
            record = read(id) ?? throw new InvalidDataException($"The record {id} is missing.");
            seen[id] = record;
        }

        return record;
    }
}
