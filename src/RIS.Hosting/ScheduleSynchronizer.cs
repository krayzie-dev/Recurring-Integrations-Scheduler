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
    ILogger<ScheduleSynchronizer> logger) : IHostedLifecycleService
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    public SyncResult? LastResult { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        store.Reloaded += OnReloaded;
        await SynchronizeAsync(cancellationToken);
    }

    /// <summary>Before the scheduler shuts down: no new synchronizations, and one in progress is finished first.</summary>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        store.Reloaded -= OnReloaded;
        await _stopping.CancelAsync();
        await _lock.WaitAsync(cancellationToken);
        _lock.Release();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void OnReloaded() => _ = Task.Run(async () =>
    {
        try
        {
            await SynchronizeAsync(_stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Service is stopping; the next start synchronizes again.
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
/// Runs in the "stopping" phase, which precedes every StopAsync - the host may stop services concurrently
/// (e.g. StopAsync while RunAsync is pending), so StopAsync order alone does not guarantee the scheduler is still running.
/// </summary>
public sealed class RunningJobsInterrupter(ISchedulerFactory schedulerFactory, RunningJobs runningJobs, ILogger<RunningJobsInterrupter> logger) : IHostedLifecycleService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        if (runningJobs.Current.Count == 0) return;
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        foreach (var (runId, key, started) in runningJobs.Current)
        {
            logger.LogWarning("Interrupting job {JobKey} (run {RunId}, running since {StartedUtc:u}) because the service is stopping",
                $"{key.Group}.{key.Name}", runId, started);
            await scheduler.InterruptFireInstance(runId, cancellationToken);
        }
    }
}

/// <summary>
/// Reloads configuration when another process (ris db import / set-secret) changed the database.
/// Changes made through this server are applied immediately by <see cref="DbConfigurationStore"/>; this is the fallback.
/// </summary>
public sealed class ConfigurationChangeWatcher(ConfigurationRepository repository, DbConfigurationStore store, RisHostOptions options,
    ILogger<ConfigurationChangeWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var version = await repository.GetVersionAsync(stoppingToken);
        using var timer = new PeriodicTimer(options.ConfigurationPollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var current = await repository.GetVersionAsync(stoppingToken);
                if (current == version) continue;

                logger.LogInformation("Configuration changed in the database (version {Previous} -> {Current}); reloading", version, current);
                version = current;
                store.Refresh();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Checking the database for configuration changes failed: {Error}", ex.Message);
            }
        }
    }
}
