using System.Diagnostics;
using Quartz;
using RIS.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RIS.Jobs.Quartz;
using RIS.Persistence;

namespace RIS.Hosting.Tests;

[TestClass]
public sealed class SchedulerPersistenceTests
{
    private string _dataDirectory = null!;
    private string _downloadDirectory = null!;
    private readonly FakeD365 _d365 = new();

    [TestInitialize]
    public void Initialize()
    {
        _dataDirectory = Directory.CreateTempSubdirectory("ris-host-").FullName;
        _downloadDirectory = Path.Combine(_dataDirectory, "export");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { }
    }

    private async Task<RisTestHost> StartAsync()
    {
        var host = new RisTestHost(_dataDirectory, _d365);
        await host.Host.StartAsync();
        return host;
    }

    private static readonly TriggerKey DailyTrigger = new("Daily trigger", "Tests");

    [TestMethod]
    public async Task RestartWithoutChanges_KeepsTrigger_AndHistory()
    {
        DateTimeOffset? next;
        await using (var first = await StartAsync())
        {
            await first.Repository.SaveConnectionAsync(RisTestHost.Connection, "test");
            await first.Repository.SaveJobAsync(RisTestHost.Export("Daily", _downloadDirectory, new ScheduleOptions { Cron = "0 0 2 * * ?" }), "test");
            await first.Synchronizer.SynchronizeAsync(default);
            Assert.IsNotNull(await (await first.SchedulerAsync()).GetJobDetail(new JobKey("Daily", "Tests")), "Saved job is added to the scheduler.");

            var scheduler = await first.SchedulerAsync();
            await RisJobScheduling.TriggerOnceAsync(scheduler, first.Repository.LoadAsync().Result.FindJob("Daily")!, default);
            await WaitForAsync(async () => (await first.HistoryAsync("Daily")).Count == 1);
            next = (await scheduler.GetTrigger(DailyTrigger))!.NextFireTimeUtc;
        }

        await using var second = await StartAsync();
        Assert.AreEqual(new SyncResult(0, 0, 0, 1), second.Synchronizer.LastResult, $"Unchanged job must not be rescheduled after restart. Actual: {second.Synchronizer.LastResult}");
        Assert.AreEqual(next, (await (await second.SchedulerAsync()).GetTrigger(DailyTrigger))!.NextFireTimeUtc);
        Assert.AreEqual(1, (await second.HistoryAsync("Daily")).Count, "Execution history survives restart.");
        Assert.AreEqual(1, Directory.GetFiles(_dataDirectory, "*.db").Count(f => f.EndsWith("quartz.db")));
    }

    [TestMethod]
    public async Task ChangedAndRemovedJobs_AreAppliedToScheduler()
    {
        await using var host = await StartAsync();
        await host.Repository.SaveConnectionAsync(RisTestHost.Connection, "test");
        await host.Repository.SaveJobAsync(RisTestHost.Export("Daily", _downloadDirectory, new ScheduleOptions { Cron = "0 0 2 * * ?" }), "test");
        await host.Synchronizer.SynchronizeAsync(default);

        await host.Repository.SaveJobAsync(RisTestHost.Export("Daily", _downloadDirectory, new ScheduleOptions { Cron = "0 30 3 * * ?" }), "test");
        await host.Synchronizer.SynchronizeAsync(default); // change is also applied in the background after saving
        Assert.AreEqual("0 30 3 * * ?", ((ICronTrigger)(await (await host.SchedulerAsync()).GetTrigger(DailyTrigger))!).CronExpressionString);

        await host.Repository.DeleteJobAsync("Tests", "Daily", "test");
        await host.Synchronizer.SynchronizeAsync(default);
        Assert.IsNull(await (await host.SchedulerAsync()).GetJobDetail(new JobKey("Daily", "Tests")));
    }

