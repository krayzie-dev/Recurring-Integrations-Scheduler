using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Extensibility;
using RIS.Core.Configuration;
using RIS.Core.Options;
using RIS.Jobs.Quartz;

namespace RIS.Jobs.Tests;

/// <summary>
/// Interval schedules keep their rhythm across service restarts (4.x restarted the rhythm at the moment of restart and ran the job immediately).
/// </summary>
[TestClass]
public sealed class ScheduleTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(new DateTime(year, month, day, hour, minute, 0), Warsaw.GetUtcOffset(new DateTime(year, month, day, hour, minute, 0)));

    private static JobDefinition Every(TimeSpan interval, DateTimeOffset startAt, MissedRunPolicy missed = MissedRunPolicy.RunOnce) => new()
    {
        Name = "Job",
        Group = "Tests",
        Schedule = new ScheduleOptions { Interval = interval, StartAt = startAt, TimeZone = Warsaw.Id, MissedRuns = missed },
        Options = new ExportJobOptions()
    };

    private static DateTimeOffset FirstFire(ITrigger trigger) =>
        // First run relative to the trigger start (ComputeFirstFireTimeUtc would also apply the real current time).
        trigger.GetFireTimeAfter(trigger.StartTimeUtc.AddTicks(-1)) ?? throw new AssertFailedException("Trigger never fires.");

    private static IEnumerable<DateTimeOffset> Fires(ITrigger trigger, DateTimeOffset after, int count)
    {
        var current = after;
        for (var i = 0; i < count; i++)
        {
            current = trigger.GetFireTimeAfter(current) ?? throw new AssertFailedException("Trigger stopped.");
            yield return current;
        }
    }

    [TestMethod]
    public void Every2HoursFrom12_RestartAt1317_NextRunIs1400_NotImmediately()
    {
        var restart = Local(2026, 10, 27, 13, 17);
        var trigger = RisJobScheduling.BuildTrigger(Every(TimeSpan.FromHours(2), Local(2026, 10, 1, 12, 0)), restart);

        var first = TimeZoneInfo.ConvertTime(FirstFire(trigger), Warsaw);

        Assert.AreEqual(new DateTime(2026, 10, 27, 14, 0, 0), first.DateTime);
        CollectionAssert.AreEqual(new[] { 16, 18, 20 }, Fires(trigger, FirstFire(trigger), 3).Select(f => TimeZoneInfo.ConvertTime(f, Warsaw).Hour).ToArray());
    }

    [TestMethod]
    public void IntervalNotDividingDay_ContinuesRhythmFromStartAt()
    {
        // every 7 h from 12:00 on 26.10: 12:00, 19:00, 02:00 (27.10), 09:00, 16:00, 23:00 ... restart at 20:00 on 27.10 -> 23:00
        var trigger = RisJobScheduling.BuildTrigger(Every(TimeSpan.FromHours(7), Local(2026, 10, 26, 12, 0)), Local(2026, 10, 27, 20, 0));

        Assert.AreEqual(new DateTime(2026, 10, 27, 23, 0, 0), TimeZoneInfo.ConvertTime(FirstFire(trigger), Warsaw).DateTime);
    }

    [TestMethod]
    public void Every2Hours_KeepsWallClockHoursAcrossDaylightSavingChange()
    {
        // DST ends in Poland on 25.10.2026 03:00 -> 02:00
        var trigger = RisJobScheduling.BuildTrigger(Every(TimeSpan.FromHours(2), Local(2026, 10, 1, 12, 0)), Local(2026, 10, 24, 12, 0));

        var hours = Fires(trigger, Local(2026, 10, 24, 21, 0), 14).Select(f => TimeZoneInfo.ConvertTime(f, Warsaw)).ToList();

        Assert.IsTrue(hours.All(h => h.Hour % 2 == 0 && h.Minute == 0), string.Join(", ", hours));
        Assert.IsTrue(hours.Any(h => h.Date == new DateTime(2026, 10, 25)));
    }

    [TestMethod]
    public void Every15MinutesFrom1205_IsAlignedToFiveMinutesPastQuarter()
    {
        var trigger = RisJobScheduling.BuildTrigger(Every(TimeSpan.FromMinutes(15), Local(2026, 10, 1, 12, 5)), Local(2026, 10, 27, 8, 0));

        CollectionAssert.AreEqual(new[] { 5, 20, 35, 50 },
            new[] { FirstFire(trigger) }.Concat(Fires(trigger, FirstFire(trigger), 3)).Select(f => TimeZoneInfo.ConvertTime(f, Warsaw).Minute).ToArray());
    }

    [TestMethod]
    public void MissedRunPolicy_MapsToMisfireInstruction()
    {
        var now = Local(2026, 10, 27, 13, 17);
        Assert.AreEqual(CronTriggerMisfireInstruction.FireAndProceed,
            ((ICronTrigger)RisJobScheduling.BuildTrigger(Every(TimeSpan.FromHours(2), now), now)).MisfireInstruction);
        Assert.AreEqual(DailyTimeIntervalTriggerMisfireInstruction.FireAndProceed,
            ((IDailyTimeIntervalTrigger)RisJobScheduling.BuildTrigger(Every(TimeSpan.FromMinutes(90), now), now)).MisfireInstruction);
        Assert.AreEqual(DailyTimeIntervalTriggerMisfireInstruction.DoNothing,
            ((IDailyTimeIntervalTrigger)RisJobScheduling.BuildTrigger(Every(TimeSpan.FromMinutes(90), now, MissedRunPolicy.Skip), now)).MisfireInstruction);
        Assert.AreEqual(CalendarIntervalTriggerMisfireInstruction.DoNothing,
            ((ICalendarIntervalTrigger)RisJobScheduling.BuildTrigger(Every(TimeSpan.FromHours(7), now, MissedRunPolicy.Skip), now)).MisfireInstruction);

        var cron = new JobDefinition { Name = "C", Schedule = new ScheduleOptions { Cron = "0 0 2 * * ?", MissedRuns = MissedRunPolicy.Skip }, Options = new ExportJobOptions() };
        Assert.AreEqual(CronTriggerMisfireInstruction.DoNothing, ((ICronTrigger)RisJobScheduling.BuildTrigger(cron, now)).MisfireInstruction);
    }

    [TestMethod]
    [DataRow(15, 12, 5, "0 5/15 * * * ?")]
    [DataRow(60, 12, 30, "0 30 * * * ?")]
    [DataRow(120, 12, 0, "0 0 0/2 * * ?")]
    [DataRow(360, 13, 45, "0 45 1/6 * * ?")]
    [DataRow(90, 12, 0, null)]
    [DataRow(420, 12, 0, null)]
    public void IntervalAsCron(int intervalMinutes, int anchorHour, int anchorMinute, string? expected) =>
        Assert.AreEqual(expected, RisJobScheduling.IntervalAsCron(TimeSpan.FromMinutes(intervalMinutes), new TimeSpan(anchorHour, anchorMinute, 0)));

    [TestMethod]
    public void Every90Minutes_UsesDailyIntervalAlignedToStartAt()
    {
        var trigger = RisJobScheduling.BuildTrigger(Every(TimeSpan.FromMinutes(90), Local(2026, 10, 1, 12, 0)), Local(2026, 10, 27, 13, 0));

        Assert.AreEqual(new DateTime(2026, 10, 27, 13, 30, 0), TimeZoneInfo.ConvertTime(FirstFire(trigger), Warsaw).DateTime);
    }

    [TestMethod]    [DataRow(13, 17, 14, 0)]
    [DataRow(14, 0, 14, 0)]
    [DataRow(23, 59, 0, 0)]
    public void NextSlot_IsFirstSlotNotBeforeNow(int nowHour, int nowMinute, int expectedHour, int expectedMinute)
    {
        var anchor = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 10, 27, nowHour, nowMinute, 0, TimeSpan.Zero);

        var slot = RisJobScheduling.NextSlot(anchor, TimeSpan.FromHours(2), now);

        Assert.AreEqual(expectedHour, slot.Hour);
        Assert.AreEqual(expectedMinute, slot.Minute);
        Assert.IsTrue(slot >= now);
    }

    [TestMethod]
    public async Task ScheduledInRealScheduler_DoesNotRunImmediately_AndNextRunIsOnTheRhythm()
    {
        using var host = new JobsTestHost(services => services.AddQuartz());
        var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await scheduler.Start();
        var definition = Every(TimeSpan.FromHours(2), Local(2026, 10, 1, 12, 0));

        // Schedule twice = configuration loaded at two service starts.
        await RisJobScheduling.ScheduleAsync(scheduler, definition, default);
        await RisJobScheduling.ScheduleAsync(scheduler, definition, default);
        var next = (await scheduler.GetTrigger(new TriggerKey("Job trigger", "Tests"), default))!.NextFireTimeUtc!.Value;
        await scheduler.Shutdown(false);

        var local = TimeZoneInfo.ConvertTime(next, Warsaw);
        Assert.IsTrue(next > DateTimeOffset.UtcNow.AddSeconds(1), "Job must not run right after (re)scheduling.");
        Assert.AreEqual(0, local.Hour % 2, local.ToString());
        Assert.AreEqual(0, local.Minute);
        Assert.IsTrue(next - DateTimeOffset.UtcNow <= TimeSpan.FromHours(2));
    }
}
