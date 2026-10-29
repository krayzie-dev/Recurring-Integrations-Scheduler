using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Extensibility;
using RIS.Core.Configuration;
using RIS.Core.Options;
using RIS.Jobs.Quartz;

namespace RIS.Jobs.Tests;

[TestClass]
public sealed class QuartzIntegrationTests
{
    private static JobsTestHost CreateHost(IJobPauseRecorder? pauses = null) => new(services =>
    {
        services.AddQuartz(q => q.UseExecutionLogCapture(o => o.MaxLines = 200));
        services.AddQuartzExecutionHistory(o => o.RecordInput = true);
        if (pauses is not null) services.AddSingleton(pauses);
    });

    private sealed class RecordedPauses : IJobPauseRecorder
    {
        public List<string> Pauses { get; } = [];

        public Task RecordPauseAsync(string group, string name, string reason, string by, CancellationToken cancellationToken)
        {
            Pauses.Add($"{group}.{name}: {reason} ({by})");
            return Task.CompletedTask;
        }
    }

    private static async Task<ExecutionHistoryEntry> WaitForHistoryAsync(IServiceProvider services, IScheduler scheduler, string jobName)
    {
        var store = services.GetRequiredService<IExecutionHistoryStore>();
        for (var i = 0; i < 100; i++)
        {
            var page = await store.QueryExecutions(new ExecutionHistoryQuery { SchedulerName = scheduler.SchedulerName }, default);
            var entry = page.Items.FirstOrDefault(e => e.JobName == jobName);
            if (entry is not null)
            {
                return entry;
            }
            await Task.Delay(50);
        }
        throw new AssertFailedException("Job did not run.");
    }

    [TestMethod]
    public async Task ScheduledExport_RunsThroughQuartz_ReportAndLogAreInHistory_WithoutSecretsInInput()
    {
        using var host = CreateHost();
        var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await scheduler.Start();
        var definition = new JobDefinition
        {
            Name = "CustInvoiceJour",
            Group = "Export jobs",
            Schedule = new ScheduleOptions { Cron = "0 0 2 * * ?" },
            Options = new ExportJobOptions
            {
                Connection = "PROD",
                DataProject = "CustInvoiceJour",
                Company = "USMF",
                DownloadSuccessDir = @"C:\ris\export",
                SftpOutbound = new SftpTransferOptions { Server = "SFTP", RemoteFolder = "/out" }
            }
        };

        await RisJobScheduling.ScheduleAsync(scheduler, definition, default);
        await RisJobScheduling.TriggerOnceAsync(scheduler, definition, default);
        var entry = await WaitForHistoryAsync(host.Services, scheduler, "CustInvoiceJour");
        await scheduler.Shutdown(true);

        Assert.AreEqual(JobRunResult.Succeeded, entry.EffectiveResult, entry.Summary + entry.ExceptionMessage);
        StringAssert.StartsWith(entry.Summary, "Exported CustInvoiceJour");
        StringAssert.Contains(entry.MetricsJson, "ExecutionId");
        StringAssert.Contains(entry.Log, "Export finished with status Succeeded");
        StringAssert.Contains(entry.Input, "\"type\":\"Export\"");
        Assert.IsFalse(entry.Input!.Contains("Password", StringComparison.OrdinalIgnoreCase), "Job input must not contain secrets.");
        Assert.AreEqual(1, host.Sftp.Uploaded.Count);
    }

    [TestMethod]
    public async Task FailedRun_IsRecordedAsFailed_AndPauseOnErrorPausesJob()
    {
        var pauses = new RecordedPauses();
        using var host = CreateHost(pauses);
        host.D365.ExportStatus = () => "Failed";
        var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await scheduler.Start();
        var definition = new JobDefinition
        {
            Name = "Vendors",
            Group = "Export jobs",
            Schedule = new ScheduleOptions { Cron = "0 0 2 * * ?" },
            Options = new ExportJobOptions { Connection = "PROD", DataProject = "Vendors", Company = "USMF", DownloadSuccessDir = @"C:\ris\export", PauseOnError = true }
        };

        await RisJobScheduling.ScheduleAsync(scheduler, definition, default);
        await RisJobScheduling.TriggerOnceAsync(scheduler, definition, default);
        var entry = await WaitForHistoryAsync(host.Services, scheduler, "Vendors");
        var triggerState = await scheduler.GetTriggerState(new TriggerKey("Vendors trigger", "Export jobs"), default);
        await scheduler.Shutdown(true);

        Assert.AreEqual(JobRunResult.Failed, entry.EffectiveResult);
        StringAssert.StartsWith(entry.Summary, "[RIS-EXP-002]");
        Assert.AreEqual(TriggerState.Paused, triggerState);
        CollectionAssert.AreEqual(new[] { $"Export jobs.Vendors: {PauseReasons.Error} (RIS)" }, pauses.Pauses, "The pause is saved in the job configuration.");
    }

    [TestMethod]
    public void CronTrigger_WithTimeZone_IsBuilt()
    {
        var trigger = (ICronTrigger)RisJobScheduling.BuildTrigger(new JobDefinition
        {
            Name = "Daily",
            Schedule = new ScheduleOptions { Cron = "0 0 2 * * ?", TimeZone = "Central European Standard Time" },
            Options = new ExportJobOptions()
        }, DateTimeOffset.UtcNow);

        Assert.AreEqual("0 0 2 * * ?", trigger.CronExpressionString);
        Assert.AreEqual("Central European Standard Time", trigger.TimeZone.Id);
    }
}
