using Microsoft.Extensions.DependencyInjection;
using RIS.Core.Configuration;
using RIS.Core.Options;
using RIS.Hosting.Tests;
using RIS.Persistence;
using RIS.Server.Services;

namespace RIS.Server.Tests;

[TestClass]
public sealed class JobTemplatesTests
{
    [TestMethod]
    public async Task ImportWithMonitor_IsValidPair_SavedTogether()
    {
        var import = new JobDefinition
        {
            Name = "Vendor invoices",
            Group = "Import jobs",
            Schedule = new ScheduleOptions { Cron = "0 0/15 * * * ?" },
            Options = new ImportJobOptions
            {
                Connection = "TEST", DataProject = "VendInvoice", Company = "USMF", InputDir = @"C:\ris\in", UploadSuccessDir = @"C:\ris\uploaded",
                UploadErrorsDir = @"C:\ris\upload-errors", InputFilesArePackages = true, StatusFileExtension = ".Status"
            }
        };

        var monitor = JobTemplates.CreateMonitor(import, "Vendor invoices – monitor", @"C:\ris\processed", @"C:\ris\processing-errors");

        Assert.AreEqual("Vendor invoices – monitor", import.PairedJob);
        Assert.AreEqual("Vendor invoices", monitor.PairedJob);
        Assert.IsTrue(((ImportJobOptions)import.Options).WriteStatusFiles, "Import writes status files for the monitor.");
        var options = (ExecutionMonitorJobOptions)monitor.Options!;
        Assert.AreEqual(@"C:\ris\uploaded", options.UploadSuccessDir, "Monitor reads the status files where the import puts them.");
        Assert.AreEqual(".Status", options.StatusFileExtension);
        Assert.AreEqual(TimeSpan.FromMinutes(5), monitor.Schedule.Interval);

        await using var factory = new RisServerFactory();
        _ = factory.CreateClient();
        (await factory.Client("Admin").PutRisAsync("/api/v1/connections/TEST", RisTestHost.Connection)).EnsureSuccessStatusCode();
        var repository = factory.Services.GetRequiredService<ConfigurationRepository>();
        await repository.ImportAsync(new RisConfiguration { Jobs = [import, monitor] }, "test");

        var saved = await repository.LoadAsync();
        Assert.AreEqual("Vendor invoices – monitor", saved.FindJob("Import jobs.Vendor invoices")!.PairedJob);
        Assert.AreEqual(JobType.ExecutionMonitor, saved.FindJob("Import jobs.Vendor invoices – monitor")!.Type);
    }

    [TestMethod]
    public void UploadMonitor_KeepsActivity_ExportHasNone()
    {
        var upload = new JobDefinition
        {
            Name = "Customers",
            Options = new UploadJobOptions { Connection = "TEST", ActivityId = Guid.NewGuid(), InputDir = @"C:\in", UploadSuccessDir = @"C:\up", UploadErrorsDir = @"C:\err" }
        };
        var monitor = JobTemplates.CreateMonitor(upload, "Customers – monitor", @"C:\done", @"C:\failed");
        Assert.AreEqual(((UploadJobOptions)upload.Options).ActivityId, ((ProcessingMonitorJobOptions)monitor.Options!).ActivityId);

        Assert.IsFalse(JobTemplates.CanHaveMonitor(JobType.Export));
        Assert.ThrowsExactly<InvalidOperationException>(() => JobTemplates.CreateMonitor(new JobDefinition { Name = "E", Options = new ExportJobOptions() }, "m", "a", "b"));
    }
}
