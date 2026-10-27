using System.Text.Json;
using System.Text.RegularExpressions;
using Quartz;
using Quartz.Extensibility;
using RIS.Core.Configuration;
using RIS.Jobs.Quartz;

namespace RIS.Server.Services;

public sealed record JobSummary(
    string Key, string Group, string Name, string Type, string? Description, string? Connection, string Schedule, string State,
    DateTimeOffset? NextRunUtc, DateTimeOffset? PreviousRunUtc,
    string? LastResult, string? LastSummary, DateTimeOffset? LastRunUtc, DateTimeOffset? LastSucceededUtc, int ConsecutiveFailures,
    string? PauseReason, string? PausedBy, bool Running)
{
    public bool IsPaused => State == nameof(TriggerState.Paused);
}

public sealed record JobDetails(JobSummary Summary, JobDefinition Definition, IReadOnlyList<DateTimeOffset> UpcomingRunsUtc);

public sealed record RunDetails(
    string RunId, string EntryId, string Job, DateTimeOffset FiredAtUtc, TimeSpan Duration, string Result,
    string? Summary, JsonElement? Metrics, string? Error, bool Manual, string? Log)
{
    private static readonly Regex ErrorCodePattern = new(@"\[(RIS-[A-Z]+-\d{3})\]", RegexOptions.Compiled);

    public string Group => Job[..Job.IndexOf('.')];
    public string Name => Job[(Job.IndexOf('.') + 1)..];
    public DateTimeOffset EndUtc => FiredAtUtc + Duration;

    /// <summary>RIS error code from the summary or error, e.g. RIS-AUTH-001.</summary>
    public string? ErrorCode => ErrorCodePattern.Match(Summary ?? Error ?? string.Empty) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>D365 execution id or message id from the run metrics, when the job reported one.</summary>
    public string? ExecutionId => Metric("ExecutionId") ?? Metric("MessageId");

    private string? Metric(string name) =>
        Metrics is { ValueKind: JsonValueKind.Object } m && m.TryGetProperty(name, out var value) ? value.ToString() : null;
}

/// <summary>A run planned while the service was down, or skipped because the previous run was still running.</summary>
public sealed record MissedRun(string Job, DateTimeOffset ScheduledUtc, string Reason);

public sealed record HistoryFilter(
    string? Group = null, string? Name = null, DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null,
    IReadOnlyCollection<JobRunResult>? Results = null, string? Contains = null, int Take = 500);

public sealed record StatisticsBucket(DateTimeOffset StartUtc, int Succeeded, int Failed, int Other);

