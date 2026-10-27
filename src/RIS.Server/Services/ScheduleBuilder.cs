using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;
using RIS.Core.Configuration;
using RIS.Server.Resources;

namespace RIS.Server.Services;

public enum ScheduleMode
{
    /// <summary>Once a day at a time, optionally only on selected days of the week (cron).</summary>
    Daily,
    /// <summary>Every N minutes/hours from a time of day (interval with a start point).</summary>
    Every,
    /// <summary>On a day of the month (or its last day) at a time (cron).</summary>
    Monthly,
    /// <summary>Any Quartz cron expression.</summary>
    Advanced
}

/// <summary>Schedule as edited in the UI; converted to and from <see cref="ScheduleOptions"/> by <see cref="ScheduleBuilder"/>.</summary>
public sealed class ScheduleModel
{
    public ScheduleMode Mode { get; set; } = ScheduleMode.Daily;

    /// <summary>Daily and Monthly: time of day.</summary>
    public TimeSpan At { get; set; } = new(2, 0, 0);

    /// <summary>Daily: days of the week; empty = every day.</summary>
    public HashSet<DayOfWeek> Days { get; set; } = [];

    /// <summary>Monthly: day 1–31, 0 = last day of the month.</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Every: interval and the time of day the rhythm starts from.</summary>
    public TimeSpan Every { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan From { get; set; } = TimeSpan.Zero;
    /// <summary>Every: date of the reference point (kept when editing; only matters when the interval does not divide a day).</summary>
    public DateTime? FromDate { get; set; }

    public string? Cron { get; set; }

    public string? TimeZone { get; set; }
    public MissedRunPolicy MissedRuns { get; set; } = MissedRunPolicy.RunOnce;
    public bool Paused { get; set; }
}

/// <summary>Converts between the schedule builder of the UI and <see cref="ScheduleOptions"/>, and describes schedules in words.</summary>
public static partial class ScheduleBuilder
{
    private static readonly DayOfWeek[] WeekOrder =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private static readonly string[] CronDays = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    public static readonly IReadOnlySet<DayOfWeek> Workdays = new HashSet<DayOfWeek>
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

    public static ScheduleOptions Build(ScheduleModel model)
    {
        var schedule = new ScheduleOptions { TimeZone = string.IsNullOrWhiteSpace(model.TimeZone) ? null : model.TimeZone, MissedRuns = model.MissedRuns, Paused = model.Paused };
        switch (model.Mode)
        {
            case ScheduleMode.Daily:
                var days = model.Days.Count is 0 or 7 ? "?" : DaysToCron(model.Days);
                schedule.Cron = days == "?"
                    ? $"0 {model.At.Minutes} {model.At.Hours} * * ?"
                    : $"0 {model.At.Minutes} {model.At.Hours} ? * {days}";
                break;
            case ScheduleMode.Monthly:
                schedule.Cron = $"0 {model.At.Minutes} {model.At.Hours} {(model.DayOfMonth <= 0 ? "L" : model.DayOfMonth.ToString(CultureInfo.InvariantCulture))} * ?";
                break;
            case ScheduleMode.Every:
                var zone = Zone(model.TimeZone);
                var date = model.FromDate?.Date ?? TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
                var local = date + model.From;
                while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
                schedule.Interval = model.Every;
                schedule.StartAt = new DateTimeOffset(local, zone.GetUtcOffset(local));
                break;
            default:
                schedule.Cron = model.Cron?.Trim();
                break;
        }
        return schedule;
    }

