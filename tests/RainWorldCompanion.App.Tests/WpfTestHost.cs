using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace RainWorldCompanion.App.Tests;

internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> WorkerDispatcher = new(CreateDispatcher);

    internal static Exception? Run(Action action)
    {
        try
        {
            WorkerDispatcher.Value.Invoke(action);
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    internal static async Task<Exception?> RunAsync(Func<Task> action)
    {
        try
        {
            await WorkerDispatcher.Value.InvokeAsync(action).Task.Unwrap();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static Dispatcher CreateDispatcher()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}
