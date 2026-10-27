using Quartz;
using RIS.Jobs.Quartz;

namespace RIS.Server.Services;

public enum AttentionKind
{
    Failing,
    NoRecentSuccess,
    Paused,
    NotScheduled
}

public sealed record AttentionItem(JobSummary Job, AttentionKind Kind, string? Detail, DateTimeOffset? SinceUtc);

public sealed record RunningItem(string RunId, string Job, DateTimeOffset StartedUtc);

public sealed record DashboardData(
    int Total, int Healthy, int Failing, int Paused, int Running,
    IReadOnlyList<AttentionItem> Attention, IReadOnlyList<RunningItem> RunningNow,
    IReadOnlyList<StatisticsBucket> Last24Hours, IReadOnlyList<RunDetails> RecentFailures);

/// <summary>
/// Data of the dashboard. "No recent success": the last success is older than the limit from Ris:Health:RequiredJobs,
/// or else than twice the job's period plus one hour (period = time between previous and next run).
/// </summary>
public sealed class DashboardService(JobQueryService queries, RunningJobs running, HealthOptions health, TimeProvider time)
{
    public async Task<DashboardData> LoadAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var jobs = await queries.ListAsync(ct);
        var attention = new List<AttentionItem>();
        foreach (var job in jobs)
        {
            if (job.ConsecutiveFailures > 0)
            {
                attention.Add(new AttentionItem(job, AttentionKind.Failing, job.LastSummary, job.LastRunUtc));
            }
            else if (IsStale(job, now) is { } limit)
            {
                attention.Add(new AttentionItem(job, AttentionKind.NoRecentSuccess, limit, job.LastSucceededUtc));
            }

            if (job.IsPaused)
            {
                attention.Add(new AttentionItem(job, AttentionKind.Paused, job.PauseReason is null ? null : string.IsNullOrEmpty(job.PausedBy) ? job.PauseReason : $"{job.PauseReason} ({job.PausedBy})", null));
            }
            else if (job.State == "NotScheduled")
            {
                attention.Add(new AttentionItem(job, AttentionKind.NotScheduled, null, null));
            }
        }

        var runningNow = running.Current
            .Select(r => new RunningItem(r.RunId, $"{r.Key.Group}.{r.Key.Name}", r.StartedUtc))
            .OrderBy(r => r.StartedUtc).ToList();
        var hourStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var statistics = await queries.StatisticsAsync(hourStart.AddHours(-23), hourStart.AddHours(1), TimeSpan.FromHours(1), ct);
        var failures = await queries.HistoryAsync(new HistoryFilter(FromUtc: now.AddDays(-1), Results: [JobRunResult.Failed], Take: 10), ct);

        return new DashboardData(
            jobs.Count,
            jobs.Count(j => j.ConsecutiveFailures == 0 && !j.IsPaused),
            jobs.Count(j => j.ConsecutiveFailures > 0),
            jobs.Count(j => j.IsPaused),
            runningNow.Count,
            attention.OrderBy(a => a.Kind).ThenBy(a => a.Job.Key).ToList(),
            runningNow,
            FillHours(statistics, hourStart.AddHours(-23)),
            failures);
    }

    /// <summary>Returns the limit as text when the job has not succeeded within it, otherwise null.</summary>
    private string? IsStale(JobSummary job, DateTimeOffset now)
    {
        if (job.LastSucceededUtc is null || job.IsPaused) return null;
        TimeSpan? limit = health.RequiredJobs.TryGetValue(job.Key, out var required) ? required
            : job.NextRunUtc is { } next && job.PreviousRunUtc is { } previous && next > previous ? (next - previous) * 2 + TimeSpan.FromHours(1)
            : null;
        return limit is { } l && now - job.LastSucceededUtc.Value > l ? Ui.UiFormat.Duration(l) : null;
    }

    /// <summary>24 hourly buckets, also hours without runs.</summary>
    private static IReadOnlyList<StatisticsBucket> FillHours(IReadOnlyList<StatisticsBucket> buckets, DateTimeOffset from)
    {
        var byHour = buckets.ToDictionary(b => b.StartUtc.ToUniversalTime());
        return Enumerable.Range(0, 24).Select(i => from.AddHours(i))
            .Select(hour => byHour.TryGetValue(hour, out var b) ? b : new StatisticsBucket(hour, 0, 0, 0))
            .ToList();
    }
}
