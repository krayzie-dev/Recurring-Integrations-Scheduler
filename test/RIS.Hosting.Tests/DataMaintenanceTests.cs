using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RIS.Core.Options;
using RIS.Persistence;

namespace RIS.Hosting.Tests;

[TestClass]
public sealed class DataMaintenanceTests
{
    [TestMethod]
    public async Task Cleanup_RemovesOldLogsEmptyJobFoldersOldBackups_AndKeepsNewestAuditEntry()
    {
        var data = Directory.CreateTempSubdirectory("ris-maint-").FullName;
        try
        {
            var persistence = new PersistenceOptions { DataDirectory = data };
            await using var services = new ServiceCollection().AddLogging().AddRisPersistence(persistence).BuildServiceProvider();
            await DatabaseMaintenance.MigrateAsync(services);
            var repository = services.GetRequiredService<ConfigurationRepository>();
            for (var i = 0; i < 3; i++)
            {
                await repository.SaveConnectionAsync(new D365ConnectionOptions
                {
                    Name = "C" + i, AosUri = new Uri("https://contoso.operations.dynamics.com"), Tenant = "t", ClientId = Guid.NewGuid().ToString(), ClientSecret = "s"
                }, "tester");
            }
            var version = await repository.GetVersionAsync();

            var logs = Path.Combine(data, "logs");
            Directory.CreateDirectory(Path.Combine(logs, "jobs", "Deleted job"));
            Directory.CreateDirectory(Path.Combine(logs, "jobs", "Live job"));
            var old = Path.Combine(logs, "jobs", "Deleted job", "job-20260101.clef");
            var fresh = Path.Combine(logs, "jobs", "Live job", "job-20261028.clef");
            File.WriteAllText(old, "{}");
            File.WriteAllText(fresh, "{}");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-40));
            for (var i = 1; i <= 7; i++)
            {
                Directory.CreateDirectory(Path.Combine(persistence.BackupDirectory, $"before-upgrade-2026102{i}-120000"));
                File.WriteAllText(Path.Combine(data, $"appsettings.json.2026102{i}-120000.bak"), "{}");
            }

            var retention = new RetentionOptions { AuditDays = -1, UpgradeBackupsToKeep = 5, SettingsBackupsToKeep = 2 };
            var host = new RisHostOptions { LogDirectory = logs, LogRetentionDays = 30 };
            var service = new DataMaintenanceService(retention, host, persistence, repository, TimeProvider.System, NullLogger<DataMaintenanceService>.Instance);

            var result = await service.RunAsync();

            Assert.IsTrue(result.Succeeded, string.Join("; ", result.Errors));
            Assert.AreEqual(5, result.AuditEntriesDeleted, "All but the newest entry (3 instances, each with its app registration).");
            Assert.AreEqual(version, await repository.GetVersionAsync(), "The configuration version must not change.");
            Assert.AreEqual(1, result.LogFilesDeleted);
            Assert.AreEqual(1, result.LogFoldersDeleted);
            Assert.IsTrue(File.Exists(fresh));
            Assert.IsFalse(Directory.Exists(Path.Combine(logs, "jobs", "Deleted job")));
            CollectionAssert.AreEqual(new[] { "before-upgrade-20261023-120000", "before-upgrade-20261024-120000", "before-upgrade-20261025-120000", "before-upgrade-20261026-120000", "before-upgrade-20261027-120000" },
                Directory.GetDirectories(persistence.BackupDirectory, "before-upgrade-*").Select(Path.GetFileName).Order().ToArray());
            Assert.AreEqual(2, Directory.GetFiles(data, "appsettings.json.*.bak").Length);
            Assert.AreEqual(7, result.BackupsDeleted);
            Assert.AreSame(result, service.LastResult);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(data, recursive: true);
        }
    }

    [TestMethod]
    public void NextDaily_TodayIfStillAhead_OtherwiseTomorrow()
    {
        var at = new TimeOnly(3, 30);
        var offset = TimeSpan.FromHours(1);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 28, 3, 30, 0, offset), DataMaintenanceService.NextDaily(new DateTimeOffset(2026, 10, 28, 1, 0, 0, offset), at));
        Assert.AreEqual(new DateTimeOffset(2026, 10, 29, 3, 30, 0, offset), DataMaintenanceService.NextDaily(new DateTimeOffset(2026, 10, 28, 3, 30, 0, offset), at));
    }
}