    /// <summary>Recognizes the simple patterns created by <see cref="Build"/>; anything else becomes <see cref="ScheduleMode.Advanced"/> unchanged.</summary>
    public static ScheduleModel Parse(ScheduleOptions schedule)
    {
        var model = new ScheduleModel { TimeZone = schedule.TimeZone, MissedRuns = schedule.MissedRuns, Paused = schedule.Paused };
        if (schedule.Interval is { } interval)
        {
            model.Mode = ScheduleMode.Every;
            model.Every = interval;
            if (schedule.StartAt is { } start)
            {
                var local = TimeZoneInfo.ConvertTime(start, Zone(schedule.TimeZone));
                model.From = local.TimeOfDay;
                model.FromDate = local.Date;
            }
            return model;
        }

        var cron = schedule.Cron?.Trim() ?? string.Empty;
        model.Cron = cron;
        if (DailyPattern().Match(cron) is { Success: true } daily
            && TryTime(daily.Groups["h"].Value, daily.Groups["m"].Value, out var at)
            && TryDays(daily.Groups["dow"].Value, out var days)
            && (daily.Groups["dom"].Value == "*") == (daily.Groups["dow"].Value is "?" or "*"))
        {
            model.Mode = ScheduleMode.Daily;
            model.At = at;
            model.Days = days;
        }
        else if (MonthlyPattern().Match(cron) is { Success: true } monthly && TryTime(monthly.Groups["h"].Value, monthly.Groups["m"].Value, out var monthlyAt))
        {
            model.Mode = ScheduleMode.Monthly;
            model.At = monthlyAt;
            model.DayOfMonth = monthly.Groups["dom"].Value == "L" ? 0 : int.Parse(monthly.Groups["dom"].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            model.Mode = ScheduleMode.Advanced;
        }
        return model;
    }

    /// <summary>Schedule in words, e.g. "Monday to Friday at 02:00" / "Every 2 h from 12:00" (time zone appended when set).</summary>
    public static string Describe(ScheduleOptions schedule, IStringLocalizer<UiText> text)
    {
        var model = Parse(schedule);
        var culture = CultureInfo.CurrentCulture;
        var description = model.Mode switch
        {
            ScheduleMode.Daily when model.Days.Count is 0 or 7 => text["Sched_Daily", Time(model.At)].Value,
            ScheduleMode.Daily when model.Days.SetEquals(Workdays) => text["Sched_Workdays", Time(model.At)].Value,
            ScheduleMode.Daily => text["Sched_Days", string.Join(", ", WeekOrder.Where(model.Days.Contains).Select(d => culture.DateTimeFormat.GetAbbreviatedDayName(d))), Time(model.At)].Value,
            ScheduleMode.Monthly when model.DayOfMonth <= 0 => text["Sched_MonthlyLast", Time(model.At)].Value,
            ScheduleMode.Monthly => text["Sched_Monthly", model.DayOfMonth, Time(model.At)].Value,
            ScheduleMode.Every => text["Sched_Every", Period(model.Every), Time(model.From)].Value,
            _ => text["Sched_Cron", model.Cron ?? ""].Value
        };
        return schedule.TimeZone is null ? description : $"{description} ({ZoneName(schedule.TimeZone)})";
    }

    /// <summary>"15 min", "2 h", "1 h 30 min", "1 d".</summary>
    public static string Period(TimeSpan value) => value switch
    {
        { TotalDays: >= 1 } when value.Ticks % TimeSpan.TicksPerDay == 0 => $"{(int)value.TotalDays} d",
        { TotalHours: >= 1 } when value.Minutes == 0 && value.Seconds == 0 => $"{(int)value.TotalHours} h",
        { TotalHours: >= 1 } => $"{(int)value.TotalHours} h {value.Minutes} min",
        _ when value.Seconds == 0 => $"{(int)value.TotalMinutes} min",
        _ => value.ToString("c", CultureInfo.InvariantCulture)
    };

    public static string ZoneName(string? zoneId)
    {
        try
        {
            return string.IsNullOrWhiteSpace(zoneId) ? TimeZoneInfo.Local.DisplayName : TimeZoneInfo.FindSystemTimeZoneById(zoneId).DisplayName;
        }
        catch (TimeZoneNotFoundException)
        {
            return zoneId!;
        }
    }

    private static string Time(TimeSpan value) => $"{value.Hours:00}:{value.Minutes:00}";

    private static TimeZoneInfo Zone(string? id)
    {
        try
        {
            return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static string DaysToCron(IEnumerable<DayOfWeek> days)
    {
        var set = days.ToHashSet();
        if (set.SetEquals(Workdays)) return "MON-FRI";
        return string.Join(",", WeekOrder.Where(set.Contains).Select(d => CronDays[(int)d]));
    }

    private static bool TryTime(string hour, string minute, out TimeSpan time)
    {
        time = default;
        if (!int.TryParse(hour, out var h) || !int.TryParse(minute, out var m) || h is < 0 or > 23 || m is < 0 or > 59) return false;
        time = new TimeSpan(h, m, 0);
        return true;
    }

    /// <summary>"?" / "*" = every day; names, lists and ranges (MON-FRI, MON,WED,FRI, SAT-SUN).</summary>
    private static bool TryDays(string value, out HashSet<DayOfWeek> days)
    {
        days = [];
        if (value is "?" or "*") return true;
        foreach (var part in value.Split(','))
        {
            var range = part.Split('-');
            var start = Array.IndexOf(CronDays, range[0].ToUpperInvariant());
            var end = range.Length == 2 ? Array.IndexOf(CronDays, range[1].ToUpperInvariant()) : start;
            if (start < 0 || end < 0 || range.Length > 2) return false;
            for (var d = start; ; d = (d + 1) % 7)
            {
                days.Add((DayOfWeek)d);
                if (d == end) break;
            }
        }
        return true;
    }

    [GeneratedRegex(@"^0 (?<m>\d{1,2}) (?<h>\d{1,2}) (?<dom>\*|\?) \* (?<dow>\?|\*|[A-Za-z]{3}(?:[,-][A-Za-z]{3})*)$")]
    private static partial Regex DailyPattern();

    [GeneratedRegex(@"^0 (?<m>\d{1,2}) (?<h>\d{1,2}) (?<dom>[1-9]|[12]\d|3[01]|L) \* \?$")]
    private static partial Regex MonthlyPattern();
}