    [TestMethod]
    public async Task OperatorPause_IsKept_AfterRestartAndAfterDefinitionChange_AndResumeRuns()
    {
        DateTimeOffset? next;
        await using (var first = await StartAsync())
        {
            await first.Repository.SaveConnectionAsync(RisTestHost.Connection, "test");
            await first.Repository.SaveJobAsync(RisTestHost.Export("Daily", _downloadDirectory, new ScheduleOptions { Cron = "0 0 2 * * ?" }), "test");
            await first.Synchronizer.SynchronizeAsync(default);
            next = (await (await first.SchedulerAsync()).GetTrigger(DailyTrigger))!.NextFireTimeUtc;

            await first.Repository.SetPausedAsync([("Tests", "Daily")], true, "D365 maintenance", "operator");
            await first.Synchronizer.SynchronizeAsync(default);
            var scheduler = await first.SchedulerAsync();
            Assert.AreEqual(TriggerState.Paused, await scheduler.GetTriggerState(DailyTrigger));
            Assert.AreEqual(new SyncResult(0, 0, 0, 1), first.Synchronizer.LastResult, "A pause is applied to the trigger, the job is not rescheduled.");
            Assert.AreEqual("D365 maintenance", (await scheduler.GetTriggerPause(DailyTrigger))?.Reason);
        }

        await using var second = await StartAsync();
        var restarted = await second.SchedulerAsync();
        Assert.AreEqual(TriggerState.Paused, await restarted.GetTriggerState(DailyTrigger));

        // The job form sends only the switch: the pause and its reason stay.
        await second.Repository.SaveJobAsync(RisTestHost.Export("Daily", _downloadDirectory, new ScheduleOptions { Cron = "0 0 4 * * ?", Paused = true }), "editor");
        await second.Synchronizer.SynchronizeAsync(default);
        Assert.AreEqual(TriggerState.Paused, await restarted.GetTriggerState(DailyTrigger));
        var saved = (await second.Repository.LoadAsync()).FindJob("Daily")!.Schedule;
        Assert.AreEqual("D365 maintenance", saved.PauseReason);
        Assert.AreEqual("operator", saved.PausedBy);

        await second.Repository.SetPausedAsync([("Tests", "Daily")], false, null, "operator");
        await second.Synchronizer.SynchronizeAsync(default);
        Assert.AreEqual(TriggerState.Normal, await restarted.GetTriggerState(DailyTrigger));
        Assert.IsNull((await second.Repository.LoadAsync()).FindJob("Daily")!.Schedule.PauseReason);
        var audit = await second.Repository.GetAuditAsync();
        Assert.IsTrue(audit.Any(a => a.Action == "Paused" && a.User == "operator") && audit.Any(a => a.Action == "Resumed"), "Pause and resume are audited.");
        Assert.IsNotNull(next);
    }

    [TestMethod]
    public async Task Upgrade_TakesPauseStateFromScheduler_Once()
    {
        await using (var first = await StartAsync())
        {
            await first.Repository.SaveConnectionAsync(RisTestHost.Connection, "test");
            // Imported from 4.x as paused, then resumed from the list (before one pause state: only the trigger changed).
            await first.Repository.SaveJobAsync(RisTestHost.Export("Imported", _downloadDirectory, new ScheduleOptions { Cron = "0 0 2 * * ?", Paused = true }), "import");
            // Running in the configuration, paused from the list.
            await first.Repository.SaveJobAsync(RisTestHost.Export("Daily", _downloadDirectory, new ScheduleOptions { Cron = "0 0 3 * * ?" }), "test");
            await first.Synchronizer.SynchronizeAsync(default);
            var scheduler = await first.SchedulerAsync();
            await scheduler.ResumeJob(new JobKey("Imported", "Tests"));
            await scheduler.PauseJobWith(new JobKey("Daily", "Tests"), new PauseDetails { Reason = "waiting for D365", RequestedBy = @"MEDNET\operator" });
        }

        File.Delete(Path.Combine(_dataDirectory, ScheduleSynchronizer.PauseAdoptionMarker)); // as before the upgrade

        await using var second = await StartAsync();
        var configuration = await second.Repository.LoadAsync();
        Assert.IsFalse(configuration.FindJob("Imported")!.Schedule.Paused, "Resumed from the list: stays resumed.");
        var daily = configuration.FindJob("Daily")!.Schedule;
        Assert.IsTrue(daily.Paused, "Paused from the list: stays paused.");
        Assert.AreEqual("waiting for D365", daily.PauseReason);
        Assert.AreEqual(@"MEDNET\operator", daily.PausedBy);
        var restarted = await second.SchedulerAsync();
        Assert.AreEqual(TriggerState.Normal, await restarted.GetTriggerState(new TriggerKey("Imported trigger", "Tests")));
        Assert.AreEqual(TriggerState.Paused, await restarted.GetTriggerState(DailyTrigger));
        Assert.IsTrue(File.Exists(Path.Combine(_dataDirectory, ScheduleSynchronizer.PauseAdoptionMarker)));

        // Once: from now on the configuration decides, a pause made only in the scheduler is undone.
        await restarted.PauseJob(new JobKey("Imported", "Tests"));
        await second.Synchronizer.SynchronizeAsync(default);
        Assert.AreEqual(TriggerState.Normal, await restarted.GetTriggerState(new TriggerKey("Imported trigger", "Tests")));
    }

