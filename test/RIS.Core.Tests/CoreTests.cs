using System.Text.Json;
using RIS.Core.Configuration;
using RIS.Core.Contracts;
using RIS.Core.Options;
using RIS.Core.Polling;
using RIS.Core.Statuses;

namespace RIS.Core.Tests;

[TestClass]
public class StatusTests
{
    [TestMethod]
    [DataRow("Succeeded", DmfExecutionStatus.Succeeded)]
    [DataRow("succeeded", DmfExecutionStatus.Succeeded)]
    [DataRow(" NotRun ", DmfExecutionStatus.NotRun)]
    [DataRow("Bad request", DmfExecutionStatus.NotRun)]
    [DataRow("PartiallySucceeded", DmfExecutionStatus.PartiallySucceeded)]
    [DataRow("Cancelling", DmfExecutionStatus.Unrecognized)]
    [DataRow("", DmfExecutionStatus.Unrecognized)]
    [DataRow(null, DmfExecutionStatus.Unrecognized)]
    public void DmfStatus_Parse(string? text, DmfExecutionStatus expected) =>
        Assert.AreEqual(expected, DmfExecutionStatusExtensions.Parse(text));

    [TestMethod]
    public void DmfStatus_Classification()
    {
        Assert.IsTrue(DmfExecutionStatus.NotRun.IsInProgress());
        Assert.IsTrue(DmfExecutionStatus.Executing.IsInProgress());
        Assert.IsFalse(DmfExecutionStatus.Unrecognized.IsInProgress());
        Assert.IsTrue(DmfExecutionStatus.PartiallySucceeded.IsSuccess(partialIsSuccess: true));
        Assert.IsFalse(DmfExecutionStatus.PartiallySucceeded.IsSuccess(partialIsSuccess: false));
    }

    [TestMethod]
    [DataRow("Processed", DataJobState.Processed)]
    [DataRow("PostProcessingError", DataJobState.PostProcessError)]
    [DataRow("Processing", DataJobState.InProcess)]
    [DataRow("6", DataJobState.ProcessedWithErrors)]
    [DataRow("SomethingNew", DataJobState.Unrecognized)]
    public void DataJobState_Parse(string text, DataJobState expected) =>
        Assert.AreEqual(expected, DataJobStateExtensions.Parse(text));

    [TestMethod]
    public void DataJobState_UnknownJsonValue_DoesNotThrow()
    {
        var message = JsonSerializer.Deserialize<DataMessage>("""{"MessageId":"1","DataJobState":"Brand new state","MessageStatus":"Enqueued"}""");
        Assert.AreEqual(DataJobState.Unrecognized, message!.DataJobState);
        Assert.AreEqual(MessageStatus.Enqueued, message.MessageStatus);
    }
}

[TestClass]
public class BackoffTests
{
    [TestMethod]
    public void Backoff_GrowsToMaximum()
    {
        var backoff = new StatusCheckBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
        var delays = Enumerable.Range(0, 20).Select(_ => backoff.Next()).ToList();

        Assert.AreEqual(TimeSpan.FromSeconds(5), delays[0]);
        Assert.AreEqual(TimeSpan.FromSeconds(7.5), delays[1]);
        Assert.AreEqual(TimeSpan.FromMinutes(5), delays[^1]);
        CollectionAssert.AreEqual(delays.OrderBy(d => d).ToList(), delays);
    }

    [TestMethod]
    public void Backoff_ZeroValues_UseDefaults()
    {
        var backoff = new StatusCheckBackoff(TimeSpan.Zero, TimeSpan.Zero);
        Assert.AreEqual(PollingOptions.DefaultInitialDelay, backoff.Next());
    }
}

[TestClass]
public class ConfigurationTests
{
    private static RisConfiguration ValidConfiguration() => new()
    {
        Connections =
        [
            new D365ConnectionOptions
            {
                Name = "PROD",
                AosUri = new Uri("https://contoso.operations.dynamics.com"),
                Tenant = "contoso.onmicrosoft.com",
                ClientId = Guid.NewGuid().ToString(),
                ClientSecret = "secret"
            }
        ],
        Jobs =
        [
            new JobDefinition
            {
                Name = "CustInvoiceJour",
                Group = "Export jobs",
                Schedule = new ScheduleOptions { Cron = "0 0 2 * * ?" },
                Options = new ExportJobOptions
                {
                    Connection = "PROD",
                    DataProject = "CustInvoiceJour",
                    Company = "USMF",
                    DownloadSuccessDir = @"C:\RIS\Export"
                }
            }
        ]
    };

    [TestMethod]
    public void ValidConfiguration_HasNoErrors()
    {
        var errors = ConfigurationValidator.Validate(ValidConfiguration());
        Assert.AreEqual(0, errors.Count, string.Join(Environment.NewLine, errors));
    }

    [TestMethod]
    public void InvalidConfiguration_ReportsAllProblemsWithPaths()
    {
        var configuration = ValidConfiguration();
        configuration.Connections[0].ClientId = "not-a-guid";
        configuration.Connections[0].ClientSecret = null;
        var export = (ExportJobOptions)configuration.Jobs[0].Options!;
        export.Connection = "TEST";
        export.DownloadSuccessDir = "relative";
        configuration.Jobs[0].Schedule = new ScheduleOptions();

        var errors = ConfigurationValidator.Validate(configuration);

        StringAssert.Contains(string.Join("|", errors), "ClientId must be a GUID");
        StringAssert.Contains(string.Join("|", errors), "ClientSecret is required");
        StringAssert.Contains(string.Join("|", errors), "connection 'TEST' is not defined");
        StringAssert.Contains(string.Join("|", errors), "DownloadSuccessDir must be an absolute path");
        StringAssert.Contains(string.Join("|", errors), "Exactly one of Cron or Interval");
    }

    [TestMethod]
    public void IntervalSchedule_WithoutStartAt_IsInvalid()
    {
        var configuration = ValidConfiguration();
        configuration.Jobs[0].Schedule = new ScheduleOptions { Interval = TimeSpan.FromHours(2) };

        var errors = string.Join("|", ConfigurationValidator.Validate(configuration));

        StringAssert.Contains(errors, "StartAt is required for interval schedules");
    }

    [TestMethod]
    public void Configuration_RoundTripsThroughJson_WithJobTypeDiscriminator()
    {
        var json = JsonSerializer.Serialize(ValidConfiguration(), RisJson.Options);
        StringAssert.Contains(json, "\"type\": \"Export\"");

        var loaded = JsonSerializer.Deserialize<RisConfiguration>(json, RisJson.Options)!;
        Assert.AreEqual(JobType.Export, loaded.Jobs[0].Type);
        Assert.AreEqual("Export jobs.CustInvoiceJour", loaded.Jobs[0].Key);
        Assert.AreEqual(TimeSpan.FromHours(12), ((ExportJobOptions)loaded.Jobs[0].Options!).Polling.Timeout);
    }
}
