using System.Net;
using Microsoft.Extensions.Logging;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.D365;

namespace RIS.Jobs.Tests;

[TestClass]
public sealed class ExportTests
{
    private static ExportJobOptions Options() => new()
    {
        DataProject = "CustInvoiceJour",
        Company = "USMF",
        DownloadSuccessDir = @"C:\ris\export"
    };

    [TestMethod]
    public async Task NotRunForTwoHours_ThenSucceeded_DownloadsPackage_WithGrowingDelays()
    {
        using var host = new JobsTestHost();
        var started = host.Clock.UtcNow;
        host.D365.ExportStatus = () => host.Clock.UtcNow - started < TimeSpan.FromHours(2) ? "NotRun" : "Succeeded";

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(JobOutcomeStatus.Succeeded, outcome.Status, outcome.Summary);
        Assert.AreEqual(1, host.FileSystem.Directory.GetFiles(@"C:\ris\export", "*.zip").Length);
        Assert.AreEqual(TimeSpan.FromSeconds(5), host.Clock.Delays[0]);
        Assert.AreEqual(TimeSpan.FromMinutes(5), host.Clock.Delays.Max());
        CollectionAssert.AreEqual(host.Clock.Delays.OrderBy(d => d).ToList(), host.Clock.Delays);
        StringAssert.StartsWith((string)outcome.Metrics["ExecutionId"], "CustInvoiceJour-");
        Assert.AreEqual(0, host.Logs.Errors.Count());
    }

    [TestMethod]
    public async Task StillNotRunAfter12Hours_FailsWithTimeoutCode_ExecutionId_AndSingleErrorLog()
    {
        using var host = new JobsTestHost();
        var started = host.Clock.UtcNow;
        host.D365.ExportStatus = () => "NotRun";

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(JobOutcomeStatus.Failed, outcome.Status);
        Assert.AreEqual(ErrorCodes.ExportTimeout, outcome.ErrorCode);
        StringAssert.Contains(outcome.Summary, "did not finish within 12:00:00");
        StringAssert.StartsWith((string)outcome.Metrics["ExecutionId"], "CustInvoiceJour-");
        Assert.AreEqual(TimeSpan.FromHours(12), host.Clock.UtcNow - started);

        var error = host.Logs.Errors.Single();
        StringAssert.Contains(error.Message, "[RIS-EXP-001]");
        Assert.AreEqual("Tests.ExportJobOptions", error.Scope["JobKey"]);
        Assert.AreEqual("RUN-1", error.Scope["RunId"]);
    }

    [TestMethod]
    public async Task SucceededExactlyAtDeadline_IsDownloaded()
    {
        using var host = new JobsTestHost();
        var deadline = host.Clock.UtcNow.AddHours(12);
        host.D365.ExportStatus = () => host.Clock.UtcNow < deadline ? "Executing" : "Succeeded";

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(JobOutcomeStatus.Succeeded, outcome.Status, outcome.Summary);
    }

    [TestMethod]
    public async Task TransientErrorsWhilePolling_AreTolerated()
    {
        using var host = new JobsTestHost();
        var calls = 0;
        host.D365.ExportStatus = () => ++calls switch
        {
            1 => throw FakeD365Client.HttpError(HttpStatusCode.ServiceUnavailable, "maintenance"),
            2 => throw new D365ApiException("Test", null, null, null, new Dictionary<string, string>(), new HttpRequestException("The remote name could not be resolved")),
            _ => "Succeeded"
        };

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(JobOutcomeStatus.Succeeded, outcome.Status, outcome.Summary);
        Assert.AreEqual(2, host.Logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("Temporary error")));
    }

    [TestMethod]
    [DataRow("Failed", ErrorCodes.ExportFailed)]
    [DataRow("Canceled", ErrorCodes.ExportFailed)]
    [DataRow("Cancelling", ErrorCodes.StatusUnrecognized)]
    public async Task FinalErrorStatus_FailsWithCode(string status, string code)
    {
        using var host = new JobsTestHost();
        host.D365.ExportStatus = () => status;

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(JobOutcomeStatus.Failed, outcome.Status);
        Assert.AreEqual(code, outcome.ErrorCode);
        Assert.AreEqual(0, host.FileSystem.AllFiles.Count());
    }

    [TestMethod]
    public async Task Unzip_AndSftpOutbound_UploadsPackage_MovesItToUploaded()
    {
        using var host = new JobsTestHost();
        var options = Options();
        options.UnzipPackage = true;
        options.SftpOutbound = new SftpTransferOptions { Server = "SFTP", RemoteFolder = "/out" };

        var outcome = await host.RunAsync(options);

        Assert.AreEqual(JobOutcomeStatus.Succeeded, outcome.Status, outcome.Summary);
        Assert.AreEqual(1, host.Sftp.Uploaded.Count);
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\export\Customers.csv"));
        Assert.AreEqual(1, host.FileSystem.Directory.GetFiles(@"C:\ris\export\Uploaded", "*.zip").Length);
    }

    [TestMethod]
    public async Task FileNamePattern_WithExecutionId_IsUsed()
    {
        using var host = new JobsTestHost();
        var options = Options();
        options.FileNamePattern = "{date}_{executionId}";

        var outcome = await host.RunAsync(options);

        var file = host.FileSystem.Path.GetFileName(host.FileSystem.Directory.GetFiles(@"C:\ris\export", "*.zip").Single());
        StringAssert.Contains(file, (string)outcome.Metrics["ExecutionId"]);
    }

    [TestMethod]
    public async Task Interrupted_PropagatesCancellation_AndLogsExecutionId()
    {
        using var host = new JobsTestHost();
        using var cts = new CancellationTokenSource();
        host.D365.ExportStatus = () => "NotRun";
        host.Clock.OnDelay = count => { if (count == 3) cts.Cancel(); };

        await Assert.ThrowsAsync<OperationCanceledException>(() => host.RunAsync(Options(), cts.Token));

        Assert.IsTrue(host.Logs.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("may still run in D365") && e.Message.Contains("CustInvoiceJour-")));
        Assert.AreEqual(0, host.Logs.Errors.Count());
    }
}

