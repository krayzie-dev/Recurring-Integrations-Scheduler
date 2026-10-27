using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Text.Json;
using RIS.Core.Contracts;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.Core.Statuses;
using RIS.Transfer;

namespace RIS.Jobs.Tests;

[TestClass]
public sealed class ImportTests
{
    private static ImportJobOptions Options() => new()
    {
        DataProject = "Vendor invoices",
        Company = "2020",
        InputDir = @"C:\ris\in",
        UploadSuccessDir = @"C:\ris\success",
        UploadErrorsDir = @"C:\ris\errors",
        PackageTemplate = @"C:\ris\template.zip",
        Files = new FileSelectionOptions { OrderBy = FileOrder.FileName }
    };

    private static void AddTemplate(JobsTestHost host) =>
        host.FileSystem.AddFile(@"C:\ris\template.zip", new MockFileData(TestFiles.Zip(("Manifest.xml", TestFiles.TemplateManifest), ("PackageHeader.xml", "<h/>"))));

    [TestMethod]
    public async Task OneFileFails_OtherFilesAreStillImported()
    {
        using var host = new JobsTestHost();
        AddTemplate(host);
        host.FileSystem.AddFile(@"C:\ris\in\a.csv", new MockFileData("A"));
        host.FileSystem.AddFile(@"C:\ris\in\sub\b.csv", new MockFileData("B"));
        host.FileSystem.AddFile(@"C:\ris\in\c.csv", new MockFileData("C"));
        var call = 0;
        host.D365.ImportFromPackage = (_, executionId) => ++call == 2
            ? throw FakeD365Client.HttpError(HttpStatusCode.BadRequest, "{\"error\":\"definition group not found\"}")
            : executionId;

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(JobOutcomeStatus.Failed, outcome.Status);
        Assert.AreEqual(ErrorCodes.ImportRequestFailed, outcome.ErrorCode);
        Assert.AreEqual(2, outcome.Metrics["Succeeded"]);
        Assert.AreEqual(1, outcome.Metrics["Failed"]);

        // a.csv and b.csv (subfolder kept) succeeded, c.csv - the second file in order a, c, sub\b - failed
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\success\a.csv"));
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\success\sub\b.csv"));
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\errors\c.csv"));
        var report = host.FileSystem.File.ReadAllText(@"C:\ris\errors\c.Status");
        StringAssert.Contains(report, "definition group not found");
        StringAssert.Contains(report, ErrorCodes.ImportRequestFailed);

        var status = JsonSerializer.Deserialize<DataMessage>(host.FileSystem.File.ReadAllText(@"C:\ris\success\a.Status"))!;
        Assert.AreEqual(MessageStatus.Enqueued, status.MessageStatus);
        Assert.AreEqual(host.Clock.UtcNow, status.SubmittedUtc);
        StringAssert.StartsWith(status.MessageId, "Vendor invoices-");

        Assert.AreEqual(0, host.FileSystem.Directory.GetFiles(host.FileSystem.Path.GetTempPath(), "ris-*.zip").Length, "Temporary packages must be deleted.");
        Assert.AreEqual(1, host.Logs.Errors.Count(), "Failed file is logged once.");
    }

    [TestMethod]
    public async Task WrappedPackage_ContainsInputFile_AndManifestPointsToOriginalName()
    {
        using var host = new JobsTestHost();
        AddTemplate(host);
        host.FileSystem.AddFile(@"C:\ris\in\INV_0001.csv", new MockFileData("header;line"));

        await host.RunAsync(Options());

        var package = host.D365.UploadedPackages.Single().Package;
        Assert.AreEqual("header;line", TestFiles.ReadEntry(package, "INV_0001.csv"));
        StringAssert.Contains(TestFiles.ReadEntry(package, "Manifest.xml"), "<InputFilePath>INV_0001.csv</InputFilePath>");
    }

    [TestMethod]
    public async Task MultiCompany_FromFileNameToken()
    {
        using var host = new JobsTestHost();
        AddTemplate(host);
        host.FileSystem.AddFile(@"C:\ris\in\INV_2020_0001.csv", new MockFileData("x"));
        var options = Options();
        options.Company = null;
        options.MultiCompany = new MultiCompanyOptions { Source = LegalEntitySource.FileName, FileNameSeparator = "_", TokenPosition = 2 };

        var outcome = await host.RunAsync(options);

        Assert.AreEqual(JobOutcomeStatus.Succeeded, outcome.Status, outcome.Summary);
        CollectionAssert.Contains(host.D365.Calls, "ImportFromPackage 2020");
    }

