using System.Collections.Concurrent;

namespace HyperVControl.Services;

internal enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>Progress reported by a job; any field may be null when the job cannot measure it.</summary>
internal sealed record JobProgress(double? Percent = null, long? BytesDone = null, long? BytesTotal = null, string? Message = null);

/// <summary>Snapshot of one background job, serialized as the <c>hyperv_jobs</c> result.</summary>
internal sealed record JobInfo(string Id, string Name, JobStatus Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    JobProgress Progress, object? Result, string? Error, string? ErrorType);

/// <summary>
/// In-process background jobs for work that outlives one tool call (downloads, extraction, export,
/// import, clones, image builds). Jobs belong to this MCP process: a job's work receives a
/// cancellation token, and cancellation is the only way to stop it.
/// </summary>
internal sealed class JobRunner : IDisposable
{
    private sealed class Job(string id, string name)
    {
        public readonly string Id = id;
        public readonly string Name = name;
        public readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
        public readonly CancellationTokenSource Cancellation = new();
        public volatile JobStatus Status = JobStatus.Queued;
        public DateTimeOffset? FinishedAt;
        public JobProgress Progress = new();
        public object? Result;
        public string? Error;
        public string? ErrorType;
        public Task Completion = Task.CompletedTask;

        public JobInfo Snapshot() => new(Id, Name, Status, StartedAt, FinishedAt, Progress, Result, Error, ErrorType);
    }

    private sealed class Reporter(Job job) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => job.Progress = value;
    }

    private readonly ConcurrentDictionary<string, Job> jobs = new();

    /// <summary>Starts <paramref name="work"/> on the thread pool and returns immediately.</summary>
    public JobInfo Start(string name, Func<IProgress<JobProgress>, CancellationToken, Task<object?>> work)
    {
        var job = new Job(Guid.NewGuid().ToString("N")[..12], name);
        jobs[job.Id] = job;
        job.Completion = Task.Run(async () =>
        {
            job.Status = JobStatus.Running;
            try
            {
                job.Result = await work(new Reporter(job), job.Cancellation.Token);
                job.Status = JobStatus.Succeeded;
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
                job.Status = JobStatus.Cancelled; job.Error = "The job was cancelled."; job.ErrorType = "Cancelled";
            }
            catch (HyperVOperationException ex)
            {
                job.Status = JobStatus.Failed; job.Error = ex.Message; job.ErrorType = ex.ErrorType;
            }
            catch (Exception ex)
            {
                job.Status = JobStatus.Failed; job.Error = ex.Message; job.ErrorType = ex.GetType().Name;
            }
            finally { job.FinishedAt = DateTimeOffset.UtcNow; }
        });
        return job.Snapshot();
    }

    public JobInfo? Get(string id) => jobs.TryGetValue(id, out var job) ? job.Snapshot() : null;

    public IReadOnlyList<JobInfo> List() => jobs.Values.OrderByDescending(j => j.StartedAt).Select(j => j.Snapshot()).ToList();

    public JobInfo Cancel(string id)
    {
        var job = jobs.TryGetValue(id, out var found) ? found : throw new ArgumentException($"Unknown job '{id}'.");
        if (job.Status is JobStatus.Queued or JobStatus.Running) job.Cancellation.Cancel();
        return job.Snapshot();
    }

    /// <summary>Waits up to <paramref name="timeout"/> for completion and returns the latest snapshot either way.</summary>
    public async Task<JobInfo> WaitAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var job = jobs.TryGetValue(id, out var found) ? found : throw new ArgumentException($"Unknown job '{id}'.");
        await Task.WhenAny(job.Completion, Task.Delay(timeout, ct));
        ct.ThrowIfCancellationRequested();
        return job.Snapshot();
    }

    /// <summary>Starts a job and, when <paramref name="waitSeconds"/> is positive, returns its state after that wait.</summary>
    public async Task<JobInfo> StartAndWaitAsync(string name, Func<IProgress<JobProgress>, CancellationToken, Task<object?>> work, int waitSeconds, CancellationToken ct)
    {
        var started = Start(name, work);
        return waitSeconds > 0 ? await WaitAsync(started.Id, TimeSpan.FromSeconds(Math.Min(waitSeconds, 3600)), ct) : started;
    }

    public void Dispose()
    {
        foreach (var job in jobs.Values) { job.Cancellation.Cancel(); job.Cancellation.Dispose(); }
    }
}
