using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RIS.Core.Contracts;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.Core.Statuses;

namespace RIS.Transfer.Tests;

[TestClass]
public class FileStoreTests
{
    private static readonly RetryOptions NoDelayRetry = new() { Count = 2, Delay = TimeSpan.Zero };

    private static (MockFileSystem fs, FileStore store) Create()
    {
        var fs = new MockFileSystem();
        return (fs, new FileStore(fs, NullLogger<FileStore>.Instance));
    }

    [TestMethod]
    public void GetFiles_OrdersAsConfigured()
    {
        var (fs, store) = Create();
        fs.AddFile(@"C:\in\b.csv", new MockFileData("bb") { CreationTime = new DateTime(2026, 1, 1) });
        fs.AddFile(@"C:\in\a.csv", new MockFileData("a") { CreationTime = new DateTime(2026, 1, 2) });
        fs.AddFile(@"C:\in\sub\c.csv", new MockFileData("ccc") { CreationTime = new DateTime(2026, 1, 3) });

        CollectionAssert.AreEqual(new[] { @"C:\in\b.csv", @"C:\in\a.csv", @"C:\in\sub\c.csv" },
            store.GetFiles(@"C:\in", new FileSelectionOptions()).ToArray());
        CollectionAssert.AreEqual(new[] { @"C:\in\a.csv", @"C:\in\b.csv", @"C:\in\sub\c.csv" },
            store.GetFiles(@"C:\in", new FileSelectionOptions { OrderBy = FileOrder.FileName }).ToArray());
        CollectionAssert.AreEqual(new[] { @"C:\in\sub\c.csv", @"C:\in\b.csv", @"C:\in\a.csv" },
            store.GetFiles(@"C:\in", new FileSelectionOptions { OrderBy = FileOrder.Size, Reverse = true }).ToArray());
    }

    [TestMethod]
    public void GetFiles_MissingFolder_ThrowsWithErrorCode()
    {
        var (_, store) = Create();
        var ex = Assert.ThrowsExactly<RisException>(() => store.GetFiles(@"C:\missing", new FileSelectionOptions()));
        Assert.AreEqual(ErrorCodes.FolderNotAccessible, ex.ErrorCode);
    }

    [TestMethod]
    public async Task Move_CreatesFolderAndOverwrites()
    {
        var (fs, store) = Create();
        fs.AddFile(@"C:\in\a.csv", new MockFileData("new"));
        fs.AddFile(@"C:\out\x\a.csv", new MockFileData("old"));

        await store.MoveAsync(@"C:\in\a.csv", @"C:\out\x\a.csv", NoDelayRetry, CancellationToken.None);

        Assert.IsFalse(fs.FileExists(@"C:\in\a.csv"));
        Assert.AreEqual("new", fs.File.ReadAllText(@"C:\out\x\a.csv"));
    }

    [TestMethod]
    public async Task OpenRead_LockedFile_RetriesThenThrowsFileLocked()
    {
        var (fs, store) = Create();
        fs.AddFile(@"C:\in\locked.csv", new MockFileData("x") { AllowedFileShare = FileShare.None });

        var ex = await Assert.ThrowsExactlyAsync<RisException>(() => store.OpenReadAsync(@"C:\in\locked.csv", NoDelayRetry, CancellationToken.None));

        Assert.AreEqual(ErrorCodes.FileLocked, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "3 attempt(s)");
    }

    [TestMethod]
    public void ResolveUniquePath_AddsCounter()
    {
        var (fs, store) = Create();
        fs.AddFile(@"C:\in\a.csv", new MockFileData("1"));
        fs.AddFile(@"C:\in\a_1.csv", new MockFileData("2"));

        Assert.AreEqual(@"C:\in\b.csv", store.ResolveUniquePath(@"C:\in", "b.csv"));
        Assert.AreEqual(@"C:\in\a_2.csv", store.ResolveUniquePath(@"C:\in", "a.csv"));
    }

    [TestMethod]
    public void UnzipPackage_ExtractsDataFilesAndSkipsUnsafeEntries()
    {
        var (fs, store) = Create();
        fs.AddFile(@"C:\out\pkg.zip", new MockFileData(CreateZip(("Manifest.xml", "m"), ("Customers.csv", "data"), ("../evil.txt", "x"))));

        var extracted = store.UnzipPackage(@"C:\out\pkg.zip", deletePackage: true, addTimestamp: false);

        CollectionAssert.AreEqual(new[] { @"C:\out\Customers.csv" }, extracted.ToArray());
        Assert.IsFalse(fs.FileExists(@"C:\out\Manifest.xml"));
        Assert.IsFalse(fs.FileExists(@"C:\evil.txt"));
        Assert.IsFalse(fs.FileExists(@"C:\out\pkg.zip"));
    }

    [TestMethod]
    public void UnzipPackage_AddTimestamp_PrefixesPackageName()
    {
        var (fs, store) = Create();
        fs.AddFile(@"C:\out\2026-10-27.zip", new MockFileData(CreateZip(("Customers.csv", "data"))));

        var extracted = store.UnzipPackage(@"C:\out\2026-10-27.zip", deletePackage: false, addTimestamp: true);

        CollectionAssert.AreEqual(new[] { @"C:\out\2026-10-27-Customers.csv" }, extracted.ToArray());
    }

