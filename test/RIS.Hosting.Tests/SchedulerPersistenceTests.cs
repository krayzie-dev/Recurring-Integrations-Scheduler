using System.Diagnostics;
using Quartz;
using RIS.Core.Configuration;
using RIS.Jobs.Quartz;

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
    public async Task OperatorPause_IsKept_AfterRestartAndAfterDefinitionChange()
    {
        await using (var first = await StartAsync())
        {
            await first.Repository.SaveConnectionAsync(RisTestHost.Connection, "test");
            await first.Repository.SaveJobAsync(RisTestHost.Export("Daily", _downloadDirectory, new ScheduleOptions { Cron = "0 0 2 * * ?" }), "test");
            await first.Synchronizer.SynchronizeAsync(default);
            await (await first.SchedulerAsync()).PauseJob(new JobKey("Daily", "Tests"));
        }

        await using var second = await StartAsync();
        var scheduler = await second.SchedulerAsync();
        Assert.AreEqual(TriggerState.Paused, await scheduler.GetTriggerState(DailyTrigger));

        await second.Repository.SaveJobAsync(RisTestHost.Export("Daily", _downloadDirectory, new ScheduleOptions { Cron = "0 0 4 * * ?" }), "test");
        await second.Synchronizer.SynchronizeAsync(default);
        Assert.AreEqual(TriggerState.Paused, await scheduler.GetTriggerState(DailyTrigger));
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
