using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Extensibility;
using RIS.Core.Configuration;
using RIS.Jobs.Quartz;
using RIS.Persistence;

namespace RIS.Hosting;

public enum IdleGuardCase
{
    /// <summary>Every run in the window failed.</summary>
    Failing,
    /// <summary>No run in the window did any work (only skipped runs and failures).</summary>
    NoWork
}

/// <summary>What the guard knows about one job when it checks it.</summary>
public sealed record IdleGuardInput(
    JobDefinition Job,
    DateTimeOffset? ChangedUtc,
    bool Running,
    JobRunResult? LastResult,
    DateTimeOffset? FirstFiredUtc,
    DateTimeOffset? LastFiredUtc,
    DateTimeOffset? LastNotFailedUtc,
    int ConsecutiveFailures,
    DateTimeOffset? LastWorkUtc);

/// <summary>A job the guard pauses (or would pause, in a preview), with the numbers behind the decision.</summary>
public sealed record IdleGuardFinding(string JobKey, IdleGuardCase Case, int PlannedRuns, int ConsecutiveFailures,
    DateTimeOffset? LastWorkUtc, DateTimeOffset? LastNotFailedUtc, string Details)
{
    public string Reason => Case == IdleGuardCase.Failing ? PauseReasons.IdleFailing : PauseReasons.IdleNoWork;
}

public sealed record IdleGuardResult(DateTimeOffset CheckedAtUtc, bool Applied, int JobsChecked, IReadOnlyList<IdleGuardFinding> Findings,
    IReadOnlyList<string> Errors);

/// <summary>
/// The rule of the idle guard (pure, no I/O). A job is paused only when all of these hold:
/// <list type="number">
/// <item>it is running (not paused), not excluded (NeverAutoPause) and not running right now;</item>
/// <item>its schedule has at least <see cref="IdleGuardSettings.MinPlannedRuns"/> runs in the window, so the window is many
/// times its rhythm (a weekly or daily job is never paused);</item>
/// <item>it was not created, changed or resumed in the window and it has been firing since before the window;</item>
/// <item>failing: its last result is a failure, nothing but failures since the window started, and at least
/// max(3, planned / 4) failures in a row (proof that it really ran); or no work: no run with real work (Succeeded) since
/// the window started, while RIS recorded work for the whole window, and it fired in the window.</item>
/// </list>
/// </summary>
public static class IdleGuardRule
{
    public const int MaxCountedRuns = 100_000;

    public static IdleGuardFinding? Evaluate(IdleGuardInput input, IdleGuardSettings settings, DateTimeOffset nowUtc, DateTimeOffset trackingSinceUtc)
    {
        if (!settings.Enabled) return null;
        var job = input.Job;
        if (job.Schedule.Paused || job.Options?.NeverAutoPause == true || input.Running) return null;

        var windowStart = nowUtc.AddDays(-settings.WindowDays);
        if (input.ChangedUtc is { } changed && changed > windowStart) return null;
        if (input.FirstFiredUtc is not { } first || first > windowStart) return null;

        var planned = PlannedRuns(job, windowStart, nowUtc);
        if (planned < settings.MinPlannedRuns) return null;

        var noNonFailedRun = input.LastNotFailedUtc is null || input.LastNotFailedUtc < windowStart;
        var failing = input.LastResult == JobRunResult.Failed && noNonFailedRun && input.ConsecutiveFailures >= Math.Max(3, planned / 4);
        if (failing && settings.PauseFailing)
        {
            return new IdleGuardFinding(job.Key, IdleGuardCase.Failing, planned, input.ConsecutiveFailures, input.LastWorkUtc, input.LastNotFailedUtc,
                $"{input.ConsecutiveFailures} failed runs in a row, no other result since {Day(input.LastNotFailedUtc)}; {planned} planned runs in {settings.WindowDays} days");
        }

        var noWork = (input.LastWorkUtc is null || input.LastWorkUtc < windowStart) && trackingSinceUtc <= windowStart
                     && input.LastFiredUtc is { } last && last >= windowStart;
        if (noWork && settings.PauseIdle)
        {
            return new IdleGuardFinding(job.Key, IdleGuardCase.NoWork, planned, input.ConsecutiveFailures, input.LastWorkUtc, input.LastNotFailedUtc,
                $"no run with work done since {Day(input.LastWorkUtc)}; {planned} planned runs in {settings.WindowDays} days, last result {input.LastResult}");
        }

        return null;
    }

    /// <summary>Planned runs of the job's schedule between the two times (windows of hours and days included).</summary>
    public static int PlannedRuns(JobDefinition job, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var trigger = RisJobScheduling.BuildTrigger(job, fromUtc);
        var count = 0;
        var next = trigger.GetFireTimeAfter(fromUtc.AddTicks(-1));
        while (next is { } time && time < toUtc && count < MaxCountedRuns)
        {
            count++;
            next = trigger.GetFireTimeAfter(time);
        }
        return count;
    }

    private static string Day(DateTimeOffset? value) => value is { } v ? v.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture) : "ever";
}

