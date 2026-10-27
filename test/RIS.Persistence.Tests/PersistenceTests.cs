using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RIS.Core.Configuration;
using RIS.Core.Options;

namespace RIS.Persistence.Tests;

[TestClass]
public sealed class PersistenceTests
{
    private string _dataDirectory = null!;
    private ServiceProvider _services = null!;
    private ConfigurationRepository _repository = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _dataDirectory = Directory.CreateTempSubdirectory("ris-db-").FullName;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRisPersistence(new PersistenceOptions { DataDirectory = _dataDirectory });
        _services = services.BuildServiceProvider();
        await DatabaseMaintenance.MigrateAsync(_services);
        _repository = _services.GetRequiredService<ConfigurationRepository>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dataDirectory, recursive: true);
    }

    private static D365ConnectionOptions Connection(string secret = "s3cr3t-value") => new()
    {
        Name = "PROD",
        AosUri = new Uri("https://contoso.operations.dynamics.com"),
        Tenant = "contoso.onmicrosoft.com",
        ClientId = Guid.NewGuid().ToString(),
        ClientSecret = secret
    };

    private static JobDefinition Job(string cron = "0 0 2 * * ?") => new()
    {
        Name = "CustInvoiceJour",
        Group = "Export jobs",
        Schedule = new ScheduleOptions { Cron = cron },
        Options = new ExportJobOptions { Connection = "PROD", DataProject = "CustInvoiceJour", Company = "USMF", DownloadSuccessDir = @"C:\ris\export" }
    };

    private string RawColumn(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataDirectory, "ris.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    [TestMethod]
    public async Task Secret_IsStoredEncrypted_AndReadBack()
    {
        await _repository.SaveConnectionAsync(Connection(), "tester");

        Assert.AreEqual("s3cr3t-value", (await _repository.LoadAsync()).FindConnection("PROD")!.ClientSecret);
        var raw = RawColumn("SELECT ProtectedClientSecret FROM Connections");
        Assert.IsFalse(raw.Contains("s3cr3t", StringComparison.Ordinal), "Secret must not be stored in plain text.");
        Assert.IsTrue(raw.Length > 50);
    }

    [TestMethod]
    public async Task Audit_DoesNotContainSecrets_AndExportUsesPlaceholders()
    {
        await _repository.SaveConnectionAsync(Connection(), "tester");
        await _repository.SetSecretAsync(SecretOwner.Connection, "PROD", "another-secret", "tester");

        var audit = await _repository.GetAuditAsync();
        Assert.AreEqual(2, audit.Count);
        Assert.IsTrue(audit.All(a => !(a.After ?? "").Contains("s3cr3t") && !(a.After ?? "").Contains("another-secret")));
        Assert.AreEqual("tester", audit[0].User);
        Assert.AreEqual(SecretPlaceholder.Value, (await _repository.ExportAsync()).FindConnection("PROD")!.ClientSecret);
        Assert.AreEqual("another-secret", (await _repository.LoadAsync()).FindConnection("PROD")!.ClientSecret);
    }

    [TestMethod]
    public async Task Import_WithPlaceholderSecret_KeepsStoredSecret()
    {
        await _repository.SaveConnectionAsync(Connection(), "tester");
        var exported = await _repository.ExportAsync();
        exported.Jobs.Add(Job());

        await _repository.ImportAsync(exported, "tester");

        var loaded = await _repository.LoadAsync();
        Assert.AreEqual("s3cr3t-value", loaded.FindConnection("PROD")!.ClientSecret);
        Assert.AreEqual(1, loaded.Jobs.Count);
        Assert.AreEqual(JobType.Export, loaded.Jobs[0].Type);
    }

    [TestMethod]
    public async Task DeletingConnectionUsedByJob_IsRejected()
    {
        await _repository.SaveConnectionAsync(Connection(), "tester");
        await _repository.SaveJobAsync(Job(), "tester");

        var ex = await Assert.ThrowsExactlyAsync<ConfigurationValidationException>(() => _repository.DeleteConnectionAsync("PROD", "tester"));

        StringAssert.Contains(ex.Message, "connection 'PROD' is not defined");
        Assert.IsNotNull((await _repository.LoadAsync()).FindConnection("PROD"), "Rejected change is rolled back.");
    }

    [TestMethod]
    public async Task InvalidJob_IsRejectedWithValidatorMessage()
    {
        await _repository.SaveConnectionAsync(Connection(), "tester");
        var job = Job();
        job.Schedule = new ScheduleOptions { Interval = TimeSpan.FromHours(2) };

        var ex = await Assert.ThrowsExactlyAsync<ConfigurationValidationException>(() => _repository.SaveJobAsync(job, "tester"));

        StringAssert.Contains(ex.Message, "StartAt is required");
        Assert.AreEqual(0, (await _repository.LoadAsync()).Jobs.Count);
    }

    [TestMethod]
    public async Task Hash_ChangesOnlyWhenScheduleOrOptionsChange()
    {
        Assert.AreEqual(ConfigurationRepository.ComputeHash(Job()), ConfigurationRepository.ComputeHash(Job()));
        Assert.AreNotEqual(ConfigurationRepository.ComputeHash(Job()), ConfigurationRepository.ComputeHash(Job("0 0 3 * * ?")));

        await _repository.SaveConnectionAsync(Connection(), "tester");
        await _repository.SaveJobAsync(Job(), "tester");
        Assert.AreEqual(ConfigurationRepository.ComputeHash(Job()), (await _repository.GetJobHashesAsync())["Export jobs.CustInvoiceJour"]);
    }

    [TestMethod]
    public async Task ConfigurationStore_IsRefreshedAfterChange()
    {
        var store = _services.GetRequiredService<DbConfigurationStore>();
        store.Refresh();
        var reloaded = 0;
        store.Reloaded += () => reloaded++;

        await _repository.SaveConnectionAsync(Connection(), "tester");

        Assert.AreEqual(1, reloaded);
        Assert.IsNotNull(store.Current.FindConnection("PROD"));
    }

    [TestMethod]
    public async Task Migration_OnExistingDatabase_MakesNoChangesWhenUpToDate_AndBackupWorks()
    {
        await _repository.SaveConnectionAsync(Connection(), "tester");
        await DatabaseMaintenance.MigrateAsync(_services); // already up to date

        Assert.IsFalse(Directory.Exists(Path.Combine(_dataDirectory, "backup")), "No backup when nothing to migrate.");

        var backup = await DatabaseMaintenance.BackupAsync(Path.Combine(_dataDirectory, "ris.db"), Path.Combine(_dataDirectory, "backup"), 2, "test");
        Assert.IsTrue(File.Exists(backup));
        using var connection = new SqliteConnection($"Data Source={backup}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Connections";
        Assert.AreEqual(1L, command.ExecuteScalar());
    }
}
