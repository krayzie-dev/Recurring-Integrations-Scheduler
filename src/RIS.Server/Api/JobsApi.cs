using System.Security.Claims;
using System.Text.Json;
using Quartz;
using Quartz.Extensibility;
using RIS.Core.Configuration;
using RIS.Jobs.Quartz;

namespace RIS.Server.Api;

public sealed record JobSummary(
    string Key, string Group, string Name, string Type, string? Description, string Schedule, string State,
    DateTimeOffset? NextRunUtc, DateTimeOffset? PreviousRunUtc,
    string? LastResult, string? LastSummary, DateTimeOffset? LastRunUtc, DateTimeOffset? LastSucceededUtc, int ConsecutiveFailures);

public sealed record JobDetails(JobSummary Summary, JobDefinition Definition, IReadOnlyList<DateTimeOffset> UpcomingRunsUtc);

public sealed record RunDetails(
    string RunId, string EntryId, string Job, DateTimeOffset FiredAtUtc, TimeSpan Duration, string Result,
    string? Summary, JsonElement? Metrics, string? Error, bool Manual, string? Log);

public sealed record PauseRequest(string? Reason);

/// <summary>Operational API: jobs, their state, history and runs (Viewer), run/pause/resume (Operator).</summary>
public static class JobsApi
{
    public static void MapJobsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization(RisRoles.CanView).WithTags("Jobs");
        api.MapGet("/jobs", ListAsync);
        api.MapGet("/jobs/{group}/{name}", GetAsync);
        api.MapGet("/jobs/{group}/{name}/history", HistoryAsync);
        api.MapGet("/runs/{runId}", RunAsync);
        api.MapPost("/jobs/{group}/{name}/run", TriggerAsync).RequireAuthorization(RisRoles.CanOperate);
        api.MapPost("/jobs/{group}/{name}/pause", PauseAsync).RequireAuthorization(RisRoles.CanOperate);
        api.MapPost("/jobs/{group}/{name}/resume", ResumeAsync).RequireAuthorization(RisRoles.CanOperate);
    }

    private static async Task<IResult> ListAsync(IRisConfigurationStore store, ISchedulerFactory schedulers, IExecutionHistoryStore history, CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        var result = new List<JobSummary>();
        foreach (var job in store.Current.Jobs.OrderBy(j => j.Group).ThenBy(j => j.Name))
        {
            result.Add(await SummaryAsync(scheduler, history, job, ct));
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> GetAsync(string group, string name, IRisConfigurationStore store, ISchedulerFactory schedulers, IExecutionHistoryStore history, CancellationToken ct)
    {
        if (Find(store, group, name) is not { } job) return NotFound(group, name);
        var scheduler = await schedulers.GetScheduler(ct);
        var trigger = await RegularTriggerAsync(scheduler, job, ct);
        var upcoming = new List<DateTimeOffset>();
        var next = trigger?.NextFireTimeUtc;
        for (var i = 0; i < 5 && next is not null; i++)
        {
            upcoming.Add(next.Value);
            next = trigger!.GetFireTimeAfter(next);
        }
        return Results.Ok(new JobDetails(await SummaryAsync(scheduler, history, job, ct), job, upcoming));
    }

    private static async Task<IResult> HistoryAsync(string group, string name, int? take, IRisConfigurationStore store, ISchedulerFactory schedulers, IExecutionHistoryStore history, CancellationToken ct)
    {
        if (Find(store, group, name) is null) return NotFound(group, name);
        var scheduler = await schedulers.GetScheduler(ct);
        var page = await history.QueryExecutions(new ExecutionHistoryQuery { SchedulerName = scheduler.SchedulerName, Job = new JobKey(name, group) }, ct);
        return Results.Ok(page.Items.Take(Math.Clamp(take ?? 50, 1, 500)).Select(e => ToRun(e, includeLog: false)));
    }

    private static async Task<IResult> RunAsync(string runId, ISchedulerFactory schedulers, IExecutionHistoryStore history, CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        var page = await history.QueryExecutions(new ExecutionHistoryQuery { SchedulerName = scheduler.SchedulerName, FireInstanceId = runId }, ct);
        if (page.Items.FirstOrDefault() is not { } entry)
        {
            return Results.Problem($"Run '{runId}' was not found in execution history.", statusCode: StatusCodes.Status404NotFound);
        }

        // List queries do not return the captured log; the single entry does.
        var full = entry.EntryId is null ? entry : await history.GetExecution(scheduler.SchedulerName, entry.EntryId, ct) ?? entry;
        return Results.Ok(ToRun(full, includeLog: true));
    }

    private static async Task<IResult> TriggerAsync(string group, string name, IRisConfigurationStore store, ISchedulerFactory schedulers,
        ClaimsPrincipal user, ILoggerFactory loggers, CancellationToken ct)
    {
        if (Find(store, group, name) is not { } job) return NotFound(group, name);
        await RisJobScheduling.TriggerOnceAsync(await schedulers.GetScheduler(ct), job, ct);
        loggers.CreateLogger("RIS.Server.Api").LogInformation("Job {JobKey} started manually by {User}", job.Key, user.Identity?.Name);
        return Results.Accepted($"/api/v1/jobs/{group}/{name}/history", new { message = $"Job {job.Key} started." });
    }

    private static async Task<IResult> PauseAsync(string group, string name, PauseRequest? request, IRisConfigurationStore store, ISchedulerFactory schedulers,
        ClaimsPrincipal user, ILoggerFactory loggers, CancellationToken ct)
    {
        if (Find(store, group, name) is not { } job) return NotFound(group, name);
        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? "Paused by operator" : request.Reason;
        await (await schedulers.GetScheduler(ct)).PauseJobWith(new JobKey(name, group), new PauseDetails { Reason = reason, RequestedBy = user.Identity?.Name }, ct);
        loggers.CreateLogger("RIS.Server.Api").LogInformation("Job {JobKey} paused by {User}: {Reason}", job.Key, user.Identity?.Name, reason);
        return Results.NoContent();
    }

    private static async Task<IResult> ResumeAsync(string group, string name, IRisConfigurationStore store, ISchedulerFactory schedulers,
        ClaimsPrincipal user, ILoggerFactory loggers, CancellationToken ct)
    {
        if (Find(store, group, name) is not { } job) return NotFound(group, name);
        await (await schedulers.GetScheduler(ct)).ResumeJob(new JobKey(name, group), ct);
        loggers.CreateLogger("RIS.Server.Api").LogInformation("Job {JobKey} resumed by {User}", job.Key, user.Identity?.Name);
        return Results.NoContent();
    }

    private static async Task<JobSummary> SummaryAsync(IScheduler scheduler, IExecutionHistoryStore history, JobDefinition job, CancellationToken ct)
    {
        var trigger = await RegularTriggerAsync(scheduler, job, ct);
        var state = trigger is null ? "NotScheduled" : (await scheduler.GetTriggerState(trigger.Key, ct)).ToString();
        var status = await history.GetJobRunStatus(scheduler.SchedulerName, new JobKey(job.Name, job.Group), ct);
        return new JobSummary(job.Key, job.Group, job.Name, job.Type.ToString(), job.Description, Describe(job.Schedule), state,
            trigger?.NextFireTimeUtc, trigger?.PreviousFireTimeUtc,
            status?.LastResult.ToString(), status?.LastSummary, status?.LastFiredAtUtc, status?.LastSucceededAtUtc, status?.ConsecutiveFailures ?? 0);
    }

    private static async Task<ITrigger?> RegularTriggerAsync(IScheduler scheduler, JobDefinition job, CancellationToken ct) =>
        (await scheduler.GetTriggersOfJob(new JobKey(job.Name, job.Group), ct)).FirstOrDefault(t => t.Key.Name == job.Name + " trigger");

    private static string Describe(ScheduleOptions schedule)
    {
        var text = schedule.Cron is not null ? $"cron {schedule.Cron}" : $"every {schedule.Interval} from {schedule.StartAt:yyyy-MM-dd HH:mm}";
        return schedule.TimeZone is null ? text : $"{text} ({schedule.TimeZone})";
    }

    private static RunDetails ToRun(ExecutionHistoryEntry e, bool includeLog) => new(
        e.FireInstanceId ?? "", e.EntryId ?? "", $"{e.JobGroup}.{e.JobName}", e.FiredAtUtc, e.Duration, e.EffectiveResult.ToString(), e.Summary,
        string.IsNullOrEmpty(e.MetricsJson) ? null : JsonDocument.Parse(e.MetricsJson).RootElement.Clone(),
        string.IsNullOrEmpty(e.ExceptionMessage) ? null : e.ExceptionMessage, e.Manual, includeLog ? e.Log : null);

    private static JobDefinition? Find(IRisConfigurationStore store, string group, string name) =>
        store.Current.Jobs.FirstOrDefault(j => string.Equals(j.Group, group, StringComparison.OrdinalIgnoreCase) && string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IResult NotFound(string group, string name) =>
        Results.Problem($"Job '{group}.{name}' is not configured.", statusCode: StatusCodes.Status404NotFound);
}
