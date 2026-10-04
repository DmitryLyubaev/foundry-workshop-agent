using System.Runtime.ExceptionServices;

namespace Workshop.Surface.Tests;

/// <summary>
/// Runs test code on a dedicated STA thread inside a WinForms message loop, as the app's UI
/// thread would, so tests can create forms, show them and use their handles.
/// </summary>
internal static class StaRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);

            // Posted, so the action runs inside the message loop rather than before it starts.
            context.Post(_ =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    failure = ExceptionDispatchInfo.Capture(e);
                }
                finally
                {
                    Application.ExitThread();
                }
            }, null);

            Application.Run();
        })
        {
            IsBackground = true,
            Name = "StaRunner",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(Timeout))
        {
            throw new TimeoutException($"The UI action did not finish within {Timeout.TotalSeconds} seconds.");
        }

        failure?.Throw();
    }
}