    [TestMethod]
    [DataRow("orders.csv", "*.csv", true)]
    [DataRow("orders.CSV", "*.csv", true)]
    [DataRow("orders.txt", "*.csv", false)]
    [DataRow("ord_1.csv", "ord_?.csv", true)]
    [DataRow("anything", "*", true)]
    public void SftpMask(string name, string mask, bool expected) =>
        Assert.AreEqual(expected, SshNetSftpTransfer.MatchesMask(name, mask));

    internal static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }
        return memory.ToArray();
    }
}

[TestClass]
public class StatusFileStoreTests
{
    private static (MockFileSystem fs, StatusFileStore store) Create()
    {
        var fs = new MockFileSystem();
        var files = new FileStore(fs, NullLogger<FileStore>.Instance);
        return (fs, new StatusFileStore(files, NullLogger<StatusFileStore>.Instance));
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTrip()
    {
        var (_, store) = Create();
        var submitted = new DateTime(2026, 10, 16, 2, 0, 0, DateTimeKind.Utc);
        await store.WriteAsync(new DataMessage
        {
            FullPath = @"C:\success\sub\orders.csv",
            Name = "orders.csv",
            MessageId = "EXEC-1",
            MessageStatus = MessageStatus.Enqueued,
            SubmittedUtc = submitted
        }, ".Status", CancellationToken.None);

        var entry = store.ReadAll(@"C:\success", ".Status").Single();

        Assert.AreEqual(@"C:\success\sub\orders.Status", entry.StatusFilePath);
        Assert.AreEqual("EXEC-1", entry.Message.MessageId);
        Assert.AreEqual(submitted, store.GetSubmittedUtc(entry));
    }

    [TestMethod]
    public void Read_4xStatusFileInAnsiCodePage_WithoutSubmittedTime()
    {
        var (fs, store) = Create();
        // 4.x: Newtonsoft, indented, StringEnumConverter, Encoding.Default (ANSI, e.g. 1250 with Polish letters).
        var json = """
                   {
                     "Name": "faktura_żółć.csv",
                     "FullPath": "C:\\success\\faktura_żółć.csv",
                     "MessageId": "EXEC-4X",
                     "MessageStatus": "Enqueued",
                     "DataJobState": "Enqueued",
                     "CorrelationId": null,
                     "PopReceipt": null,
                     "DownloadLocation": null
                   }
                   """;
        var created = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        fs.AddFile(@"C:\success\faktura_żółć.Status", new MockFileData(Encoding.GetEncoding(1250).GetBytes(json)) { CreationTime = created });

        var entry = store.ReadAll(@"C:\success", ".Status").Single();

        Assert.AreEqual("EXEC-4X", entry.Message.MessageId);
        Assert.AreEqual(DataJobState.Enqueued, entry.Message.DataJobState);
        Assert.AreEqual(created, store.GetSubmittedUtc(entry), "4.x status files fall back to file creation time.");
        if (System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage == 1250)
        {
            Assert.AreEqual(@"C:\success\faktura_żółć.csv", entry.Message.FullPath);
        }
    }

    [TestMethod]
    public void ReadAll_CorruptedFile_IsSkipped()
    {
        var (fs, store) = Create();
        fs.AddFile(@"C:\success\bad.Status", new MockFileData("{ not json"));
        fs.AddFile(@"C:\success\good.Status", new MockFileData("""{"FullPath":"C:\\success\\good.csv","MessageId":"1"}"""));

        Assert.AreEqual("1", store.ReadAll(@"C:\success", ".Status").Single().Message.MessageId);
    }

    [TestMethod]
    public async Task MoveWithStatus_MovesOrDeletesStatusFile()
    {
        var (fs, store) = Create();
        fs.AddFile(@"C:\success\a.csv", new MockFileData("a"));
        fs.AddFile(@"C:\success\a.Status", new MockFileData("{}"));
        fs.AddFile(@"C:\success\b.csv", new MockFileData("b"));
        fs.AddFile(@"C:\success\b.Status", new MockFileData("{}"));
        var retry = new RetryOptions { Count = 0 };

        await store.MoveWithStatusAsync(@"C:\success\a.csv", @"C:\errors\a.csv", ".Status", deleteStatusFile: false, retry, CancellationToken.None);
        await store.MoveWithStatusAsync(@"C:\success\b.csv", @"C:\done\b.csv", ".Status", deleteStatusFile: true, retry, CancellationToken.None);

        Assert.IsTrue(fs.FileExists(@"C:\errors\a.Status"));
        Assert.IsFalse(fs.FileExists(@"C:\success\b.Status"));
        Assert.IsFalse(fs.FileExists(@"C:\done\b.Status"));
        Assert.IsTrue(fs.FileExists(@"C:\done\b.csv"));
    }
}
