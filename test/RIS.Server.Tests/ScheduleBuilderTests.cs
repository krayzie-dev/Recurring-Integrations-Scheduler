using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using RIS.Core.Configuration;
using RIS.Jobs.Quartz;
using RIS.Server.Resources;
using RIS.Server.Services;

namespace RIS.Server.Tests;

[TestClass]
public sealed class ScheduleBuilderTests
{
    private const string Warsaw = "Central European Standard Time";

    [TestMethod]
    [DataRow("0 0 2 * * ?")]
    [DataRow("0 30 23 ? * MON-FRI")]
    [DataRow("0 5 6 ? * MON,WED,FRI")]
    [DataRow("0 0 7 ? * SAT,SUN")]
    [DataRow("0 15 1 1 * ?")]
    [DataRow("0 0 22 L * ?")]
    public void SimpleCrons_RoundTrip(string cron)
    {
        var model = ScheduleBuilder.Parse(new ScheduleOptions { Cron = cron, TimeZone = Warsaw });
        Assert.AreNotEqual(ScheduleMode.Advanced, model.Mode, cron);
        var rebuilt = ScheduleBuilder.Build(model);
        Assert.AreEqual(cron, rebuilt.Cron);
        Assert.AreEqual(Warsaw, rebuilt.TimeZone);
    }

    [TestMethod]
    [DataRow("0 0/15 * * * ?")]
    [DataRow("0 0 2,14 * * ?")]
    [DataRow("0 0 8-18 ? * MON-FRI")]
    [DataRow("0 0 2 ? * 2-6")]
    public void OtherCrons_StayAdvancedAndUnchanged(string cron)
    {
        var model = ScheduleBuilder.Parse(new ScheduleOptions { Cron = cron });
        Assert.AreEqual(ScheduleMode.Advanced, model.Mode);
        Assert.AreEqual(cron, ScheduleBuilder.Build(model).Cron);
    }

    [TestMethod]
    public void Daily_WithDays_BuildsCron()
    {
        var model = new ScheduleModel { Mode = ScheduleMode.Daily, At = new TimeSpan(2, 30, 0), Days = [.. ScheduleBuilder.Workdays] };
        Assert.AreEqual("0 30 2 ? * MON-FRI", ScheduleBuilder.Build(model).Cron);
        model.Days = [DayOfWeek.Sunday, DayOfWeek.Saturday];
        Assert.AreEqual("0 30 2 ? * SAT,SUN", ScheduleBuilder.Build(model).Cron);
        model.Days = [];
        Assert.AreEqual("0 30 2 * * ?", ScheduleBuilder.Build(model).Cron);
    }