[TestClass]
public sealed class DownloadTests
{
    [TestMethod]
    public async Task EachMessage_IsDownloadedAndAcked_AckFailureMovesOnlyThatFileToErrors()
    {
        using var host = new JobsTestHost();
        host.D365.Messages.Enqueue(new DequeuedMessage("C1", "P1", new Uri("https://blob.test/1.zip")));
        host.D365.Messages.Enqueue(new DequeuedMessage("C2", "P2", new Uri("https://blob.test/2.zip")));
        host.D365.Messages.Enqueue(new DequeuedMessage("C3", "P3", new Uri("https://blob.test/3.zip")));
        host.D365.AckSucceeds = m => m.CorrelationId != "C2";

        var outcome = await host.RunAsync(new DownloadJobOptions
        {
            ActivityId = Guid.NewGuid(),
            DownloadSuccessDir = @"C:\ris\download",
            DownloadErrorsDir = @"C:\ris\download-errors"
        });

        Assert.AreEqual(JobOutcomeStatus.Failed, outcome.Status);
        Assert.AreEqual(ErrorCodes.AckFailed, outcome.ErrorCode);
        Assert.AreEqual(2, outcome.Metrics["Succeeded"]);
        Assert.AreEqual(2, host.FileSystem.Directory.GetFiles(@"C:\ris\download", "*.zip").Length);
        Assert.AreEqual(1, host.FileSystem.Directory.GetFiles(@"C:\ris\download-errors", "*.zip").Length);
        CollectionAssert.AreEqual(new[] { "Ack C1", "Ack C2", "Ack C3" }, host.D365.Calls.Where(c => c.StartsWith("Ack")).ToArray());
    }

    [TestMethod]
    public async Task DownloadFailure_IsNotAcked_AndOtherMessagesContinue()
    {
        using var host = new JobsTestHost();
        host.D365.Messages.Enqueue(new DequeuedMessage("C1", "P1", new Uri("https://broken.test/1.zip")));
        host.D365.Messages.Enqueue(new DequeuedMessage("C2", "P2", new Uri("https://blob.test/2.zip")));
        host.D365.DownloadContent = uri => uri.Host == "broken.test" ? throw FakeD365Client.HttpError(HttpStatusCode.Forbidden, "expired SAS") : TestFiles.Zip(("x.csv", "1"));

        var outcome = await host.RunAsync(new DownloadJobOptions
        {
            ActivityId = Guid.NewGuid(),
            DownloadSuccessDir = @"C:\ris\download",
            DownloadErrorsDir = @"C:\ris\download-errors"
        });

        Assert.AreEqual(1, outcome.Metrics["Failed"]);
        Assert.AreEqual(1, outcome.Metrics["Succeeded"]);
        CollectionAssert.AreEqual(new[] { "Ack C2" }, host.D365.Calls.Where(c => c.StartsWith("Ack")).ToArray());
        Assert.AreEqual(1, host.FileSystem.Directory.GetFiles(@"C:\ris\download", "*.zip").Length);
    }
}
