using System.Runtime.ExceptionServices;

namespace Workshop.Surface.Tests;

/// <summary>
/// A long-lived STA thread running a WinForms message loop, as the app's UI thread does, for tests
/// where other threads (an HTTP listener) call onto the UI while the test runs.
/// </summary>
internal sealed class UiThread : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Thread thread;
    private readonly SynchronizationContext context;

    public UiThread()
    {
        var ready = new TaskCompletionSource<SynchronizationContext>(TaskCreationOptions.RunContinuationsAsynchronously);

        thread = new Thread(() =>
        {
            var loop = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(loop);
            ready.SetResult(loop);
            Application.Run();
        })
        {
            IsBackground = true,
            Name = "UiThread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        context = ready.Task.Wait(Timeout)
            ? ready.Task.Result
            : throw new TimeoutException("The UI thread did not start.");
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread and returns its result.</summary>
    public T Run<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;

        context.Send(_ =>
        {
            try
            {
                result = work();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        }, null);

        failure?.Throw();
        return result;
    }

    public void Run(Action work) => Run(() =>
    {
        work();
        return 0;
    });

    public void Dispose()
    {
        context.Post(_ => Application.ExitThread(), null);

        if (!thread.Join(Timeout))
        {
            throw new TimeoutException("The UI thread did not stop.");
        }
    }
}
