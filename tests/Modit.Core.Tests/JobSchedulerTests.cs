using Modit.Core.Jobs;

namespace Modit.Core.Tests;

public sealed class JobSchedulerTests
{
    [Fact]
    public async Task Higher_priority_jobs_run_before_earlier_lower_priority_jobs()
    {
        var scheduler = new JobScheduler(maxConcurrency: 1);
        var order = new List<string>();
        var gate = new TaskCompletionSource();

        // The first job occupies the only slot so the rest queue up behind it.
        Job blocker = scheduler.Enqueue(new JobSpec("blocker", JobPriority.Background, (_, _) => gate.Task));
        Job background = scheduler.Enqueue(Record("background", JobPriority.Background));
        Job batch = scheduler.Enqueue(Record("batch", JobPriority.UserBatch));
        Job interactive = scheduler.Enqueue(Record("interactive", JobPriority.Interactive));

        gate.SetResult();
        await Task.WhenAll(blocker.Completion, background.Completion, batch.Completion, interactive.Completion);

        Assert.Equal(["interactive", "batch", "background"], order);

        JobSpec Record(string name, JobPriority priority) => new(name, priority, (_, _) =>
        {
            lock (order)
            {
                order.Add(name);
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Concurrency_limit_is_respected()
    {
        var scheduler = new JobScheduler(maxConcurrency: 2);
        int running = 0;
        int peak = 0;

        List<Job> jobs = [.. Enumerable.Range(0, 8).Select(i => scheduler.Enqueue(new JobSpec($"job{i}", JobPriority.UserBatch, async (_, ct) =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref running);
        })))];

        await Task.WhenAll(jobs.Select(j => j.Completion));

        Assert.Equal(2, peak);
        Assert.All(jobs, j => Assert.Equal(JobState.Succeeded, j.State));
    }

    [Fact]
    public async Task Cancelling_a_running_job_cancels_its_token()
    {
        var scheduler = new JobScheduler(maxConcurrency: 1);
        var started = new TaskCompletionSource();
        Job job = scheduler.Enqueue(new JobSpec("long", JobPriority.UserBatch, async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }));

        await started.Task;
        scheduler.Cancel(job);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion);
        Assert.Equal(JobState.Cancelled, job.State);
    }

    [Fact]
    public async Task Cancelling_a_queued_job_means_it_never_runs()
    {
        var scheduler = new JobScheduler(maxConcurrency: 1);
        var gate = new TaskCompletionSource();
        bool ran = false;
        Job blocker = scheduler.Enqueue(new JobSpec("blocker", JobPriority.UserBatch, (_, _) => gate.Task));
        Job queued = scheduler.Enqueue(new JobSpec("queued", JobPriority.UserBatch, (_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        }));

        scheduler.Cancel(queued);
        gate.SetResult();
        await blocker.Completion;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.Completion);

        Assert.False(ran);
    }

    [Fact]
    public async Task A_failing_job_records_its_error_and_does_not_stop_the_queue()
    {
        var scheduler = new JobScheduler(maxConcurrency: 1);
        Job failing = scheduler.Enqueue(new JobSpec(
            "fails", JobPriority.UserBatch, (_, _) => throw new ModitException(ErrorCode.PdfCorrupt, "bad file")));
        Job next = scheduler.Enqueue(new JobSpec("next", JobPriority.UserBatch, (_, _) => Task.CompletedTask));

        ModitException ex = await Assert.ThrowsAsync<ModitException>(() => failing.Completion);
        await next.Completion;

        Assert.Equal(ErrorCode.PdfCorrupt, ex.Code);
        Assert.Equal(JobState.Failed, failing.State);
        Assert.Equal(JobState.Succeeded, next.State);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
        }
        while (value > current && Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
