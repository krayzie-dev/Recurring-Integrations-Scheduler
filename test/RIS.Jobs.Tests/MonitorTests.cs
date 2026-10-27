using System.IO.Abstractions.TestingHelpers;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RIS.Core.Contracts;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.Core.Statuses;
using RIS.Transfer;

namespace RIS.Jobs.Tests;

[TestClass]
public sealed class ExecutionMonitorTests
{
    private static ExecutionMonitorJobOptions Options() => new()
    {
        UploadSuccessDir = @"C:\ris\success",
        ProcessingSuccessDir = @"C:\ris\done",
        ProcessingErrorsDir = @"C:\ris\failed",
        GetExecutionErrors = true
    };

    private static async Task AddSubmittedAsync(JobsTestHost host, string name, string executionId, DateTime submittedUtc)
    {
        var path = @"C:\ris\success\" + name;
        host.FileSystem.AddFile(path, new MockFileData("data"));
        var statusFiles = host.Services.GetRequiredService<StatusFileStore>();
        await statusFiles.WriteAsync(new DataMessage { Name = name, FullPath = path, MessageId = executionId, MessageStatus = MessageStatus.Enqueued, SubmittedUtc = submittedUtc }, ".Status", default);
    }

    [TestMethod]
    public async Task Statuses_AreHandledPerFile()
    {
        using var host = new JobsTestHost();
        var now = host.Clock.UtcNow;
        await AddSubmittedAsync(host, "ok.csv", "IMPORT-OK", now.AddMinutes(-5));
        await AddSubmittedAsync(host, "failed.csv", "IMPORT-FAILED", now.AddMinutes(-5));
        await AddSubmittedAsync(host, "waiting.csv", "IMPORT-WAIT", now.AddHours(-1));
        await AddSubmittedAsync(host, "stuck.csv", "IMPORT-STUCK", now.AddHours(-13));
        await AddSubmittedAsync(host, "unavailable.csv", "IMPORT-503", now.AddMinutes(-5));
        host.D365.ExecutionStatus = id => id switch
        {
            "IMPORT-OK" => "Succeeded",
            "IMPORT-FAILED" => "Failed",
            "IMPORT-503" => throw FakeD365Client.HttpError(HttpStatusCode.ServiceUnavailable, "maintenance"),
            _ => "Executing"
        };

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(JobOutcomeStatus.Failed, outcome.Status);
        Assert.AreEqual(1, outcome.Metrics["Succeeded"]);
        Assert.AreEqual(2, outcome.Metrics["Failed"]);
        Assert.AreEqual(2, outcome.Metrics["Pending"]);

        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\done\ok.csv"));
        Assert.IsFalse(host.FileSystem.FileExists(@"C:\ris\done\ok.Status"));
        StringAssert.Contains(host.FileSystem.File.ReadAllText(@"C:\ris\done\ok.url"), "URL=https://d365.test/summary/IMPORT-OK");

        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\failed\failed.csv"));
        Assert.AreEqual(1, host.FileSystem.Directory.GetFiles(@"C:\ris\failed", "failed-ExecutionErrors-*.txt").Length);

        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\failed\stuck.csv"), "Import waiting longer than 12 h is moved to errors.");
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\success\waiting.csv"));
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\success\unavailable.csv"), "Temporary D365 error leaves file for next run.");
        Assert.IsTrue(host.Logs.Errors.Any(e => e.Message.Contains(ErrorCodes.MonitorTimeout)));
    }

    [TestMethod]
    public async Task StatusFileFrom4x_WithoutSubmittedTime_UsesCreationTime()
    {
        using var host = new JobsTestHost();
        host.FileSystem.AddFile(@"C:\ris\success\old.csv", new MockFileData("data"));
        host.FileSystem.AddFile(@"C:\ris\success\old.Status", new MockFileData("""{"Name":"old.csv","FullPath":"C:\\ris\\success\\old.csv","MessageId":"IMPORT-OLD","MessageStatus":"Enqueued","DataJobState":"Enqueued"}""")
        {
            CreationTime = host.Clock.UtcNow.AddHours(-20)
        });
        host.D365.ExecutionStatus = _ => "NotRun";

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(ErrorCodes.MonitorTimeout, outcome.ErrorCode);
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\failed\old.csv"));
    }
}

[TestClass]
public sealed class ProcessingMonitorTests
{
    [TestMethod]
    public async Task States_AreHandledPerFile_AndStatusDetailIsKeptForFailures()
    {
        using var host = new JobsTestHost();
        var statusFiles = host.Services.GetRequiredService<StatusFileStore>();
        var now = host.Clock.UtcNow;
        foreach (var (name, id, submitted) in new[] { (@"sub\ok.csv", "M-OK", now), ("bad.csv", "M-BAD", now), ("slow.csv", "M-SLOW", now.AddHours(-13)) })
        {
            host.FileSystem.AddFile(@"C:\ris\success\" + name, new MockFileData("x"));
            await statusFiles.WriteAsync(new DataMessage { Name = name, FullPath = @"C:\ris\success\" + name, MessageId = id, SubmittedUtc = submitted }, ".Status", default);
        }
        host.D365.JobState = id => id switch { "M-OK" => DataJobState.Processed, "M-BAD" => DataJobState.ProcessedWithErrors, _ => DataJobState.InProcess };

        var outcome = await host.RunAsync(new ProcessingMonitorJobOptions
        {
            ActivityId = Guid.NewGuid(),
            UploadSuccessDir = @"C:\ris\success",
            ProcessingSuccessDir = @"C:\ris\done",
            ProcessingErrorsDir = @"C:\ris\failed"
        });

        Assert.AreEqual(1, outcome.Metrics["Succeeded"]);
        Assert.AreEqual(2, outcome.Metrics["Failed"]);
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\done\sub\ok.csv"), "Subfolder is kept.");
        Assert.IsFalse(host.FileSystem.FileExists(@"C:\ris\done\sub\ok.Status"));
        StringAssert.Contains(host.FileSystem.File.ReadAllText(@"C:\ris\failed\bad.Status"), "log of M-BAD");
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\failed\slow.csv"));
    }
}

[TestClass]
public sealed class PackageManifestTests
{
    [TestMethod]
    public void EntityNames_AreReadFromManifest()
    {
        using var zip = new MemoryStream(TestFiles.Zip(("Manifest.xml", TestFiles.TemplateManifest)));
        CollectionAssert.AreEqual(new[] { "Vendor invoice header" }, PackageManifest.GetEntityNames(zip, "test").ToArray());
    }

    [TestMethod]
    public void TemplateWithoutManifest_IsReportedWithCode()
    {
        using var zip = new MemoryStream(TestFiles.Zip(("data.csv", "x")));
        var ex = Assert.ThrowsExactly<RisException>(() => PackageManifest.GetInputFileName(zip, "template.zip"));
        Assert.AreEqual(ErrorCodes.PackageTemplateInvalid, ex.ErrorCode);
    }

    [TestMethod]
    public void Rebase_KeepsSubfolders_AndFallsBackToFileName()
    {
        var files = new JobFiles(new FileStore(new MockFileSystem(), NullLogger<FileStore>.Instance), null!, null!);
        Assert.AreEqual(@"C:\b\sub\x.csv", files.Rebase(@"C:\a\sub\x.csv", @"C:\a", @"C:\b"));
        Assert.AreEqual(@"C:\b\x.csv", files.Rebase(@"D:\other\x.csv", @"C:\a", @"C:\b"));
    }
}
