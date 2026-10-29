using System.Globalization;
using Quartz;
using RIS.Core.Configuration;
using RIS.Jobs.Quartz;

namespace RIS.Server.Services;

public enum TimelineItemKind
{
    Planned,
    PlannedPaused,
    Band,
    BandPaused,
    Run,
    Running,
    Missed
}

/// <summary>Bar (or marker) on a timeline row. Times are UTC; End = Start for markers.</summary>
public sealed record TimelineItem(DateTimeOffset StartUtc, DateTimeOffset EndUtc, TimelineItemKind Kind, string? Status = null,
    string? RunId = null, string? Label = null, int Count = 1);

/// <summary>Row of a Gantt chart; <paramref name="Type"/> colours the outline of its bars (null for jobs no longer configured).</summary>
public sealed record TimelineRow(string Key, string Label, string GroupLabel, string? Link, IReadOnlyList<TimelineItem> Items, RIS.Core.Configuration.JobType? Type = null);

public sealed record Timeline(DateTimeOffset FromUtc, DateTimeOffset ToUtc, IReadOnlyList<TimelineRow> Rows, bool Truncated = false);

/// <summary>History of a window: the chart and the runs started in the window (for the table), from one query.</summary>
public sealed record HistoryView(Timeline Timeline, IReadOnlyList<RunDetails> Runs);

public enum TimelineGrouping
{
    Group,
    Connection,
    Type,
    None
}

/// <summary>
/// Planned runs of the jobs in a time window. Frequent jobs (period shorter than <paramref name="bandThreshold"/>) are
/// returned as continuous bands ("every 5 min") instead of hundreds of bars. Bar length = median duration of recent runs.
/// </summary>
public sealed class SchedulePlanService(IRisConfigurationStore store, ISchedulerFactory schedulers, JobQueryService queries)
{
    /// <summary>A job running every minute has 10 080 runs in a week; more is cut and reported (Timeline.Truncated).</summary>
    public const int MaxFiresPerJob = 20_000;

    private static readonly TimeSpan DurationsCacheTime = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim DurationsLock = new(1, 1);
    private static (DateTimeOffset LoadedAt, IReadOnlyDictionary<string, TimeSpan> Values)? _durations;

