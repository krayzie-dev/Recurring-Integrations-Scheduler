using System.Security.Claims;
using Quartz;
using RIS.Server.Services;

namespace RIS.Server.Api;

public sealed record PauseRequest(string? Reason);

/// <summary>Bulk operation: jobs as "Group.Name" keys.</summary>
public sealed record BulkRequest(IReadOnlyList<string> Jobs, string? Reason = null);

/// <summary>Operational API: jobs, their state, history and runs (Viewer), run/pause/resume (Operator), bulk delete (Admin).</summary>
public static class JobsApi
{
    public static void MapJobsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization(RisRoles.CanView).WithTags("Jobs");
        api.MapGet("/jobs", async (JobQueryService queries, CancellationToken ct) => Results.Ok(await queries.ListAsync(ct)));
        api.MapGet("/jobs/{group}/{name}", async (string group, string name, JobQueryService queries, CancellationToken ct) =>
            await queries.GetAsync(group, name, ct) is { } details ? Results.Ok(details) : NotFound(group, name));
        api.MapGet("/jobs/{group}/{name}/history", async (string group, string name, int? take, DateTimeOffset? from, DateTimeOffset? to,
            JobQueryService queries, CancellationToken ct) =>
            queries.Find(group, name) is null
                ? NotFound(group, name)
                : Results.Ok(await queries.HistoryAsync(new HistoryFilter(group, name, from, to, Take: take ?? 50), ct)));
        api.MapGet("/history", async (DateTimeOffset? from, DateTimeOffset? to, string? result, string? job, int? take,
            JobQueryService queries, CancellationToken ct) =>
            Results.Ok(await queries.HistoryAsync(new HistoryFilter(FromUtc: from, ToUtc: to, Results: ParseResults(result), Contains: job, Take: take ?? 500), ct)));
        api.MapGet("/runs/{runId}", async (string runId, JobQueryService queries, CancellationToken ct) =>
            await queries.GetRunAsync(runId, ct) is { } run
                ? Results.Ok(run)
                : Results.Problem($"Run '{runId}' was not found in execution history.", statusCode: StatusCodes.Status404NotFound));

        var operate = api.MapGroup("").RequireAuthorization(RisRoles.CanOperate);
        operate.MapPost("/jobs/{group}/{name}/run", async (string group, string name, JobOperationsService operations, ClaimsPrincipal user, CancellationToken ct) =>
            Single(await operations.RunAsync([new JobRef(group, name)], user.Identity?.Name, ct), group, name,
                () => Results.Accepted($"/api/v1/jobs/{group}/{name}/history", new { message = $"Job {group}.{name} started." })));
        operate.MapPost("/jobs/{group}/{name}/pause", async (string group, string name, PauseRequest? request, JobOperationsService operations,
            ClaimsPrincipal user, CancellationToken ct) =>
            Single(await operations.PauseAsync([new JobRef(group, name)], request?.Reason, user.Identity?.Name, ct), group, name, Results.NoContent));
        operate.MapPost("/jobs/{group}/{name}/resume", async (string group, string name, JobOperationsService operations, ClaimsPrincipal user, CancellationToken ct) =>
            Single(await operations.ResumeAsync([new JobRef(group, name)], user.Identity?.Name, ct), group, name, Results.NoContent));

        operate.MapPost("/jobs/bulk/run", async (BulkRequest request, JobOperationsService operations, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(await operations.RunAsync(Refs(request), user.Identity?.Name, ct)));
        operate.MapPost("/jobs/bulk/pause", async (BulkRequest request, JobOperationsService operations, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(await operations.PauseAsync(Refs(request), request.Reason, user.Identity?.Name, ct)));
        operate.MapPost("/jobs/bulk/resume", async (BulkRequest request, JobOperationsService operations, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(await operations.ResumeAsync(Refs(request), user.Identity?.Name, ct)));
        api.MapPost("/jobs/bulk/delete", async (BulkRequest request, JobOperationsService operations, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(await operations.DeleteAsync(Refs(request), user.Identity?.Name, ct))).RequireAuthorization(RisRoles.CanAdminister);
    }

    private static IEnumerable<JobRef> Refs(BulkRequest request) => request.Jobs.Select(JobRef.Parse);

    private static IReadOnlyCollection<JobRunResult>? ParseResults(string? results) =>
        string.IsNullOrWhiteSpace(results)
            ? null
            : results.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(r => Enum.TryParse<JobRunResult>(r, ignoreCase: true, out var value) ? value : (JobRunResult?)null)
                .OfType<JobRunResult>().ToList();

    private static IResult Single(IReadOnlyList<BulkItemResult> results, string group, string name, Func<IResult> ok) =>
        results[0] switch
        {
            { Succeeded: true } => ok(),
            var failed when failed.Error?.Contains("is not configured", StringComparison.Ordinal) == true => NotFound(group, name),
            var failed => Results.Problem(failed.Error, statusCode: StatusCodes.Status409Conflict)
        };

    private static IResult NotFound(string group, string name) =>
        Results.Problem($"Job '{group}.{name}' is not configured.", statusCode: StatusCodes.Status404NotFound);
}
