using Microsoft.VisualStudio.TestTools.UnitTesting;
using Quartz;
using Quartz.Impl;
using Quartz.Simpl;
using Quartz.Xml;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace RecurringIntegrationsScheduler.Scheduler.Tests
{
    /// <summary>
    /// Interval (simple trigger) jobs must keep their start time in Schedule.xml. Before 4.2.5 start-time was not saved,
    /// so after every service restart the job ran immediately and its rhythm moved to the restart time.
    /// </summary>
    [TestClass]
    public class ScheduleStartTimeTests
    {
        public class CountingJob : IJob
        {
            public static int Executions;

            public Task Execute(IJobExecutionContext context)
            {
                Interlocked.Increment(ref Executions);
                return Task.CompletedTask;
            }
        }

        [TestMethod]
        public async Task SavedIntervalJob_AfterReload_DoesNotRunImmediately_AndKeepsRhythm()
        {
            var startAt = new DateTimeOffset(DateTime.Today.AddDays(-1).AddHours(12)); // yesterday 12:00 local, every 2 hours
            var job = JobBuilder.Create<CountingJob>().WithIdentity("Every2h", "Export jobs").StoreDurably().Build();
            var trigger = (ITrigger)TriggerBuilder.Create()
                .WithIdentity("Every2h trigger", "Export jobs")
                .ForJob(job)
                .WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromHours(2)).RepeatForever())
                .StartAt(startAt)
                .Build();

            var file = new FileInfo(Path.Combine(Path.GetTempPath(), $"ris-schedule-{Guid.NewGuid():N}.xml"));
            Type.GetType("RecurringIntegrationsScheduler.Scheduler, RecurringIntegrationsScheduler", throwOnError: true)
                .GetMethod("WriteToFile", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { file, new List<IJobDetail> { job }, new List<ITrigger> { trigger } });

            StringAssert.Contains(File.ReadAllText(file.FullName), "<start-time>", "Schedule.xml must contain start-time.");

            // Same as service start: load Schedule.xml into a new scheduler.
            CountingJob.Executions = 0;
            var scheduler = await new StdSchedulerFactory(new NameValueCollection
            {
                ["quartz.scheduler.instanceName"] = "StartTimeTests",
                ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz",
                ["quartz.threadPool.threadCount"] = "1"
            }).GetScheduler();
            try
            {
                var typeLoadHelper = new SimpleTypeLoadHelper();
                typeLoadHelper.Initialize();
                await new XMLSchedulingDataProcessor(typeLoadHelper).ProcessFileAndScheduleJobs(file.FullName, scheduler);
                await scheduler.Start();
                await Task.Delay(TimeSpan.FromSeconds(3));

                var loaded = await scheduler.GetTrigger(new TriggerKey("Every2h trigger", "Export jobs"));
                var next = loaded.GetNextFireTimeUtc().Value.ToLocalTime();

                Assert.AreEqual(startAt.UtcDateTime, loaded.StartTimeUtc.UtcDateTime, "Start time is preserved.");
                Assert.AreEqual(0, CountingJob.Executions, "Job must not run right after restart.");
                Assert.IsTrue(next > DateTimeOffset.Now, next.ToString());
                Assert.AreEqual(0, next.Hour % 2, $"Next run {next} must stay on the 12:00 + 2 h rhythm.");
                Assert.AreEqual(0, next.Minute);
            }
            finally
            {
                await scheduler.Shutdown(false);
                file.Delete();
            }
        }
    }
}
