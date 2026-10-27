using Quartz;
using RIS.Core.Errors;
using RIS.Jobs.Quartz;
using RIS.Persistence;

namespace RIS.Server.Services;

/// <summary>Job reference "Group.Name" split into parts (the name may contain dots, the group may not).</summary>
public sealed record JobRef(string Group, string Name)
{
    public string Key => $"{Group}.{Name}";

    public static JobRef Parse(string key)
    {
        var separator = key.IndexOf('.');
        return separator > 0 ? new JobRef(key[..separator], key[(separator + 1)..]) : new JobRef("Default", key);
    }

    public override string ToString() => Key;
}

/// <summary>Result of an operation on one job of a bulk request.</summary>
public sealed record BulkItemResult(string Job, bool Succeeded, string? Error = null);

/// <summary>Operational actions on one or many jobs. Each job is handled separately: one failure does not stop the rest.</summary>
public sealed class JobOperationsService(
    JobQueryService queries, ISchedulerFactory schedulers, ConfigurationRepository repository, ILogger<JobOperationsService> logger)
{
    public Task<IReadOnlyList<BulkItemResult>> RunAsync(IEnumerable<JobRef> jobs, string? user, CancellationToken ct = default) =>
        ForEachAsync(jobs, async (scheduler, job) =>
        {
            await RisJobScheduling.TriggerOnceAsync(scheduler, queries.Find(job.Group, job.Name)!, ct);
            logger.LogInformation("Job {JobKey} started manually by {User}", job.Key, user);
        }, ct);

    public Task<IReadOnlyList<BulkItemResult>> PauseAsync(IEnumerable<JobRef> jobs, string? reason, string? user, CancellationToken ct = default)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "Paused by operator" : reason.Trim();
        return ForEachAsync(jobs, async (scheduler, job) =>
        {
            await scheduler.PauseJobWith(new JobKey(job.Name, job.Group), new PauseDetails { Reason = reason, RequestedBy = user }, ct);
            logger.LogInformation("Job {JobKey} paused by {User}: {Reason}", job.Key, user, reason);
        }, ct);
    }

    public Task<IReadOnlyList<BulkItemResult>> ResumeAsync(IEnumerable<JobRef> jobs, string? user, CancellationToken ct = default) =>
        ForEachAsync(jobs, async (scheduler, job) =>
        {
            await scheduler.ResumeJob(new JobKey(job.Name, job.Group), ct);
            logger.LogInformation("Job {JobKey} resumed by {User}", job.Key, user);
        }, ct);

    /// <summary>Deletes job definitions one by one (each validated: e.g. a job still referenced as a pair stays).</summary>
    public async Task<IReadOnlyList<BulkItemResult>> DeleteAsync(IEnumerable<JobRef> jobs, string? user, CancellationToken ct = default)
    {
        var results = new List<BulkItemResult>();
        foreach (var job in jobs)
        {
            try
            {
                await repository.DeleteJobAsync(job.Group, job.Name, user, ct);
                results.Add(new BulkItemResult(job.Key, true));
            }
            catch (RisException ex)
            {
                results.Add(new BulkItemResult(job.Key, false, ex.Message));
            }
        }
        return results;
    }

    private async Task<IReadOnlyList<BulkItemResult>> ForEachAsync(IEnumerable<JobRef> jobs, Func<IScheduler, JobRef, Task> action, CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        var results = new List<BulkItemResult>();
        foreach (var job in jobs)
        {
            if (queries.Find(job.Group, job.Name) is null)
            {
                results.Add(new BulkItemResult(job.Key, false, $"Job '{job.Key}' is not configured."));
                continue;
            }
            try
            {
                await action(scheduler, job);
                results.Add(new BulkItemResult(job.Key, true));
            }
            catch (Exception ex) when (ex is SchedulerException or RisException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Operation on job {JobKey} failed: {Error}", job.Key, ex.Message);
                results.Add(new BulkItemResult(job.Key, false, ex.Message));
            }
        }
        return results;
    }
}
