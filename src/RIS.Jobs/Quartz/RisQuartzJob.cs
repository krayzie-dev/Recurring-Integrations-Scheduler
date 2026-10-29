using Microsoft.Extensions.Logging;
using Quartz;
using RIS.Core.Configuration;
using RIS.Core.Options;

namespace RIS.Jobs.Quartz;

/// <summary>
/// Job input stored by Quartz. The envelope keeps the polymorphic "type" of options in the serialized JSON.
/// Contains no secrets: connections and SFTP servers are referenced by name.
/// </summary>
public sealed class RisJobInput
{
    public JobOptionsBase? Options { get; set; }
}

/// <summary>
/// Quartz job running any RIS job type through <see cref="JobPipeline"/>.
/// Result is stored as <see cref="JobRunReport"/> in Quartz execution history.
/// </summary>
public sealed class RisQuartzJob(JobPipeline pipeline, RunningJobs runningJobs, ILogger<RisQuartzJob> logger,
    IJobPauseRecorder? pauseRecorder = null) : IJob<RisJobInput>
{
    public async ValueTask Execute(IJobExecutionContext context, RisJobInput input, CancellationToken cancellationToken)
    {
        using var registration = runningJobs.Register(context.JobDetail.Key, context.FireInstanceId);
        await ExecuteCoreAsync(context, input, cancellationToken);
    }

    private async ValueTask ExecuteCoreAsync(IJobExecutionContext context, RisJobInput input, CancellationToken cancellationToken)
    {
        var key = context.JobDetail.Key;
        var jobKey = $"{key.Group}.{key.Name}";
        var options = input.Options ?? throw new JobExecutionException($"Job {jobKey} has no options.");

        var outcome = await pipeline.RunAsync(jobKey, options, context.FireInstanceId, context.ReportProgress, cancellationToken);

        var report = outcome.Status switch
        {
            JobOutcomeStatus.Succeeded => JobRunReport.Succeeded(outcome.Summary),
            JobOutcomeStatus.Skipped => JobRunReport.Skipped(outcome.Summary),
            _ => JobRunReport.Failed(outcome.Summary)
        };
        foreach (var (name, value) in outcome.Metrics)
        {
            report = report.With(name, value);
        }
        context.Result = report;

        if (outcome.Status == JobOutcomeStatus.Failed && options.PauseOnError)
        {
            await context.Scheduler.PauseJobWith(key, new PauseDetails { Reason = PauseReasons.Error, RequestedBy = "RIS" }, cancellationToken);
            // The pause belongs to the job configuration (one pause state): a later change of the job keeps it, the audit shows it.
            if (pauseRecorder is not null)
            {
                try
                {
                    await pauseRecorder.RecordPauseAsync(key.Group, key.Name, PauseReasons.Error, "RIS", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Pause of job {JobKey} after a failed run was not saved in the configuration: {Error}", jobKey, ex.Message);
                }
            }
            logger.LogWarning("Job {JobKey} was paused because it failed and PauseOnError is set", jobKey);
        }
    }
}

/// <summary>Saves a pause set by RIS itself (PauseOnError) in the job configuration.</summary>
public interface IJobPauseRecorder
{
    Task RecordPauseAsync(string group, string name, string reason, string by, CancellationToken cancellationToken);
}

/// <summary>
/// Jobs currently running in this process. Quartz 4 has no API listing executing jobs, and its shutdown does not cancel
/// them, so the service uses this registry to interrupt running jobs on stop.
/// </summary>
public sealed class RunningJobs
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (JobKey Key, DateTimeOffset StartedUtc)> _running = new();

    /// <summary>Raised when a job starts or finishes (UI live refresh). The history row is written shortly after "finished".</summary>
    public event Action? Changed;

    public IReadOnlyCollection<(string RunId, JobKey Key, DateTimeOffset StartedUtc)> Current =>
        _running.Select(r => (r.Key, r.Value.Key, r.Value.StartedUtc)).ToList();

    public IDisposable Register(JobKey key, string runId)
    {
        _running[runId] = (key, DateTimeOffset.UtcNow);
        Changed?.Invoke();
        return new Registration(() =>
        {
            _running.TryRemove(runId, out _);
            Changed?.Invoke();
        });
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>
/// Creates Quartz jobs and triggers from RIS job definitions.
/// </summary>
public static class RisJobScheduling
{
    /// <summary>JobDataMap key with hash of the definition the Quartz job was created from.</summary>
    public const string DefinitionHashKey = "ris.definitionHash";

    public static IJobDetail BuildJob(JobDefinition definition, string? definitionHash = null)
    {
        var builder = JobBuilder.Create<RisQuartzJob>()
            .WithIdentity(definition.Name, definition.Group)
            .WithDescription(definition.Description)
            .DisallowConcurrentExecution()
            .StoreDurably()
            .UsingInput(new RisJobInput { Options = definition.Options });
        if (definitionHash is not null)
        {
            builder = builder.UsingJobData(DefinitionHashKey, definitionHash);
        }
        return builder.Build();
    }

    /// <summary>
    /// Builds the trigger. The rhythm comes only from configuration (cron, or StartAt + Interval, in the time zone),
    /// so (re)creating the trigger at service start never runs the job immediately and never shifts its times
    /// (4.x lost the start time and restarted the rhythm at the moment of restart).
    /// </summary>
    public static ITrigger BuildTrigger(JobDefinition definition, DateTimeOffset now)
    {
        var schedule = definition.Schedule;
        var timeZone = string.IsNullOrWhiteSpace(schedule.TimeZone) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone);
        var runMissedOnce = schedule.MissedRuns == MissedRunPolicy.RunOnce;

        var builder = TriggerBuilder.Create()
            .WithIdentity(definition.Name + " trigger", definition.Group)
            .ForJob(definition.Name, definition.Group);

        if (!string.IsNullOrWhiteSpace(schedule.Cron))
        {
            return builder.WithCronSchedule(schedule.Cron, cron => cron
                    .InTimeZone(timeZone)
                    .WithMisfireInstruction(runMissedOnce ? CronTriggerMisfireInstruction.FireAndProceed : CronTriggerMisfireInstruction.DoNothing))
                .StartAt(now.ToUniversalTime())
                .Build();
        }

        var interval = schedule.Interval ?? throw new InvalidOperationException($"Job {definition.Key} has neither cron nor interval.");
        var anchor = schedule.StartAt ?? throw new InvalidOperationException($"Job {definition.Key}: StartAt is required for interval schedules.");
        var (count, unit) = ToIntervalUnit(interval);
        var anchorTimeOfDay = TimeZoneInfo.ConvertTime(anchor, timeZone).TimeOfDay;

        if (schedule.HasWindow)
        {
            // Daily window and/or days of the week, e.g. 08:00–16:00 every 10 min on workdays: the rhythm starts at the
            // window start every day (without a window: at the first time of the day of the rhythm); the end is included.
            var from = schedule.ActiveFrom ?? TimeOnly.FromTimeSpan(TimeSpan.FromTicks(anchorTimeOfDay.Ticks % Math.Min(interval.Ticks, TimeSpan.TicksPerDay)));
            var to = schedule.ActiveTo ?? new TimeOnly(23, 59, 59);
            var days = schedule.Days is { Count: > 0 } selected ? selected.Distinct().ToArray() : Enum.GetValues<DayOfWeek>();
            return builder.WithDailyTimeIntervalSchedule(daily => daily
                    .OnDaysOfTheWeek(days)
                    .StartingDailyAt(from)
                    .EndingDailyAt(to)
                    .WithInterval(count, unit)
                    .InTimeZone(timeZone)
                    .WithMisfireInstruction(runMissedOnce ? DailyTimeIntervalTriggerMisfireInstruction.FireAndProceed : DailyTimeIntervalTriggerMisfireInstruction.DoNothing))
                .StartAt(now.ToUniversalTime())
                .Build();
        }

        if (IntervalAsCron(interval, anchorTimeOfDay) is { } cronExpression)
        {
            // Most intervals (5/15/30 min, 1/2/3/4/6/8/12 h): equivalent cron in the time zone keeps wall-clock times,
            // also on days when daylight saving time changes. 12:00 every 2 h = "0 0 0/2 * * ?" = 00:00, 02:00, ... 22:00.
            return builder.WithCronSchedule(cronExpression, cron => cron
                    .InTimeZone(timeZone)
                    .WithMisfireInstruction(runMissedOnce ? CronTriggerMisfireInstruction.FireAndProceed : CronTriggerMisfireInstruction.DoNothing))
                .StartAt(now.ToUniversalTime())
                .Build();
        }

        if (TimeSpan.TicksPerDay % interval.Ticks == 0)
        {
            // Other intervals dividing a day (e.g. 90 min): same times every day in the time zone.
            // On the day of a DST change the times after the change can be shifted by one hour until midnight.
            var firstOfDay = TimeSpan.FromTicks(anchorTimeOfDay.Ticks % interval.Ticks);
            return builder.WithDailyTimeIntervalSchedule(daily => daily
                    .OnEveryDay()
                    .StartingDailyAt(TimeOnly.FromTimeSpan(firstOfDay))
                    .WithInterval(count, unit)
                    .InTimeZone(timeZone)
                    .WithMisfireInstruction(runMissedOnce ? DailyTimeIntervalTriggerMisfireInstruction.FireAndProceed : DailyTimeIntervalTriggerMisfireInstruction.DoNothing))
                .StartAt(now.ToUniversalTime())
                .Build();
        }

        // Other intervals (e.g. 7 h): continue the rhythm from StartAt - first run is the next slot not before now.
        return builder.WithCalendarIntervalSchedule(calendar => calendar
                .WithInterval(count, unit)
                .InTimeZone(timeZone)
                .WithMisfireInstruction(runMissedOnce ? CalendarIntervalTriggerMisfireInstruction.FireAndProceed : CalendarIntervalTriggerMisfireInstruction.DoNothing))
            .StartAt(NextSlot(anchor, interval, now).ToUniversalTime())
            .Build();
    }

    /// <summary>
    /// Cron expression equivalent to "every <paramref name="interval"/> aligned to <paramref name="anchorTimeOfDay"/>",
    /// or null when cron cannot express it (interval not dividing an hour / a day, or seconds in anchor or interval).
    /// </summary>
    public static string? IntervalAsCron(TimeSpan interval, TimeSpan anchorTimeOfDay)
    {
        if (interval.Ticks % TimeSpan.TicksPerMinute != 0 || anchorTimeOfDay.Seconds != 0 || anchorTimeOfDay.Milliseconds != 0)
        {
            return null;
        }

        var minutes = (int)interval.TotalMinutes;
        if (minutes <= 60 && 60 % minutes == 0)
        {
            var firstMinute = anchorTimeOfDay.Minutes % minutes;
            return minutes == 60 ? $"0 {firstMinute} * * * ?" : $"0 {firstMinute}/{minutes} * * * ?";
        }

        if (minutes % 60 == 0 && 24 % (minutes / 60) == 0)
        {
            var hours = minutes / 60;
            return $"0 {anchorTimeOfDay.Minutes} {anchorTimeOfDay.Hours % hours}/{hours} * * ?";
        }

        return null;
    }

    /// <summary>Runs the job once now (manual run), without touching its regular schedule.</summary>
    public static async Task TriggerOnceAsync(IScheduler scheduler, JobDefinition definition, CancellationToken cancellationToken)
    {
        var jobKey = new JobKey(definition.Name, definition.Group);
        if (await scheduler.Exists(jobKey, cancellationToken))
        {
            await scheduler.TriggerJob(jobKey, new JobDataMap(), cancellationToken);
            return;
        }

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{definition.Name} manual {Guid.NewGuid():N}", definition.Group)
            .ForJob(definition.Name, definition.Group)
            .StartNow()
            .Build();
        await scheduler.ScheduleJob(BuildJob(definition), [trigger], new ScheduleJobOptions { Replace = true }, cancellationToken);
    }

    /// <summary>First time anchor + k * interval (k ≥ 0) that is not before <paramref name="now"/>.</summary>
    public static DateTimeOffset NextSlot(DateTimeOffset anchor, TimeSpan interval, DateTimeOffset now)
    {
        if (anchor >= now)
        {
            return anchor;
        }

        var steps = (now - anchor).Ticks / interval.Ticks;
        var slot = anchor + TimeSpan.FromTicks(steps * interval.Ticks);
        return slot < now ? slot + interval : slot;
    }

    private static (int Count, IntervalUnit Unit) ToIntervalUnit(TimeSpan interval) =>
        interval.Ticks % TimeSpan.TicksPerHour == 0 ? ((int)interval.TotalHours, IntervalUnit.Hour)
        : interval.Ticks % TimeSpan.TicksPerMinute == 0 ? ((int)interval.TotalMinutes, IntervalUnit.Minute)
        : ((int)interval.TotalSeconds, IntervalUnit.Second);

    /// <summary>Adds or replaces the job and its trigger, paused when the configuration says so (the one pause state).</summary>
    public static async Task ScheduleAsync(IScheduler scheduler, JobDefinition definition, CancellationToken cancellationToken,
        string? definitionHash = null)
    {
        var paused = definition.Schedule.Paused;
        var options = new ScheduleJobOptions
        {
            Replace = true,
            Paused = paused,
            PauseReason = paused ? definition.Schedule.PauseReason ?? PauseReasons.Configuration : null,
            PauseRequestedBy = paused ? definition.Schedule.PausedBy : null
        };
        await scheduler.ScheduleJob(BuildJob(definition, definitionHash), [BuildTrigger(definition, DateTimeOffset.UtcNow)], options, cancellationToken);
    }
}
