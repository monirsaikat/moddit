namespace Modit.Core.Jobs;

/// <summary>Lower value runs first. Viewer work always beats background batches.</summary>
public enum JobPriority
{
    Interactive = 0,
    UserBatch = 1,
    Background = 2,
}

public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record JobSpec(
    string Name, JobPriority Priority, Func<IProgress<double>, CancellationToken, Task> Work);

/// <summary>A scheduled unit of work. Observe <see cref="Completion"/> or <see cref="Changed"/>.</summary>
public sealed class Job : IDisposable
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Job(JobSpec spec, long sequence)
    {
        Spec = spec;
        Sequence = sequence;
        Id = Guid.NewGuid();
    }

    public event EventHandler? Changed;

    public Guid Id { get; }

    public string Name => Spec.Name;

    public JobPriority Priority => Spec.Priority;

    public JobState State { get; private set; } = JobState.Queued;

    /// <summary>0 to 1.</summary>
    public double Progress { get; private set; }

    public Exception? Error { get; private set; }

    /// <summary>Completes when the job ends in any state; faults or cancels to mirror the outcome.</summary>
    public Task Completion => _completion.Task;

    internal JobSpec Spec { get; }

    internal long Sequence { get; }

    internal CancellationTokenSource Cancellation { get; } = new();

    public void Dispose() => Cancellation.Dispose();

    internal void ReportProgress(double value)
    {
        Progress = Math.Clamp(value, 0, 1);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Transition(JobState state, Exception? error = null)
    {
        State = state;
        Error = error;
        Changed?.Invoke(this, EventArgs.Empty);
        switch (state)
        {
            case JobState.Succeeded:
                _completion.TrySetResult();
                break;
            case JobState.Failed:
                _completion.TrySetException(error!);
                break;
            case JobState.Cancelled:
                _completion.TrySetCanceled();
                break;
        }
    }
}

public interface IJobScheduler
{
    Job Enqueue(JobSpec spec);

    void Cancel(Job job);
}

/// <summary>
/// In-memory priority scheduler: at most <c>maxConcurrency</c> jobs run at once, the highest priority
/// first and first-come first-served within a priority. Persistence across restarts is added in
/// milestone 2 on top of this contract.
/// </summary>
public sealed class JobScheduler : IJobScheduler
{
    private readonly Lock _lock = new();
    private readonly PriorityQueue<Job, (int Priority, long Sequence)> _queue = new();
    private readonly int _maxConcurrency;
    private int _running;
    private long _sequence;

    public JobScheduler(int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        _maxConcurrency = maxConcurrency;
    }

    public Job Enqueue(JobSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Job job;
        lock (_lock)
        {
            job = new Job(spec, _sequence++);
            _queue.Enqueue(job, ((int)spec.Priority, job.Sequence));
        }

        Pump();
        return job;
    }

    public void Cancel(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        bool wasQueued;
        lock (_lock)
        {
            wasQueued = job.State == JobState.Queued;
            if (wasQueued)
            {
                // Left in the queue; skipped when dequeued because it is no longer Queued.
                job.Transition(JobState.Cancelled);
            }
        }

        if (!wasQueued && job.State == JobState.Running)
        {
            job.Cancellation.Cancel();
        }
    }

    private void Pump()
    {
        while (true)
        {
            Job? next = null;
            lock (_lock)
            {
                if (_running >= _maxConcurrency)
                {
                    return;
                }

                while (_queue.TryDequeue(out Job? candidate, out _))
                {
                    if (candidate.State == JobState.Queued)
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next is null)
                {
                    return;
                }

                _running++;
                next.Transition(JobState.Running);
            }

            _ = RunAsync(next);
        }
    }

    private async Task RunAsync(Job job)
    {
        try
        {
            // Task.Run so a job that blocks before its first await cannot stall the caller of Enqueue.
            await Task.Run(
                () => job.Spec.Work(new Progress<double>(job.ReportProgress), job.Cancellation.Token),
                job.Cancellation.Token).ConfigureAwait(false);
            job.Transition(JobState.Succeeded);
        }
        catch (OperationCanceledException)
        {
            job.Transition(JobState.Cancelled);
        }
#pragma warning disable CA1031 // A job boundary: every failure becomes the job's recorded error.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            job.Transition(JobState.Failed, ex);
        }
        finally
        {
            lock (_lock)
            {
                _running--;
            }

            Pump();
        }
    }
}
