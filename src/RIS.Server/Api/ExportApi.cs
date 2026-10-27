using Quartz;
using RIS.Persistence;
using RIS.Server.Services;

namespace RIS.Server.Api;

/// <summary>
/// Exports for automation: /api/v1/export/{jobs|history|plan}.{xlsx|csv}, audit (Admin). Headers follow Accept-Language,
/// times are server time unless ?zone=utc. History and plan take ?from=&amp;to= (default: last / next 7 days).
/// </summary>
public static class ExportApi
{
    private const string Format = "{format:regex(^(xlsx|csv)$)}";

    public static void MapExportApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/export").RequireAuthorization(RisRoles.CanView).WithTags("Export");

        api.MapGet($"/jobs.{Format}", async (string format, string? zone, JobQueryService queries, ExportService exports, CancellationToken ct) =>
            File(exports.Jobs(await queries.ListAsync(ct), format, Zone(zone))));

        api.MapGet($"/history.{Format}", async (string format, DateTimeOffset? from, DateTimeOffset? to, string? result, string? job, string? zone,
            JobQueryService queries, ExportService exports, CancellationToken ct) =>
        {
            var results = Enum.TryParse<JobRunResult>(result, ignoreCase: true, out var parsed) ? new[] { parsed } : null;
            var runs = await queries.HistoryAsync(new HistoryFilter(FromUtc: from ?? DateTimeOffset.UtcNow.AddDays(-7), ToUtc: to, Results: results,
                Contains: job, Take: HistoryTimelineService.MaxRuns), ct);
            return File(exports.History(runs, format, Zone(zone)));
        });

        api.MapGet($"/plan.{Format}", async (string format, DateTimeOffset? from, DateTimeOffset? to, string? zone,
            SchedulePlanService plans, ExportService exports, CancellationToken ct) =>
        {
            var start = from ?? DateTimeOffset.UtcNow;
            var plan = await plans.PlanAsync(start, to ?? start.AddDays(7), TimeSpan.FromMinutes(30), TimelineGrouping.Group, ct: ct);
            return File(exports.Plan(plan, format, Zone(zone)));
        });

        api.MapGet($"/audit.{Format}", async (string format, string? zone, ConfigurationRepository repository, ExportService exports, CancellationToken ct) =>
            File(exports.Audit(await repository.GetAuditAsync(5000, ct), format, Zone(zone)))).RequireAuthorization(RisRoles.CanAdminister);
    }

    private static TimeZoneInfo Zone(string? zone) =>
        string.Equals(zone, "utc", StringComparison.OrdinalIgnoreCase) ? TimeZoneInfo.Utc : TimeZoneInfo.Local;

    private static IResult File(ExportFile file) => Results.File(file.Content, file.ContentType, file.FileName);
}
