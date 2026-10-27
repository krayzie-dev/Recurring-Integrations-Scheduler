using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

using RIS.Core.Configuration;
using RIS.Jobs.Quartz;
using RIS.Persistence;

namespace RIS.Hosting;

/// <summary>Result of one synchronization of configuration with the scheduler.</summary>
public sealed record SyncResult(int Added, int Updated, int Removed, int Unchanged)
{
    public override string ToString() => $"{Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged";
}

/// <summary>
/// Keeps Quartz jobs in line with configured job definitions. A job is (re)created only when its definition changed,
/// so a restart keeps triggers as they are and Quartz can detect runs missed while the service was down (MissedRuns policy).
/// </summary>
public sealed class ScheduleSynchronizer(
    ISchedulerFactory schedulerFactory,
    DbConfigurationStore store,
    ILogger<ScheduleSynchronizer> logger) : IHostedService
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SyncResult? LastResult { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        store.Reloaded += OnReloaded;
        await SynchronizeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        store.Reloaded -= OnReloaded;
        return Task.CompletedTask;
    }

    private void OnReloaded() => _ = Task.Run(async () =>
    {
        try
        {
            await SynchronizeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{ErrorCode}] Applying changed configuration to the scheduler failed: {Error}", Core.Errors.ErrorCodes.Unexpected, ex.Message);
        }
    });

    public async Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
            var configuration = store.Current;
            int added = 0, updated = 0, removed = 0, unchanged = 0;

            foreach (var definition in configuration.Jobs)
            {
                var key = new JobKey(definition.Name, definition.Group);
                var hash = ConfigurationRepository.ComputeHash(definition);
                var existing = await scheduler.GetJobDetail(key, cancellationToken);
                if (existing is not null && existing.JobDataMap.GetString(RisJobScheduling.DefinitionHashKey) == hash)
                {
                    unchanged++;
                    continue;
                }

                var wasPaused = existing is not null && await IsPausedAsync(scheduler, key, cancellationToken);
                await RisJobScheduling.ScheduleAsync(scheduler, definition, cancellationToken, hash, keepPaused: wasPaused);
                if (existing is null)
                {
                    added++;
                    logger.LogInformation("Job {JobKey} added to the scheduler", definition.Key);
                }
                else
                {
                    updated++;
                    logger.LogInformation("Job {JobKey} changed in configuration and was rescheduled", definition.Key);
                }
            }

            var configured = configuration.Jobs.Select(j => new JobKey(j.Name, j.Group)).ToHashSet();
            foreach (var key in await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), cancellationToken))
            {
                var detail = await scheduler.GetJobDetail(key, cancellationToken);
                if (detail?.JobType.Type == typeof(RisQuartzJob) && !configured.Contains(key))
                {
                    await scheduler.DeleteJob(key, cancellationToken);
                    removed++;
                    logger.LogInformation("Job {JobKey} removed from the scheduler (no longer configured)", $"{key.Group}.{key.Name}");
                }
            }

            LastResult = new SyncResult(added, updated, removed, unchanged);
            logger.LogInformation("Scheduler synchronized with configuration: {SyncResult}", LastResult);
            return LastResult;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<bool> IsPausedAsync(IScheduler scheduler, JobKey key, CancellationToken cancellationToken)
    {
        foreach (var trigger in await scheduler.GetTriggersOfJob(key, cancellationToken))
        {
            if (await scheduler.GetTriggerState(trigger.Key, cancellationToken) == TriggerState.Paused)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// On service stop interrupts running jobs (they may wait for D365 for hours), so the service stops within seconds.
/// Registered after the Quartz hosted service, therefore stopped before it.
/// </summary>
public sealed class RunningJobsInterrupter(ISchedulerFactory schedulerFactory, RunningJobs runningJobs, ILogger<RunningJobsInterrupter> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        foreach (var (runId, key, started) in runningJobs.Current)
        {
            logger.LogWarning("Interrupting job {JobKey} (run {RunId}, running since {StartedUtc:u}) because the service is stopping",
                $"{key.Group}.{key.Name}", runId, started);
            await scheduler.InterruptFireInstance(runId, cancellationToken);
        }
    }
}
