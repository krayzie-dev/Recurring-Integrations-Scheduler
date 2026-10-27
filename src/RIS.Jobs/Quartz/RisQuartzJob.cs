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
public sealed class RisQuartzJob(JobPipeline pipeline, ILogger<RisQuartzJob> logger) : IJob<RisJobInput>
{
    public async ValueTask Execute(IJobExecutionContext context, RisJobInput input, CancellationToken cancellationToken)
    {
        var key = context.JobDetail.Key;
        var jobKey = $"{key.Group}.{key.Name}";
        var options = input.Options ?? throw new JobExecutionException($"Job {jobKey} has no options.");

        if (options.IndefinitePause)
        {
            await context.Scheduler.PauseJob(key, cancellationToken);
            logger.LogInformation("Job {JobKey} is disabled (IndefinitePause) and was paused", jobKey);
            context.Result = JobRunReport.Skipped("Job is disabled (IndefinitePause)");
            return;
        }

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
            await context.Scheduler.PauseJob(key, cancellationToken);
            logger.LogWarning("Job {JobKey} was paused because it failed and PauseOnError is set", jobKey);
        }
    }
}

/// <summary>
/// Creates Quartz jobs and triggers from RIS job definitions.
/// </summary>
public static class RisJobScheduling
{
    public static IJobDetail BuildJob(JobDefinition definition) =>
        JobBuilder.Create<RisQuartzJob>()
            .WithIdentity(definition.Name, definition.Group)
            .WithDescription(definition.Description)
            .DisallowConcurrentExecution()
            .StoreDurably()
            .UsingInput(new RisJobInput { Options = definition.Options })
            .Build();

    public static ITrigger BuildTrigger(JobDefinition definition, DateTimeOffset now)
    {
        var builder = TriggerBuilder.Create()
            .WithIdentity(definition.Name + " trigger", definition.Group)
            .ForJob(definition.Name, definition.Group);

        var schedule = definition.Schedule;
        if (!string.IsNullOrWhiteSpace(schedule.Cron))
        {
            builder = builder.WithCronSchedule(schedule.Cron, cron =>
            {
                if (!string.IsNullOrWhiteSpace(schedule.TimeZone))
                {
                    cron.InTimeZone(TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone));
                }
                cron.WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed);
            }).StartNow();
        }
        else
        {
            builder = builder.WithSimpleSchedule(s => s.WithInterval(schedule.Interval!.Value).RepeatForever()
                    .WithMisfireInstruction(SimpleTriggerMisfireInstruction.NextWithRemainingCount))
                .StartAt(schedule.StartAt ?? now);
        }

        return builder.Build();
    }

    /// <summary>Adds or replaces the job and its trigger; pauses it when configured.</summary>
    public static async Task ScheduleAsync(IScheduler scheduler, JobDefinition definition, CancellationToken cancellationToken)
    {
        var paused = definition.Schedule.Paused || definition.Options?.IndefinitePause == true;
        var options = new ScheduleJobOptions
        {
            Replace = true,
            Paused = paused,
            PauseReason = paused ? (definition.Options?.IndefinitePause == true ? "IndefinitePause" : "Paused in configuration") : null
        };
        await scheduler.ScheduleJob(BuildJob(definition), [BuildTrigger(definition, DateTimeOffset.UtcNow)], options, cancellationToken);
    }
}