    [TestMethod]
    public async Task MultiCompany_TokenMissing_MovesFileToErrors()
    {
        using var host = new JobsTestHost();
        AddTemplate(host);
        host.FileSystem.AddFile(@"C:\ris\in\INV.csv", new MockFileData("x"));
        var options = Options();
        options.Company = null;
        options.MultiCompany = new MultiCompanyOptions { Source = LegalEntitySource.FileName, FileNameSeparator = "_", TokenPosition = 3 };

        var outcome = await host.RunAsync(options);

        Assert.AreEqual(ErrorCodes.LegalEntityNotResolved, outcome.ErrorCode);
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\errors\INV.csv"));
    }

    [TestMethod]
    public async Task LockedFile_IsLeftForNextRun()
    {
        using var host = new JobsTestHost();
        AddTemplate(host);
        host.FileSystem.AddFile(@"C:\ris\in\locked.csv", new MockFileData("x") { AllowedFileShare = FileShare.None });

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(1, outcome.Metrics["Skipped"]);
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\in\locked.csv"));
        Assert.AreEqual(0, host.Logs.Errors.Count());
    }

    [TestMethod]
    public async Task AuthenticationError_StopsWholeRun()
    {
        using var host = new JobsTestHost();
        AddTemplate(host);
        host.FileSystem.AddFile(@"C:\ris\in\a.csv", new MockFileData("A"));
        host.FileSystem.AddFile(@"C:\ris\in\b.csv", new MockFileData("B"));
        host.D365.AzureWriteUrlError = () => new RisException(ErrorCodes.AuthenticationFailed, "AADSTS7000215: Invalid client secret");

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(ErrorCodes.AuthenticationFailed, outcome.ErrorCode);
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\in\a.csv") && host.FileSystem.FileExists(@"C:\ris\in\b.csv"), "Files stay in input.");
    }

    [TestMethod]
    public async Task InvalidConfiguration_FailsBeforeTouchingFiles()
    {
        using var host = new JobsTestHost();
        var options = Options();
        options.InputDir = "relative";

        var outcome = await host.RunAsync(options);

        Assert.AreEqual(ErrorCodes.ConfigurationInvalid, outcome.ErrorCode);
        StringAssert.Contains(outcome.Summary, "InputDir must be an absolute path");
    }
}

[TestClass]
public sealed class UploadTests
{
    [TestMethod]
    public async Task Enqueue_SuccessAndFailure_AsIn4x()
    {
        using var host = new JobsTestHost();
        host.FileSystem.AddFile(@"C:\ris\in\sub\ok.csv", new MockFileData("1"));
        host.FileSystem.AddFile(@"C:\ris\in\bad.csv", new MockFileData("2"));
        host.D365.Enqueue = name => name == "bad.csv" ? throw FakeD365Client.HttpError(HttpStatusCode.BadRequest, "entity not found") : "MSG-1";

        var outcome = await host.RunAsync(new UploadJobOptions
        {
            ActivityId = Guid.NewGuid(),
            EntityName = "Customers V3",
            Company = "USMF",
            InputDir = @"C:\ris\in",
            UploadSuccessDir = @"C:\ris\success",
            UploadErrorsDir = @"C:\ris\errors"
        });

        Assert.AreEqual(1, outcome.Metrics["Succeeded"]);
        Assert.AreEqual(1, outcome.Metrics["Failed"]);
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\success\ok.csv"), "4.x moves uploaded files without subfolders.");
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\errors\bad.csv"));
        StringAssert.Contains(host.FileSystem.File.ReadAllText(@"C:\ris\errors\bad.Status"), "entity not found");
        Assert.AreEqual("MSG-1", JsonSerializer.Deserialize<DataMessage>(host.FileSystem.File.ReadAllText(@"C:\ris\success\ok.Status"))!.MessageId);
        Assert.AreEqual(("ok.csv", (string?)"Customers V3", (string?)"USMF"), host.D365.Enqueued.First(e => e.Name == "ok.csv"));
    }

    [TestMethod]
    public async Task SftpInbound_FilesAreDownloadedFirst()
    {
        using var host = new JobsTestHost();
        host.FileSystem.AddDirectory(@"C:\ris\in");
        host.Sftp.FilesToDownload.Add("from-sftp.csv");

        var outcome = await host.RunAsync(new UploadJobOptions
        {
            ActivityId = Guid.NewGuid(),
            IsDataPackage = true,
            InputDir = @"C:\ris\in",
            UploadSuccessDir = @"C:\ris\success",
            UploadErrorsDir = @"C:\ris\errors",
            SftpInbound = new SftpTransferOptions { Server = "SFTP", RemoteFolder = "/in" }
        });

        Assert.AreEqual(1, outcome.Metrics["Succeeded"]);
        Assert.IsNull(host.D365.Enqueued.Single().Entity, "Data packages are enqueued without entity name.");
    }
}