    [TestMethod]
    public void Every_BuildsIntervalWithStartPoint_InJobTimeZone_AndKeepsDateWhenEditing()
    {
        var model = new ScheduleModel { Mode = ScheduleMode.Every, Every = TimeSpan.FromHours(2), From = new TimeSpan(12, 0, 0), TimeZone = Warsaw, FromDate = new DateTime(2026, 10, 1) };
        var schedule = ScheduleBuilder.Build(model);
        Assert.IsNull(schedule.Cron);
        Assert.AreEqual(TimeSpan.FromHours(2), schedule.Interval);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2)), schedule.StartAt, "CEST on 1 October.");

        var parsed = ScheduleBuilder.Parse(schedule);
        Assert.AreEqual(ScheduleMode.Every, parsed.Mode);
        Assert.AreEqual(new TimeSpan(12, 0, 0), parsed.From);
        Assert.AreEqual(new DateTime(2026, 10, 1), parsed.FromDate);

        // Next runs from the trigger: 12:00, 14:00, ... in local time, also after the DST change on 25 October.
        var job = new JobDefinition { Name = "J", Schedule = schedule, Options = new RIS.Core.Options.ExportJobOptions() };
        var trigger = RisJobScheduling.BuildTrigger(job, new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));
        var next = trigger.GetFireTimeAfter(new DateTimeOffset(2026, 10, 26, 8, 30, 0, TimeSpan.Zero))!.Value;
        Assert.AreEqual(new DateTime(2026, 10, 26, 10, 0, 0), TimeZoneInfo.ConvertTime(next, TimeZoneInfo.FindSystemTimeZoneById(Warsaw)).DateTime);
    }

    [TestMethod]
    public void Describe_InPolishAndEnglish()
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var text = services.GetRequiredService<IStringLocalizer<UiText>>();
        string Describe(string culture, ScheduleOptions schedule)
        {
            var (c, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
            try { return ScheduleBuilder.Describe(schedule, text); }
            finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (c, ui); }
        }

        Assert.AreEqual("Od poniedziałku do piątku o 02:00", Describe("pl-PL", new ScheduleOptions { Cron = "0 0 2 ? * MON-FRI" }));
        Assert.AreEqual("Monday to Friday at 02:00", Describe("en-US", new ScheduleOptions { Cron = "0 0 2 ? * MON-FRI" }));
        Assert.AreEqual("Codziennie o 23:30", Describe("pl-PL", new ScheduleOptions { Cron = "0 30 23 * * ?" }));
        Assert.AreEqual("Ostatniego dnia każdego miesiąca o 22:00", Describe("pl-PL", new ScheduleOptions { Cron = "0 0 22 L * ?" }));
        Assert.AreEqual("Every 2 h from 12:00", Describe("en-US",
            new ScheduleOptions { Interval = TimeSpan.FromHours(2), StartAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 1))) }));
        Assert.AreEqual("Cron: 0 0/15 * * * ?", Describe("en-US", new ScheduleOptions { Cron = "0 0/15 * * * ?" }));
        StringAssert.StartsWith(Describe("en-US", new ScheduleOptions { Cron = "0 0 2 * * ?", TimeZone = Warsaw }), "Every day at 02:00 (");
        StringAssert.Contains(Describe("pl-PL", new ScheduleOptions { Cron = "0 0 7 ? * SAT,SUN" }), "o 07:00");
    }

    [TestMethod]
    [DataRow(15, "15 min")]
    [DataRow(120, "2 h")]
    [DataRow(90, "1 h 30 min")]
    [DataRow(1440, "1 d")]
    public void Period_IsCompact(int minutes, string expected) => Assert.AreEqual(expected, ScheduleBuilder.Period(TimeSpan.FromMinutes(minutes)));

    [TestMethod]
    public void Every_WithWindowAndDays_RoundTripsAndDescribes()
    {
        var model = new ScheduleModel
        {
            Mode = ScheduleMode.Every, Every = TimeSpan.FromMinutes(10), From = new TimeSpan(8, 0, 0), Window = true, WindowTo = new TimeSpan(16, 0, 0),
            Days = [.. ScheduleBuilder.Workdays], TimeZone = Warsaw
        };
        var schedule = ScheduleBuilder.Build(model);
        Assert.AreEqual(new TimeOnly(8, 0), schedule.ActiveFrom);
        Assert.AreEqual(new TimeOnly(16, 0), schedule.ActiveTo);
        CollectionAssert.AreEqual(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }, schedule.Days);

        var parsed = ScheduleBuilder.Parse(schedule);
        Assert.AreEqual(ScheduleMode.Every, parsed.Mode);
        Assert.IsTrue(parsed.Window);
        Assert.AreEqual(new TimeSpan(16, 0, 0), parsed.WindowTo);
        Assert.IsTrue(parsed.Days.SetEquals(ScheduleBuilder.Workdays));

        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var text = services.GetRequiredService<IStringLocalizer<UiText>>();
        var (c, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("pl-PL");
        try
        {
            StringAssert.StartsWith(ScheduleBuilder.Describe(schedule, text), "Co 10 min od 08:00 do 16:00, od poniedziałku do piątku");
            schedule.TimeZone = null;
            schedule.Days = [DayOfWeek.Saturday];
            Assert.AreEqual("Co 10 min od 08:00 do 16:00, w dni: sob.", ScheduleBuilder.Describe(schedule, text));
        }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (c, ui); }

        // Without the window the days alone still limit the interval schedule.
        model.Window = false;
        Assert.IsNull(ScheduleBuilder.Build(model).ActiveFrom);
    }
}
