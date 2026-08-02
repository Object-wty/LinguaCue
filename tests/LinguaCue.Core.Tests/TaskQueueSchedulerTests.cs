using LinguaCue.Services;

namespace LinguaCue.Tests;

public sealed class TaskQueueSchedulerTests
{
    [Fact]
    public async Task Enqueue_RespectsConcurrencyAndStartsJobsInFifoOrder()
    {
        var scheduler = new TaskQueueScheduler(2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<int>();
        var sync = new object();
        var active = 0;
        var maximumActive = 0;
        var completed = 0;

        for (var index = 0; index < 4; index++)
        {
            var jobIndex = index;
            scheduler.Enqueue(async () =>
            {
                lock (sync)
                {
                    started.Add(jobIndex);
                    active++;
                    maximumActive = Math.Max(maximumActive, active);
                    if (started.Count == 2)
                    {
                        firstWave.TrySetResult();
                    }
                }

                await gate.Task;
                lock (sync)
                {
                    active--;
                    completed++;
                    if (completed == 4)
                    {
                        allCompleted.TrySetResult();
                    }
                }
            });
        }

        await firstWave.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, scheduler.RunningCount);
        Assert.Equal(2, scheduler.PendingCount);
        gate.SetResult();
        await allCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([0, 1, 2, 3], started);
        Assert.Equal(2, maximumActive);
        await WaitForAsync(() => scheduler.RunningCount == 0 && scheduler.PendingCount == 0);
        Assert.Equal(0, scheduler.RunningCount);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task CancelQueuedJob_RemovesItImmediatelyAndKeepsFollowingJob()
    {
        var scheduler = new TaskQueueScheduler(1);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var secondCancellation = new CancellationTokenSource();

        scheduler.Enqueue(async () =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
        });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        scheduler.Enqueue(() => Task.CompletedTask, secondCancellation.Token);
        scheduler.Enqueue(() =>
        {
            thirdStarted.SetResult();
            return Task.CompletedTask;
        });
        Assert.Equal(2, scheduler.PendingCount);

        secondCancellation.Cancel();
        Assert.Equal(1, scheduler.PendingCount);

        releaseFirst.SetResult();
        await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => scheduler.RunningCount == 0 && scheduler.PendingCount == 0);

        Assert.Equal(0, scheduler.RunningCount);
        Assert.Equal(0, scheduler.PendingCount);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
