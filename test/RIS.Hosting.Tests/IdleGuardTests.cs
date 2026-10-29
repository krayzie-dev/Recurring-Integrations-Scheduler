using Quartz;
using RIS.Core.Configuration;

namespace RIS.Hosting.Tests;

/// <summary>Idle guard rule on the cases seen on TEST (29.10.2026).</summary>
[TestClass]
public sealed class IdleGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = Now.AddDays(-30);
    private static readonly IdleGuardSettings Both = new() { PauseFailing = true, PauseIdle = true };

    private static JobDefinition Every(TimeSpan interval, string name = "Job") =>
        RisTestHost.Export(name, @"C:\ris\out", new ScheduleOptions { Interval = interval, StartAt = LongAgo });

    private static JobDefinition Cron(string cron) => RisTestHost.Export("Cron", @"C:\ris\out", new ScheduleOptions { Cron = cron, TimeZone = "UTC" });

    /// <summary>A job that ran the whole window: its last result and runs as given.</summary>
    private static IdleGuardInput Input(JobDefinition job, JobRunResult last, DateTimeOffset? lastNotFailed, int failuresInRow, DateTimeOffset? lastWork,
        DateTimeOffset? changed = null, bool running = false) =>
        new(job, changed ?? LongAgo, running, last, LongAgo, Now.AddMinutes(-3), lastNotFailed, failuresInRow, lastWork);

    [TestMethod]
    public void EveryThreeMinutes_OnlySkipped_ForAWeek_IsPausedAsNoWork()
    {
        // TETA_Payroll: 363 runs in history, all "Skipped - no files".
        var finding = IdleGuardRule.Evaluate(Input(Every(TimeSpan.FromMinutes(3)), JobRunResult.Skipped, Now.AddMinutes(-3), 0, lastWork: null),
            Both, Now, trackingSinceUtc: LongAgo);

        Assert.IsNotNull(finding);
        Assert.AreEqual(IdleGuardCase.NoWork, finding.Case);
        Assert.AreEqual(3360, finding.PlannedRuns);
        Assert.AreEqual(PauseReasons.IdleNoWork, finding.Reason);
    }

    [TestMethod]
    public void MonitorFailingEveryThirtyMinutes_IsPausedAsFailing()
    {
        // Ecom_IDG monitor: executions unknown to D365, every run failed.
        var finding = IdleGuardRule.Evaluate(Input(Every(TimeSpan.FromMinutes(30)), JobRunResult.Failed, null, 336, lastWork: null),
            new IdleGuardSettings { PauseFailing = true }, Now, LongAgo);

        Assert.AreEqual(IdleGuardCase.Failing, finding?.Case);
        Assert.AreEqual(PauseReasons.IdleFailing, finding!.Reason);
    }

    [TestMethod]
    public void DailyAndWeeklyJobs_AreNeverPaused()
    {
        foreach (var cron in new[] { "0 0 2 * * ?", "0 0 2 ? * MON" })
        {
            Assert.IsNull(IdleGuardRule.Evaluate(Input(Cron(cron), JobRunResult.Failed, null, 50, null), Both, Now, LongAgo), cron);
        }
    }

    [TestMethod]
    public void HourlyInOfficeHours_IsChecked_ButOnlyWithEnoughPlannedRuns()
    {
        var office = Cron("0 0 8-16 ? * MON-FRI");
        Assert.AreEqual(45, IdleGuardRule.PlannedRuns(office, Now.AddDays(-7), Now));
        Assert.IsNotNull(IdleGuardRule.Evaluate(Input(office, JobRunResult.Skipped, Now.AddDays(-1), 0, null), Both, Now, LongAgo));
        Assert.IsNull(IdleGuardRule.Evaluate(Input(office, JobRunResult.Skipped, Now.AddDays(-1), 0, null),
            new IdleGuardSettings { PauseIdle = true, MinPlannedRuns = 50 }, Now, LongAgo), "Fewer planned runs than required.");
    }

    [TestMethod]
    public void WorkDoneInTheWindow_OrOneRunThatDidNotFail_KeepsTheJobRunning()
    {
        var job = Every(TimeSpan.FromMinutes(5));
        Assert.IsNull(IdleGuardRule.Evaluate(Input(job, JobRunResult.Skipped, Now, 0, lastWork: Now.AddDays(-6)), Both, Now, LongAgo), "Work 6 days ago.");
        Assert.IsNull(IdleGuardRule.Evaluate(Input(job, JobRunResult.Failed, Now.AddDays(-2), 500, lastWork: Now.AddDays(-2)),
            new IdleGuardSettings { PauseFailing = true }, Now, LongAgo), "A non-failed run 2 days ago: not 'only failures'.");
    }

    [TestMethod]
    public void Exclusions_PausedChangedRunningOptedOutOrNoData()
    {
        var job = Every(TimeSpan.FromMinutes(5));
        var idle = Input(job, JobRunResult.Skipped, Now, 0, null);
        Assert.IsNotNull(IdleGuardRule.Evaluate(idle, Both, Now, LongAgo));

        Assert.IsNull(IdleGuardRule.Evaluate(idle with { ChangedUtc = Now.AddDays(-2) }, Both, Now, LongAgo), "Changed or resumed in the window.");
        Assert.IsNull(IdleGuardRule.Evaluate(idle with { Running = true }, Both, Now, LongAgo), "Running now.");
        Assert.IsNull(IdleGuardRule.Evaluate(idle with { FirstFiredUtc = Now.AddDays(-3) }, Both, Now, LongAgo), "Fires only since 3 days.");
        Assert.IsNull(IdleGuardRule.Evaluate(idle, Both, Now, trackingSinceUtc: Now.AddDays(-2)), "Activity recorded only since the upgrade 2 days ago.");
        Assert.IsNull(IdleGuardRule.Evaluate(idle, new IdleGuardSettings(), Now, LongAgo), "Guard off.");

        var optedOut = Every(TimeSpan.FromMinutes(5));
        optedOut.Options!.NeverAutoPause = true;
        Assert.IsNull(IdleGuardRule.Evaluate(idle with { Job = optedOut }, Both, Now, LongAgo), "Never pause automatically.");

        var paused = Every(TimeSpan.FromMinutes(5));
        paused.Schedule.SetPaused(true, "maintenance");
        Assert.IsNull(IdleGuardRule.Evaluate(idle with { Job = paused }, Both, Now, LongAgo), "Already paused.");
    }

    [TestMethod]
    public void FewFailuresInARow_AreNoProofOfAFailingWeek()
    {
        // Service was down most of the week: only 10 failures recorded for 336 planned runs.
        Assert.IsNull(IdleGuardRule.Evaluate(Input(Every(TimeSpan.FromMinutes(30)), JobRunResult.Failed, null, 10, null),
            new IdleGuardSettings { PauseFailing = true }, Now, LongAgo));
    }
}
