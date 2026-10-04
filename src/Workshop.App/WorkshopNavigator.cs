using Workshop.App.Screens;
using Workshop.Core;
using Workshop.Surface;

namespace Workshop.App;

/// <summary>
/// The app's shell as the agent surface sees it. It owns the six screens, keeps them all in the
/// host, and shows one at a time. A detail screen shows its current record: the record selected or
/// opened most recently, where booking a job in opens the new job. With none, opening the detail
/// screen opens its list instead. A list, when opened, shows the current record selected.
/// </summary>
internal sealed class WorkshopNavigator : IScreenNavigator
{
    private readonly JobListScreen jobList;
    private readonly JobDetailScreen jobDetail;
    private readonly CustomerListScreen customerList;
    private readonly CustomerDetailScreen customerDetail;
    private WorkshopScreen current;

    public WorkshopNavigator(Control host, JobService service)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(service);

        jobList = new JobListScreen(this, service);
        jobDetail = new JobDetailScreen(this, service);
        customerList = new CustomerListScreen(this, service);
        customerDetail = new CustomerDetailScreen(this, service);
        All = [jobList, jobDetail, new NewJobScreen(this, service), customerList, customerDetail, new PartsScreen(this, service)];
        Screens = [.. All.Select(s => (s.Id, s.Title))];

        foreach (var screen in All)
        {
            screen.Visible = false;
            host.Controls.Add(screen);
        }

        current = jobList;
        Show(jobList);
    }

    /// <summary>Raised after another screen is shown.</summary>
    public event EventHandler? CurrentChanged;

    /// <summary>Every screen, in menu order.</summary>
    public IReadOnlyList<WorkshopScreen> All { get; }

    public IReadOnlyList<(string Id, string Title)> Screens { get; }

    public Control Current => current;

    public WorkshopScreen CurrentScreen => current;

    /// <summary>The job the job detail screen shows: the one selected or opened most recently.</summary>
    public string? CurrentJobId { get; private set; }

    /// <summary>The customer the customer detail screen shows: the one selected or opened most recently.</summary>
    public string? CurrentCustomerId { get; private set; }

    public bool Open(string id)
    {
        switch (id)
        {
            case "job-detail":
                if (CurrentJobId is { } jobId)
                {
                    OpenJob(jobId);
                }
                else
                {
                    Show(jobList);
                }

                return true;

            case "customer-detail":
                if (CurrentCustomerId is { } customerId)
                {
                    OpenCustomer(customerId);
                }
                else
                {
                    Show(customerList);
                }

                return true;

            default:
                if (All.FirstOrDefault(s => s.Id == id) is not { } screen)
                {
                    return false;
                }

                Show(screen);
                return true;
        }
    }

    /// <summary>Makes the job current without showing it, as selecting its row does. The ID must be a job's.</summary>
    public void NoteJob(string jobId) => CurrentJobId = jobId;

    /// <summary>Makes the customer current without showing them, as selecting their row does. The ID must be a customer's.</summary>
    public void NoteCustomer(string customerId) => CurrentCustomerId = customerId;

    /// <summary>Makes the job current and shows its detail screen. The ID must be a job's.</summary>
    public void OpenJob(string jobId)
    {
        CurrentJobId = jobId;
        jobDetail.JobId = jobId;
        Show(jobDetail);
    }

    /// <summary>Makes the customer current and shows their detail screen. The ID must be a customer's.</summary>
    public void OpenCustomer(string customerId)
    {
        CurrentCustomerId = customerId;
        customerDetail.CustomerId = customerId;
        Show(customerDetail);
    }

    private void Show(WorkshopScreen screen)
    {
        if (screen != current)
        {
            current.Visible = false;
            screen.Visible = true;
            current = screen;
        }
        else
        {
            screen.Visible = true;
        }

        screen.Opened();
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }
}