/// <summary>Read side of jobs: configuration joined with scheduler state and execution history. Used by the UI and the REST API.</summary>
public sealed class JobQueryService(IRisConfigurationStore store, ISchedulerFactory schedulers, IExecutionHistoryStore history, RunningJobs running)
{
    public async Task<IReadOnlyList<JobSummary>> ListAsync(CancellationToken ct = default)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        var result = new List<JobSummary>();
        foreach (var job in store.Current.Jobs.OrderBy(j => j.Group).ThenBy(j => j.Name))
        {
            result.Add(await SummaryAsync(scheduler, job, ct));
        }
        return result;
    }

    public JobDefinition? Find(string group, string name) =>
        store.Current.Jobs.FirstOrDefault(j => string.Equals(j.Group, group, StringComparison.OrdinalIgnoreCase) && string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));

    public async Task<JobDetails?> GetAsync(string group, string name, CancellationToken ct = default)
    {
        if (Find(group, name) is not { } job) return null;
        var scheduler = await schedulers.GetScheduler(ct);
        var trigger = await RegularTriggerAsync(scheduler, job, ct);
        var upcoming = new List<DateTimeOffset>();
        var next = trigger?.NextFireTimeUtc;
        for (var i = 0; i < 5 && next is not null; i++)
        {
            upcoming.Add(next.Value);
            next = trigger!.GetFireTimeAfter(next);
        }
        return new JobDetails(await SummaryAsync(scheduler, job, ct), job, upcoming);
    }

    public async Task<IReadOnlyList<RunDetails>> HistoryAsync(HistoryFilter filter, CancellationToken ct = default)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        var page = await history.QueryExecutions(new ExecutionHistoryQuery
        {
            SchedulerName = scheduler.SchedulerName,
            Job = filter.Name is not null ? new JobKey(filter.Name, filter.Group ?? JobKey.DefaultGroup) : null,
            JobContains = filter.Name is null ? filter.Contains : null,
            FiredFrom = filter.FromUtc,
            FiredBefore = filter.ToUtc,
            Results = filter.Results,
            Take = Math.Clamp(filter.Take, 1, 5000)
        }, ct);
        return page.Items.Select(e => ToRun(e, includeLog: false)).ToList();
    }

    public async Task<RunDetails?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        var page = await history.QueryExecutions(new ExecutionHistoryQuery { SchedulerName = scheduler.SchedulerName, FireInstanceId = runId }, ct);
        if (page.Items.FirstOrDefault() is not { } entry) return null;

        // List queries do not return the captured log; the single entry does.
        var full = entry.EntryId is null ? entry : await history.GetExecution(scheduler.SchedulerName, entry.EntryId, ct) ?? entry;
        return ToRun(full, includeLog: true);
    }

    /// <summary>Missed runs recorded by the scheduler (newest first), optionally from a point in time.</summary>
    public async Task<IReadOnlyList<MissedRun>> MissedRunsAsync(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int take = 1000, CancellationToken ct = default)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        var page = await history.QueryMisfires(new MisfireHistoryQuery { SchedulerName = scheduler.SchedulerName, Take = take }, ct);
        return page.Items
            .Where(m => m.JobKey is not null && m.ScheduledFireTimeUtc is not null)
            .Where(m => (fromUtc is null || m.ScheduledFireTimeUtc >= fromUtc) && (toUtc is null || m.ScheduledFireTimeUtc < toUtc))
            .Select(m => new MissedRun($"{m.JobKey!.Group}.{m.JobKey.Name}", m.ScheduledFireTimeUtc!.Value, m.Reason.ToString()))
            .ToList();
    }

    /// <summary>Runs per time bucket (e.g. hour) for charts.</summary>
    public async Task<IReadOnlyList<StatisticsBucket>> StatisticsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeSpan bucket, CancellationToken ct = default)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        var statistics = await history.QueryExecutionStatistics(new ExecutionStatisticsQuery
        {
            SchedulerName = scheduler.SchedulerName,
            FiredFrom = fromUtc,
            FiredBefore = toUtc,
            BucketSize = bucket
        }, ct);
        return statistics.Buckets
            .Select(b => new StatisticsBucket(b.StartUtc, (int)b.SucceededCount, (int)b.FailedCount, (int)(b.RunCount - b.SucceededCount - b.FailedCount)))
            .ToList();
    }

    /// <summary>Durations of the last successful or failed runs per job (for plan estimates).</summary>
    public async Task<IReadOnlyDictionary<string, TimeSpan>> MedianDurationsAsync(int runsPerJob = 20, CancellationToken ct = default)
    {
        var runs = await HistoryAsync(new HistoryFilter(Results: [JobRunResult.Succeeded, JobRunResult.Failed], Take: 5000), ct);
        return runs.GroupBy(r => r.Job, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Median(g.Take(runsPerJob).Select(r => r.Duration)), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ITrigger?> RegularTriggerAsync(IScheduler scheduler, JobDefinition job, CancellationToken ct) =>
        (await scheduler.GetTriggersOfJob(new JobKey(job.Name, job.Group), ct)).FirstOrDefault(t => t.Key.Name == job.Name + " trigger");

    public static string Describe(ScheduleOptions schedule)
    {
        var text = schedule.Cron is not null ? $"cron {schedule.Cron}" : $"every {schedule.Interval} from {schedule.StartAt:yyyy-MM-dd HH:mm}";
        return schedule.TimeZone is null ? text : $"{text} ({schedule.TimeZone})";
    }

    private async Task<JobSummary> SummaryAsync(IScheduler scheduler, JobDefinition job, CancellationToken ct)
    {
        var key = new JobKey(job.Name, job.Group);
        var trigger = await RegularTriggerAsync(scheduler, job, ct);
        var state = trigger is null ? "NotScheduled" : (await scheduler.GetTriggerState(trigger.Key, ct)).ToString();
        var pause = trigger is not null && state == nameof(TriggerState.Paused) ? await scheduler.GetTriggerPause(trigger.Key, ct) : null;
        var status = await history.GetJobRunStatus(scheduler.SchedulerName, key, ct);
        return new JobSummary(job.Key, job.Group, job.Name, job.Type.ToString(), job.Description, job.Options?.Connection, Describe(job.Schedule), state,
            trigger?.NextFireTimeUtc, trigger?.PreviousFireTimeUtc,
            status?.LastResult.ToString(), status?.LastSummary, status?.LastFiredAtUtc, status?.LastSucceededAtUtc, status?.ConsecutiveFailures ?? 0,
            pause?.Reason, pause?.RequestedBy, running.Current.Any(r => r.Key.Equals(key)));
    }

    private static RunDetails ToRun(ExecutionHistoryEntry e, bool includeLog) => new(
        e.FireInstanceId ?? "", e.EntryId ?? "", $"{e.JobGroup}.{e.JobName}", e.FiredAtUtc, e.Duration, e.EffectiveResult.ToString(), e.Summary,
        string.IsNullOrEmpty(e.MetricsJson) ? null : JsonDocument.Parse(e.MetricsJson).RootElement.Clone(),
        string.IsNullOrEmpty(e.ExceptionMessage) ? null : e.ExceptionMessage, e.Manual, includeLog ? e.Log : null);

    public static TimeSpan Median(IEnumerable<TimeSpan> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return TimeSpan.Zero;
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
