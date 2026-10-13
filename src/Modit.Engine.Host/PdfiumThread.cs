using System.Collections.Concurrent;

namespace Modit.Engine.Host;

/// <summary>
/// PDFium is not thread-safe, so every PDFium call in this process is funnelled through this one
/// dedicated thread. Work is executed in submission order.
/// </summary>
internal sealed class PdfiumThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public PdfiumThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PDFium" };
        _thread.Start();
    }

    public bool IsCurrent => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(work());
            }
#pragma warning disable CA1031 // The exception is transported to the awaiting caller, not swallowed.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                completion.TrySetException(ex);
            }
        }, CancellationToken.None);
        return completion.Task;
    }

    public Task RunAsync(Action work, CancellationToken cancellationToken = default) =>
        RunAsync(
            () =>
            {
                work();
                return true;
            },
            cancellationToken);

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!IsCurrent)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        _queue.Dispose();
    }

    private void Run()
    {
        foreach (Action work in _queue.GetConsumingEnumerable())
        {
            work();
        }
    }
}
