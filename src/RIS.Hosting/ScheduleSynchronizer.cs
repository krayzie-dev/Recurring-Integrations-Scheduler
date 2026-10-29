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
/// The pause state belongs to the configuration (one pause state): a paused job's trigger is paused, a resumed job's
/// trigger resumed, without rescheduling.
/// </summary>
public sealed class ScheduleSynchronizer(
    ISchedulerFactory schedulerFactory,
    DbConfigurationStore store,
    ConfigurationRepository repository,
    PersistenceOptions persistence,
    ILogger<ScheduleSynchronizer> logger) : IHostedLifecycleService
{
    /// <summary>Marker of the one-time adoption of pauses kept only by the scheduler (before 5.0.0-local.30).</summary>
    public const string PauseAdoptionMarker = "pause-state.version";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    public SyncResult? LastResult { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await AdoptSchedulerPausesAsync(cancellationToken);
        store.Reloaded += OnReloaded;
        await SynchronizeAsync(cancellationToken);
    }

    /// <summary>
    /// Once, after the upgrade to one pause state: until then the list's pause/resume and PauseOnError changed only the trigger,
    /// so the scheduler knows what really runs. Its state is written to the configuration (audited as "RIS (migration)"),
    /// e.g. a job imported from 4.x as paused and resumed from the list stays resumed. A 4.x "never run" (IndefinitePause)
    /// stays paused.
    /// </summary>
    private async Task AdoptSchedulerPausesAsync(CancellationToken cancellationToken)
    {
        var marker = Path.Combine(persistence.DataDirectory, PauseAdoptionMarker);
        if (File.Exists(marker)) return;

        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        var changes = new List<JobChange>();
        foreach (var job in store.Current.Jobs)
        {
            var key = new JobKey(job.Name, job.Group);
            if (!await scheduler.Exists(key, cancellationToken)) continue;

            PauseInfo? pause = null;
            var triggerPaused = false;
            foreach (var trigger in await scheduler.GetTriggersOfJob(key, cancellationToken))
            {
                if (await scheduler.GetTriggerState(trigger.Key, cancellationToken) != TriggerState.Paused) continue;
                triggerPaused = true;
                pause ??= await scheduler.GetTriggerPause(trigger.Key, cancellationToken);
            }

            var configPaused = job.Schedule.Paused;
            if (triggerPaused == configPaused || (configPaused && job.Schedule.PauseReason == PauseReasons.IndefinitePause)) continue;

            var changed = Clone(job);
            changed.Schedule.SetPaused(triggerPaused, pause?.Reason ?? PauseReasons.Operator, pause?.RequestedBy, pause?.PausedAtUtc);
            changes.Add(new JobChange(job.Group, job.Name, changed));
            logger.LogInformation("Job {JobKey}: pause state taken from the scheduler into the configuration ({State})",
                job.Key, triggerPaused ? "paused" : "running");
        }

        if (changes.Count > 0)
        {
            await repository.SaveJobsAsync(changes, "RIS (migration)", cancellationToken);
            await store.RefreshAsync(cancellationToken);
        }

        Directory.CreateDirectory(persistence.DataDirectory);
        await File.WriteAllTextAsync(marker, "1", cancellationToken);
    }

    private static JobDefinition Clone(JobDefinition job) =>
        System.Text.Json.JsonSerializer.Deserialize<JobDefinition>(System.Text.Json.JsonSerializer.Serialize(job, RisJson.Options), RisJson.Options)!;

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

    /// <summary>
    /// Changes arriving while a synchronization runs are coalesced into one more pass (a bulk edit or an import raises
    /// one event, but several users or "ris db import" can change things at the same time).
    /// </summary>
    private void OnReloaded()
    {
        Interlocked.Exchange(ref _requested, 1);
        if (Interlocked.CompareExchange(ref _working, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _requested, 0) == 1)
                {
                    await SynchronizeSafelyAsync();
                }
            }
            finally
            {
                Volatile.Write(ref _working, 0);
                if (Volatile.Read(ref _requested) == 1 && !_stopping.IsCancellationRequested) OnReloaded();
            }
        });
    }

    private int _requested;
    private int _working;

    private async Task SynchronizeSafelyAsync()
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
    }

    /// <summary>Definition hashes applied to the scheduler; read from Quartz once (first pass), then kept up to date here.</summary>
    private Dictionary<JobKey, string>? _applied;

    public async Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
            var configuration = store.Current;
            int added = 0, updated = 0, removed = 0, unchanged = 0;

            if (_applied is null)
            {
                // First pass after start: the persistent store still has the jobs of the previous run.
                _applied = [];
                foreach (var key in await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), cancellationToken))
                {
                    if (await scheduler.GetJobDetail(key, cancellationToken) is { } detail && detail.JobType.Type == typeof(RisQuartzJob))
                    {
                        _applied[key] = detail.JobDataMap.GetString(RisJobScheduling.DefinitionHashKey) ?? "";
                    }
                }
            }

            foreach (var definition in configuration.Jobs)
            {
                var key = new JobKey(definition.Name, definition.Group);
                var hash = ConfigurationRepository.ComputeHash(definition);
                var exists = _applied.TryGetValue(key, out var appliedHash);
                if (exists && appliedHash == hash)
                {
                    unchanged++;
                    await ApplyPauseAsync(scheduler, definition, key, cancellationToken);
                    continue;
                }

                await RisJobScheduling.ScheduleAsync(scheduler, definition, cancellationToken, hash);
                _applied[key] = hash;
                if (!exists)
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
            foreach (var key in _applied.Keys.Where(k => !configured.Contains(k)).ToList())
            {
                await scheduler.DeleteJob(key, cancellationToken);
                _applied.Remove(key);
                removed++;
                logger.LogInformation("Job {JobKey} removed from the scheduler (no longer configured)", $"{key.Group}.{key.Name}");
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

    /// <summary>The trigger follows the pause state of the configuration (pause or resume, never a reschedule).</summary>
    private async Task ApplyPauseAsync(IScheduler scheduler, JobDefinition definition, JobKey key, CancellationToken cancellationToken)
    {
        var paused = await IsPausedAsync(scheduler, key, cancellationToken);
        if (paused == definition.Schedule.Paused) return;
        if (definition.Schedule.Paused)
        {
            await scheduler.PauseJobWith(key, new PauseDetails
            {
                Reason = definition.Schedule.PauseReason ?? PauseReasons.Configuration,
                RequestedBy = definition.Schedule.PausedBy
            }, cancellationToken);
            logger.LogInformation("Job {JobKey} paused (configuration)", definition.Key);
        }
        else
        {
            await scheduler.ResumeJob(key, cancellationToken);
            logger.LogInformation("Job {JobKey} resumed (configuration)", definition.Key);
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
                await store.RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Checking the database for configuration changes failed: {Error}", ex.Message);
            }
        }
    }
}
