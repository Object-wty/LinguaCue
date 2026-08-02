namespace LinguaCue.Services;

public sealed class TaskQueueScheduler
{
    private readonly object sync = new();
    private readonly Queue<WorkItem> pending = new();
    private int runningCount;
    private int maxConcurrency;

    public TaskQueueScheduler(int maxConcurrency = 2)
    {
        this.maxConcurrency = Math.Clamp(maxConcurrency, 1, 4);
    }

    public event EventHandler? StateChanged;

    public int MaxConcurrency
    {
        get
        {
            lock (sync)
            {
                return maxConcurrency;
            }
        }
        set
        {
            lock (sync)
            {
                maxConcurrency = Math.Clamp(value, 1, 4);
            }

            Pump();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int RunningCount
    {
        get
        {
            lock (sync)
            {
                return runningCount;
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (sync)
            {
                return pending.Count;
            }
        }
    }

    public void Enqueue(Func<Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var item = new WorkItem(work, cancellationToken);
        item.CancellationRegistration = cancellationToken.Register(Pump);
        if (cancellationToken.IsCancellationRequested)
        {
            item.CancellationRegistration.Dispose();
            return;
        }

        lock (sync)
        {
            pending.Enqueue(item);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        Pump();
    }

    private void Pump()
    {
        List<WorkItem> toStart = [];
        lock (sync)
        {
            var pendingCount = pending.Count;
            for (var index = 0; index < pendingCount; index++)
            {
                var queuedItem = pending.Dequeue();
                if (queuedItem.CancellationToken.IsCancellationRequested)
                {
                    queuedItem.CancellationRegistration.Dispose();
                }
                else
                {
                    pending.Enqueue(queuedItem);
                }
            }

            while (runningCount < maxConcurrency && pending.TryDequeue(out var item))
            {
                runningCount++;
                toStart.Add(item);
            }
        }

        foreach (var item in toStart)
        {
            item.CancellationRegistration.Dispose();
            _ = ExecuteAsync(item.Work);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExecuteAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        finally
        {
            lock (sync)
            {
                runningCount--;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            Pump();
        }
    }

    private sealed class WorkItem(Func<Task> work, CancellationToken cancellationToken)
    {
        public Func<Task> Work { get; } = work;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }
}