    public async Task<Timeline> PlanAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeSpan bandThreshold, TimelineGrouping grouping,
        Func<JobDefinition, bool>? filter = null, CancellationToken ct = default)
    {
        // One query for all trigger states; fire times come from the trigger built from the definition (same schedule,
        // computed in memory) instead of reading every trigger from the database.
        var scheduler = await schedulers.GetScheduler(ct);
        var paused = (await scheduler.QueryTriggers(new TriggerQuery { State = TriggerState.Paused, Take = PagedQuery.All }, ct)).Items
            .Where(t => t.Key.Name == t.JobKey.Name + " trigger").Select(t => t.JobKey).ToHashSet();
        var durations = await DurationsAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var truncated = false;
        var rows = new List<TimelineRow>();
        foreach (var job in store.Current.Jobs.Where(j => filter?.Invoke(j) ?? true))
        {
            var isPaused = paused.Contains(new JobKey(job.Name, job.Group)) || job.Schedule.Paused;
            var fires = FireTimes(RisJobScheduling.BuildTrigger(job, now), fromUtc, toUtc);
            truncated |= fires.Count >= MaxFiresPerJob;
            durations.TryGetValue(job.Key, out var duration);
            rows.Add(new TimelineRow(job.Key, job.Name, GroupLabel(job, grouping), JobLink(job.Group, job.Name),
                BuildItems(fires, duration, bandThreshold, isPaused), job.Type));
        }
        return new Timeline(fromUtc, toUtc, Sort(rows), truncated);
    }

    /// <summary>Median durations change slowly: read the history at most every 10 minutes, not on every refresh of every viewer.</summary>
    private async Task<IReadOnlyDictionary<string, TimeSpan>> DurationsAsync(CancellationToken ct)
    {
        if (_durations is { } cached && DateTimeOffset.UtcNow - cached.LoadedAt < DurationsCacheTime) return cached.Values;
        await DurationsLock.WaitAsync(ct);
        try
        {
            if (_durations is { } again && DateTimeOffset.UtcNow - again.LoadedAt < DurationsCacheTime) return again.Values;
            var values = await queries.MedianDurationsAsync(ct: ct);
            _durations = (DateTimeOffset.UtcNow, values);
            return values;
        }
        finally
        {
            DurationsLock.Release();
        }
    }

    /// <summary>Fire times of the trigger within [from, to).</summary>
    public static IReadOnlyList<DateTimeOffset> FireTimes(ITrigger trigger, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var result = new List<DateTimeOffset>();
        var next = trigger.GetFireTimeAfter(fromUtc.AddTicks(-1));
        while (next is { } time && time < toUtc && result.Count < MaxFiresPerJob)
        {
            result.Add(time);
            next = trigger.GetFireTimeAfter(time);
        }
        return result;
    }

    /// <summary>Bars for single runs, bands for runs closer to each other than the threshold.</summary>
    public static IReadOnlyList<TimelineItem> BuildItems(IReadOnlyList<DateTimeOffset> fires, TimeSpan duration, TimeSpan bandThreshold, bool paused)
    {
        var items = new List<TimelineItem>();
        var i = 0;
        while (i < fires.Count)
        {
            var j = i;
            while (j + 1 < fires.Count && fires[j + 1] - fires[j] < bandThreshold) j++;
            if (j > i)
            {
                var period = (fires[j] - fires[i]) / (j - i);
                items.Add(new TimelineItem(fires[i], fires[j] + (duration > period ? period : duration),
                    paused ? TimelineItemKind.BandPaused : TimelineItemKind.Band, Label: Ui.UiFormat.Duration(period), Count: j - i + 1));
            }
            else
            {
                items.Add(new TimelineItem(fires[i], fires[i] + duration, paused ? TimelineItemKind.PlannedPaused : TimelineItemKind.Planned));
            }
            i = j + 1;
        }
        return items;
    }

    internal static string GroupLabel(JobDefinition job, TimelineGrouping grouping) => grouping switch
    {
        TimelineGrouping.Connection => job.Options?.Connection ?? "–",
        TimelineGrouping.Type => job.Type.ToString(),
        TimelineGrouping.None => "",
        _ => job.Group
    };

    internal static string JobLink(string group, string name) => $"/jobs/{Uri.EscapeDataString(group)}/{Uri.EscapeDataString(name)}";

    internal static IReadOnlyList<TimelineRow> Sort(IEnumerable<TimelineRow> rows) =>
        rows.OrderBy(r => r.GroupLabel, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
}

/// <summary>Executed, running and missed runs of the jobs in a time window.</summary>
public sealed class HistoryTimelineService(IRisConfigurationStore store, JobQueryService queries, RunningJobs running)
{
    public const int MaxRuns = 5000;

    public async Task<HistoryView> HistoryAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, TimelineGrouping grouping,
        string? search = null, IReadOnlyCollection<JobRunResult>? results = null, CancellationToken ct = default)
    {
        bool Matches(string job) => string.IsNullOrWhiteSpace(search) || job.Contains(search, StringComparison.OrdinalIgnoreCase);
        Func<string, bool>? jobFilter = string.IsNullOrWhiteSpace(search) ? null : Matches;
        // Runs that started before the window but were still running in it are included (up to one day back).
        var runs = await queries.HistoryAsync(new HistoryFilter(FromUtc: fromUtc.AddDays(-1), ToUtc: toUtc, Results: results,
            Contains: string.IsNullOrWhiteSpace(search) ? null : search, Take: MaxRuns), ct);
        var missed = results is null ? await queries.MissedRunsAsync(fromUtc, toUtc, ct: ct) : [];
        var now = DateTimeOffset.UtcNow;

        var items = new Dictionary<string, List<TimelineItem>>(StringComparer.OrdinalIgnoreCase);
        List<TimelineItem> For(string job) => items.TryGetValue(job, out var list) ? list : items[job] = [];

        foreach (var run in runs.Where(r => r.EndUtc >= fromUtc))
        {
            For(run.Job).Add(new TimelineItem(run.FiredAtUtc, run.EndUtc, TimelineItemKind.Run, run.Result, run.RunId, run.ErrorCode));
        }
        foreach (var miss in missed)
        {
            For(miss.Job).Add(new TimelineItem(miss.ScheduledUtc, miss.ScheduledUtc, TimelineItemKind.Missed, miss.Reason));
        }
        if (results is null && toUtc > now)
        {
            foreach (var (runId, key, started) in running.Current)
            {
                For($"{key.Group}.{key.Name}").Add(new TimelineItem(started, now, TimelineItemKind.Running, "Running", runId));
            }
        }

        var configured = store.Current.Jobs.ToDictionary(j => j.Key, StringComparer.OrdinalIgnoreCase);
        var keys = configured.Keys.Concat(items.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Where(k => jobFilter?.Invoke(k) ?? true);
        var rows = keys.Select(key =>
        {
            var reference = JobRef.Parse(key);
            var groupLabel = configured.TryGetValue(key, out var job) ? SchedulePlanService.GroupLabel(job, grouping) : reference.Group;
            return new TimelineRow(key, reference.Name, groupLabel, configured.ContainsKey(key) ? SchedulePlanService.JobLink(reference.Group, reference.Name) : null,
                items.TryGetValue(key, out var list) ? list.OrderBy(i => i.StartUtc).ToList() : [], job?.Type);
        });
        return new HistoryView(new Timeline(fromUtc, toUtc, SchedulePlanService.Sort(rows), Truncated: runs.Count >= MaxRuns),
            runs.Where(r => r.FiredAtUtc >= fromUtc).ToList());
    }
}

