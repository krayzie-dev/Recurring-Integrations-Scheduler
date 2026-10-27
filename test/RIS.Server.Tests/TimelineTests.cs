using Quartz;
using RIS.Core.Configuration;
using RIS.Jobs.Quartz;
using RIS.Server.Services;
using RIS.Server.Ui;

namespace RIS.Server.Tests;

[TestClass]
public sealed class TimelineTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");

    [TestMethod]
    public void Layout_MapsTimeToX_ClipsBars_AndKeepsMinimumWidth()
    {
        var from = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        var layout = new GanttLayout(from, from.AddDays(1), 1440, TimeZoneInfo.Utc);

        Assert.AreEqual(GanttLayout.LabelWidth, layout.X(from), 0.001);
        Assert.AreEqual(GanttLayout.LabelWidth + 720, layout.X(from.AddHours(12)), 0.001);

        var bar = layout.Bar(from.AddHours(-2), from.AddMinutes(30))!.Value;
        Assert.AreEqual(GanttLayout.LabelWidth, bar.X, 0.001, "Clipped at the window start.");
        Assert.AreEqual(30, bar.Width, 0.001);
        Assert.AreEqual(GanttLayout.MinBarWidth, layout.Bar(from.AddHours(1), from.AddHours(1))!.Value.Width, "Zero-length run is still visible.");
        Assert.IsNull(layout.Bar(from.AddDays(2), from.AddDays(2).AddHours(1)), "Outside the window.");
    }

    [TestMethod]
    public void Layout_DaysAndHours_FollowDaylightSavingTime()
    {
        // Week of 26.10.2026 in Warsaw starts at local midnight; 25.10 (Sunday) had 25 hours.
        var window = new TimelineWindow(TimelineRange.Week, new DateTime(2026, 10, 19), Warsaw);
        var layout = new GanttLayout(window.FromUtc, window.UntilUtc, 7 * 240, Warsaw);

        var days = layout.Days();
        Assert.AreEqual(7, days.Count);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 18, 22, 0, 0, TimeSpan.Zero), days[0], "Monday 00:00 CEST = 22:00 UTC.");
        Assert.AreEqual(TimeSpan.FromHours(24), days[6] - days[5], "Saturday has 24 hours.");
        Assert.AreEqual(TimeSpan.FromHours(25), window.UntilUtc - days[6], "Sunday 25.10 has 25 hours.");

        var hours = layout.Hours(1).Where(h => layout.Local(h).Date == new DateTime(2026, 10, 25)).ToList();
        Assert.AreEqual(24, hours.Count, "Local 02:00 occurs twice; one tick per local hour label.");
        Assert.IsTrue(hours.All(h => layout.Local(h).Minute == 0));
    }

    [TestMethod]
    public void Window_Week_StartsOnMonday_AndNavigates()
    {
        var window = new TimelineWindow(TimelineRange.Week, new DateTime(2026, 10, 26), Warsaw);
        Assert.AreEqual(new DateTime(2026, 10, 19), window.Previous().StartLocal);
        Assert.AreEqual(new DateTime(2026, 11, 2), window.Next().StartLocal);

        var day = new TimelineWindow(TimelineRange.Day, new DateTime(2026, 10, 29), Warsaw);
        Assert.AreEqual(new DateTime(2026, 10, 26), day.WithRange(TimelineRange.Week).StartLocal, "Week containing the day.");
        Assert.AreEqual(TimeSpan.FromHours(24), day.UntilUtc - day.FromUtc);
        Assert.AreEqual(TimeSpan.FromHours(25), new TimelineWindow(TimelineRange.Day, new DateTime(2026, 10, 25), Warsaw).UntilUtc
                                                 - new TimelineWindow(TimelineRange.Day, new DateTime(2026, 10, 25), Warsaw).FromUtc);
    }

    [TestMethod]
    public void Plan_FrequentRunsBecomeBand_DailyRunsStayBars()
    {
        var from = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        var every5 = Enumerable.Range(0, 12 * 24).Select(i => from.AddMinutes(5 * i)).ToList();

        var band = SchedulePlanService.BuildItems(every5, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30), paused: false);
        Assert.AreEqual(1, band.Count);
        Assert.AreEqual(TimelineItemKind.Band, band[0].Kind);
        Assert.AreEqual(288, band[0].Count);
        Assert.AreEqual("5 min 00 s", band[0].Label);

        var daily = Enumerable.Range(0, 7).Select(i => from.AddDays(i).AddHours(2)).ToList();
        var bars = SchedulePlanService.BuildItems(daily, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30), paused: true);
        Assert.AreEqual(7, bars.Count);
        Assert.IsTrue(bars.All(b => b.Kind == TimelineItemKind.PlannedPaused && b.EndUtc - b.StartUtc == TimeSpan.FromMinutes(20)));
    }

    [TestMethod]
    public void Plan_FireTimes_ComeFromTheJobTrigger()
    {
        var job = new JobDefinition
        {
            Name = "Every2h",
            Group = "Tests",
            Schedule = new ScheduleOptions { Interval = TimeSpan.FromHours(2), StartAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2)), TimeZone = Warsaw.Id },
            Options = new RIS.Core.Options.ExportJobOptions { Connection = "C", DataProject = "D", Company = "USMF", DownloadSuccessDir = @"C:\x" }
        };
        var window = new TimelineWindow(TimelineRange.Day, new DateTime(2026, 10, 27), Warsaw);
        var trigger = RisJobScheduling.BuildTrigger(job, window.FromUtc.AddDays(-1));

        var fires = SchedulePlanService.FireTimes(trigger, window.FromUtc, window.UntilUtc);

        Assert.AreEqual(12, fires.Count);
        CollectionAssert.AreEqual(Enumerable.Range(0, 12).Select(i => i * 2).ToList(), fires.Select(f => TimeZoneInfo.ConvertTime(f, Warsaw).Hour).ToList());
    }

    [TestMethod]
    public void Median_IsMiddleValue()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(20), JobQueryService.Median([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)]));
        Assert.AreEqual(TimeSpan.FromSeconds(15), JobQueryService.Median([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)]));
        Assert.AreEqual(TimeSpan.Zero, JobQueryService.Median([]));
    }
}
