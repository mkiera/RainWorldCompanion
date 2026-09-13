namespace RainWorldCompanion.ViewModels;

internal sealed class DownloadProgress : IProgress<double>, IDisposable
{
    private readonly object _sync = new();
    private readonly IProgress<double> _queued;
    private readonly Action<double> _update;
    private bool _finished;

    public DownloadProgress(Action<double> update)
    {
        _update = update;
        _queued = new Progress<double>(fraction =>
        {
            lock (_sync)
            {
                if (!_finished) _update(fraction);
            }
        });
    }

    public void Report(double value) => _queued.Report(value);

    public void Complete()
    {
        lock (_sync)
        {
            _finished = true;
            _update(1);
        }
    }

    public void Dispose()
    {
        lock (_sync) _finished = true;
    }
}