/// <summary>
/// Geometry of a Gantt chart: time → x, day and hour lines in the display time zone (days of 23/25 h on DST changes),
/// bar positions clipped to the window with a minimum width.
/// </summary>
public sealed class GanttLayout
{
    public const double LabelWidth = 240;
    public const double RowHeight = 22;
    public const double HeaderHeight = 40;
    public const double GroupHeight = 22;
    // Wide enough to show the status fill inside the 2 px outline of the job type.
    public const double MinBarWidth = 6;

    public GanttLayout(DateTimeOffset fromUtc, DateTimeOffset toUtc, double timeWidth, TimeZoneInfo zone)
    {
        if (toUtc <= fromUtc) throw new ArgumentException("Empty time window.");
        FromUtc = fromUtc;
        ToUtc = toUtc;
        TimeWidth = timeWidth;
        Zone = zone;
    }

    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public double TimeWidth { get; }
    public TimeZoneInfo Zone { get; }
    public double Width => LabelWidth + TimeWidth;

    public double X(DateTimeOffset time) => LabelWidth + (time - FromUtc).TotalSeconds / (ToUtc - FromUtc).TotalSeconds * TimeWidth;

    /// <summary>Bar in the window: x and width (at least <see cref="MinBarWidth"/>), or null when outside the window.</summary>
    public (double X, double Width)? Bar(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc < FromUtc || startUtc >= ToUtc) return null;
        var x1 = X(startUtc < FromUtc ? FromUtc : startUtc);
        var x2 = X(endUtc > ToUtc ? ToUtc : endUtc);
        return (x1, Math.Max(MinBarWidth, x2 - x1));
    }

    /// <summary>Local midnights within the window (start of each day column).</summary>
    public IReadOnlyList<DateTimeOffset> Days()
    {
        var result = new List<DateTimeOffset>();
        var local = TimeZoneInfo.ConvertTime(FromUtc, Zone);
        var day = local.Date;
        while (true)
        {
            var midnight = LocalToUtc(day);
            if (midnight >= ToUtc) break;
            if (midnight >= FromUtc) result.Add(midnight);
            day = day.AddDays(1);
        }
        return result;
    }

    /// <summary>Hour ticks every <paramref name="step"/> hours of local time (labels show local hours).</summary>
    public IReadOnlyList<DateTimeOffset> Hours(int step)
    {
        var result = new List<DateTimeOffset>();
        var local = TimeZoneInfo.ConvertTime(FromUtc, Zone);
        var hour = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);
        for (var t = hour; ; t = t.AddHours(1))
        {
            var utc = LocalToUtc(t);
            if (utc >= ToUtc) break;
            if (utc >= FromUtc && t.Hour % step == 0 && (result.Count == 0 || result[^1] != utc)) result.Add(utc);
        }
        return result;
    }

    public DateTimeOffset Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    public string Label(DateTimeOffset utc, string format) => Local(utc).ToString(format, CultureInfo.CurrentCulture);

    /// <summary>Local wall-clock time to UTC; a time skipped by DST moves forward, an ambiguous one takes standard time.</summary>
    private DateTimeOffset LocalToUtc(DateTime local)
    {
        while (Zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