    [TestMethod]
    public async Task ChangeMadeByAnotherProcess_IsPickedUp()
    {
        await using var host = await StartAsync();

        // Same data directory, separate service provider = another process (ris db import).
        await using (var other = new ServiceCollection()
                         .AddLogging()
                         .AddRisPersistence(new PersistenceOptions { DataDirectory = _dataDirectory })
                         .BuildServiceProvider())
        {
            var repository = other.GetRequiredService<ConfigurationRepository>();
            await repository.ImportAsync(new RisConfiguration
            {
                Connections = [RisTestHost.Connection],
                Jobs = [RisTestHost.Export("External", _downloadDirectory, new ScheduleOptions { Cron = "0 0 2 * * ?" })]
            }, "other process");
        }

        await WaitForAsync(async () => await (await host.SchedulerAsync()).GetJobDetail(new JobKey("External", "Tests")) is not null);
    }

    [TestMethod]
    public async Task ServiceStop_InterruptsWaitingJob_WithinSeconds()
    {
        _d365.NeverFinish = true;
        var host = await StartAsync();
        await host.Repository.SaveConnectionAsync(RisTestHost.Connection, "test");
        await host.Repository.SaveJobAsync(RisTestHost.Export("Waiting", _downloadDirectory, new ScheduleOptions { Cron = "0 0 2 * * ?" }), "test");
        await host.Synchronizer.SynchronizeAsync(default);
        await RisJobScheduling.TriggerOnceAsync(await host.SchedulerAsync(), (await host.Repository.LoadAsync()).FindJob("Waiting")!, default);
        var running = (RunningJobs)host.Host.Services.GetService(typeof(RunningJobs))!;
        await WaitForAsync(() => Task.FromResult(running.Current.Count == 1));

        var stopwatch = Stopwatch.StartNew();
        await host.DisposeAsync();

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Stop took {stopwatch.Elapsed}.");
        Assert.AreEqual(0, running.Current.Count);
    }

    /// <summary>
    /// Real downtime of ~2 minutes with jobs running every minute: RunOnce runs exactly once right after start, Skip does not.
    /// Takes about 3.5 minutes.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(420_000)]
    public async Task MissedRunsDuringDowntime_RunOnceRunsOnce_SkipWaits()
    {
        var anchor = DateTimeOffset.UtcNow.Date;
        await using (var first = await StartAsync())
        {
            await first.Repository.SaveConnectionAsync(RisTestHost.Connection, "test");
            await first.Repository.SaveJobAsync(RisTestHost.Export("CatchUp", _downloadDirectory,
                new ScheduleOptions { Interval = TimeSpan.FromMinutes(1), StartAt = anchor, MissedRuns = MissedRunPolicy.RunOnce }), "test");
            await first.Repository.SaveJobAsync(RisTestHost.Export("NoCatchUp", _downloadDirectory,
                new ScheduleOptions { Interval = TimeSpan.FromMinutes(1), StartAt = anchor, MissedRuns = MissedRunPolicy.Skip }), "test");
            await first.Synchronizer.SynchronizeAsync(default);
        }

        // Downtime covering two scheduled minutes; restart 5 s after a minute boundary.
        await Task.Delay(TimeSpan.FromSeconds(125));
        await DelayUntilSecondAsync(5);

        await using var second = await StartAsync();
        var restartedAt = DateTimeOffset.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(20));

        var catchUp = (await second.HistoryAsync("CatchUp")).Count(e => e.FiredAtUtc >= restartedAt.AddSeconds(-2));
        var noCatchUp = (await second.HistoryAsync("NoCatchUp")).Count(e => e.FiredAtUtc >= restartedAt.AddSeconds(-2));
        Assert.AreEqual(1, catchUp, "RunOnce: exactly one catch-up run after the downtime.");
        Assert.AreEqual(0, noCatchUp, "Skip: missed runs are skipped.");
    }

    private static async Task DelayUntilSecondAsync(int second)
    {
        var now = DateTimeOffset.UtcNow;
        var target = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, second, TimeSpan.Zero);
        if (target <= now) target = target.AddMinutes(1);
        await Task.Delay(target - now);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new AssertFailedException("Condition not met in time.");
    }
}