/// <summary>
/// Runs the idle guard once a day at <see cref="IdleGuardSettings.CheckAt"/> (server time) when it is switched on, and on
/// request (preview without pausing, or check now). Pauses through the configuration (one pause state, audited as "RIS").
/// </summary>
public sealed class IdleGuardService(
    SettingsRepository settings,
    ConfigurationRepository repository,
    DbConfigurationStore store,
    ISchedulerFactory schedulerFactory,
    IExecutionHistoryStore history,
    RunningJobs running,
    TimeProvider time,
    ILogger<IdleGuardService> logger) : BackgroundService
{
    public const string LastResultKey = "IdleGuard.LastResult";
    private readonly SemaphoreSlim _lock = new(1, 1);

    public IdleGuardResult? LastResult { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await settings.EnsureActivityTrackingAsync(stoppingToken);
        LastResult = await LoadLastResultAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var current = await settings.GetAsync<IdleGuardSettings>(IdleGuardSettings.Key, stoppingToken);
                var local = time.GetLocalNow();
                var due = new DateTimeOffset(local.Date + current.CheckAt.ToTimeSpan(), local.Offset);
                var lastLocal = LastResult is { Applied: true } last ? TimeZoneInfo.ConvertTime(last.CheckedAtUtc, TimeZoneInfo.Local) : (DateTimeOffset?)null;
                if (current.Enabled && local >= due && (lastLocal is null || lastLocal.Value.Date < local.Date))
                {
                    await RunAsync(apply: true, "RIS", stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{ErrorCode}] Idle guard failed: {Error}", Core.Errors.ErrorCodes.Unexpected, ex.Message);
            }
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
        }
    }

    /// <summary>Checks all jobs; with <paramref name="apply"/> pauses the ones found, otherwise only reports them (preview).</summary>
    public async Task<IdleGuardResult> RunAsync(bool apply, string? user, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var current = await settings.GetAsync<IdleGuardSettings>(IdleGuardSettings.Key, cancellationToken);
            // A preview with the guard off still shows what both checks would pause.
            var rule = apply || current.Enabled ? current : new IdleGuardSettings
            {
                PauseFailing = true, PauseIdle = true, WindowDays = current.WindowDays, MinPlannedRuns = current.MinPlannedRuns, CheckAt = current.CheckAt
            };
            var now = time.GetUtcNow();
            var trackingSince = await settings.EnsureActivityTrackingAsync(cancellationToken);
            var changes = await settings.GetJobChangesAsync(cancellationToken);
            var work = await settings.GetLastWorkAsync(cancellationToken);
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
            var runningKeys = running.Current.Select(r => $"{r.Key.Group}.{r.Key.Name}").ToHashSet(StringComparer.OrdinalIgnoreCase);

            var findings = new List<IdleGuardFinding>();
            var errors = new List<string>();
            var jobs = store.Current.Jobs.ToList();
            foreach (var job in jobs)
            {
                var status = await history.GetJobRunStatus(scheduler.SchedulerName, new JobKey(job.Name, job.Group), cancellationToken);
                var input = new IdleGuardInput(job, changes.TryGetValue(job.Key, out var changed) ? changed : null, runningKeys.Contains(job.Key),
                    status?.LastResult, status?.FirstFiredAtUtc, status?.LastFiredAtUtc, status?.LastSucceededAtUtc, status?.ConsecutiveFailures ?? 0,
                    work.TryGetValue(job.Key, out var lastWork) ? lastWork : null);
                if (IdleGuardRule.Evaluate(input, rule, now, trackingSince) is { } finding) findings.Add(finding);
            }

            if (apply)
            {
                foreach (var finding in findings)
                {
                    try
                    {
                        var separator = finding.JobKey.IndexOf('.');
                        await repository.SetPausedAsync([(finding.JobKey[..separator], finding.JobKey[(separator + 1)..])], true, finding.Reason, "RIS", cancellationToken);
                        using (logger.BeginScope(new Dictionary<string, object> { [RisEventIds.EventLogProperty] = (int)RisEventIds.JobPausedByIdleGuard }))
                        {
                            logger.LogWarning("Job {JobKey} was paused by the idle guard ({Case}): {Details}", finding.JobKey, finding.Case, finding.Details);
                        }
                    }
                    catch (Exception ex) when (ex is Core.Errors.RisException or InvalidOperationException)
                    {
                        errors.Add($"{finding.JobKey}: {ex.Message}");
                        logger.LogError(ex, "Idle guard could not pause job {JobKey}: {Error}", finding.JobKey, ex.Message);
                    }
                }
            }

            var result = new IdleGuardResult(now, apply, jobs.Count, findings, errors);
            logger.LogInformation("Idle guard {Mode}: {Checked} job(s) checked, {Found} {Verb}", apply ? "check" : "preview", jobs.Count, findings.Count,
                apply ? "paused" : "would be paused");
            if (apply)
            {
                LastResult = result;
                await SaveLastResultAsync(result, cancellationToken);
            }
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IdleGuardResult?> LoadLastResultAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetAsync<StoredResult>(LastResultKey, cancellationToken);
        return stored.Result;
    }

    private Task SaveLastResultAsync(IdleGuardResult result, CancellationToken cancellationToken) =>
        settings.SaveRuntimeAsync(LastResultKey, new StoredResult { Result = result }, cancellationToken);

    private sealed class StoredResult
    {
        public IdleGuardResult? Result { get; set; }
    }
}
