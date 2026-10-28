namespace RIS.Server.Ui;

public enum TimelineRange
{
    Day,
    Week
}

/// <summary>Time window of a Gantt view (whole local days in the display time zone, weeks start on Monday).</summary>
public sealed record TimelineWindow(TimelineRange Range, DateTime StartLocal, TimeZoneInfo Zone)
{
    public static TimelineWindow Current(TimelineRange range, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
        return new TimelineWindow(range, range == TimelineRange.Week ? MondayOf(today) : today, zone);
    }

    public DateTime EndLocal => StartLocal.AddDays(Range == TimelineRange.Week ? 7 : 1);

    public DateTimeOffset FromUtc => LocalToUtc(StartLocal);
    public DateTimeOffset UntilUtc => LocalToUtc(EndLocal);

    public TimelineWindow Previous() => this with { StartLocal = StartLocal.AddDays(Range == TimelineRange.Week ? -7 : -1) };
    public TimelineWindow Next() => this with { StartLocal = StartLocal.AddDays(Range == TimelineRange.Week ? 7 : 1) };

    public TimelineWindow WithRange(TimelineRange range)
    {
        if (range == TimelineRange.Week) return new TimelineWindow(range, MondayOf(StartLocal), Zone);
        // Day view of a week: today when it is in that week, otherwise its first day.
        var today = Current(TimelineRange.Day, Zone).StartLocal;
        return new TimelineWindow(range, today >= StartLocal && today < EndLocal ? today : StartLocal, Zone);
    }

    /// <summary>Shorter than this between runs → shown as a continuous band.</summary>
    public TimeSpan BandThreshold => Range == TimelineRange.Week ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(5);
    public double TimeWidth => Range == TimelineRange.Week ? 7 * 210 : 24 * 60;
    public int HourStep => Range == TimelineRange.Week ? 6 : 1;

    /// <summary>Zoom levels of the time axis (1 = <see cref="TimeWidth"/>); "fit" may give any value in between.</summary>
    public static readonly double[] ZoomLevels = [0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12];
    public const double MinZoom = 0.25, MaxZoom = 12;

    public double TimeWidthAt(double zoom) => TimeWidth * Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>Hours between the labels of the axis: as dense as possible with at least 40 px between labels.</summary>
    public int HourStepAt(double zoom)
    {
        var pixelsPerHour = TimeWidthAt(zoom) / (EndLocal - StartLocal).TotalHours;
        foreach (var step in new[] { 1, 2, 3, 6, 12 })
        {
            if (step * pixelsPerHour >= 40) return step;
        }
        return 24;
    }

    /// <summary>Next zoom level in the direction (+1 larger, -1 smaller) from any current value.</summary>
    public static double NextZoom(double zoom, int direction) => direction > 0
        ? ZoomLevels.FirstOrDefault(z => z > zoom + 0.001, MaxZoom)
        : ZoomLevels.LastOrDefault(z => z < zoom - 0.001, MinZoom);
    public string DayFormat => Range == TimelineRange.Week ? "ddd d.MM" : "dddd, d MMMM yyyy";

    public string Title => Range == TimelineRange.Week
        ? $"{StartLocal:d} – {EndLocal.AddDays(-1):d}"
        : StartLocal.ToString("D");

    private DateTimeOffset LocalToUtc(DateTime local)
    {
        while (Zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static DateTime MondayOf(DateTime day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
