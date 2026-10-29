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

    private static AppRegistrationOptions Registration(string secret = "s3cr3t-value") => new()
    {
        Name = "RIS-APP", Tenant = "contoso.onmicrosoft.com", ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", ClientSecret = secret
    };

    private static D365ConnectionOptions Instance(string name = "PROD") => new()
    {
        Name = name, AosUri = new Uri($"https://{name.ToLowerInvariant()}.operations.dynamics.com"), AppRegistration = "RIS-APP"
    };

    [TestMethod]
    public async Task Secret_IsStoredEncrypted_AndReadBack()
    {
        await _repository.SaveAppRegistrationAsync(Registration(), "tester");
        await _repository.SaveConnectionAsync(Instance(), "tester");

        var configuration = await _repository.LoadAsync();
        Assert.AreEqual("s3cr3t-value", configuration.FindAppRegistration("RIS-APP")!.ClientSecret);
        Assert.AreEqual("s3cr3t-value", configuration.ResolveConnection("PROD").ClientSecret, "The instance signs in with its app registration.");
        var raw = RawColumn("SELECT ProtectedClientSecret FROM AppRegistrations");
        Assert.IsFalse(raw.Contains("s3cr3t", StringComparison.Ordinal), "Secret must not be stored in plain text.");
        Assert.IsTrue(raw.Length > 50);
        Assert.AreEqual("", RawColumn("SELECT ProtectedClientSecret FROM Connections"), "No copy in the instance.");
    }

    [TestMethod]
    public async Task SwitchingAuthMode_RemovesCredentialsOfTheOtherMode()
    {
        await _repository.SaveAppRegistrationAsync(Registration(), "tester");

        var certificate = Registration();
        certificate.ClientSecret = null;
        certificate.AuthMode = D365AuthMode.Certificate;
        certificate.CertificateThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";
        await _repository.SaveAppRegistrationAsync(certificate, "tester");

        var saved = (await _repository.LoadAsync()).FindAppRegistration("RIS-APP")!;
        Assert.AreEqual(D365AuthMode.Certificate, saved.AuthMode);
        Assert.IsNull(saved.ClientSecret, "The client secret is removed when the registration switches to a certificate.");
        Assert.AreEqual("", RawColumn("SELECT ProtectedClientSecret FROM AppRegistrations"));

        // Back to a secret: the thumbprint goes; the old secret does not come back (the form requires a new one).
        await _repository.SaveAppRegistrationAsync(Registration(secret: ""), "tester");
        Assert.IsTrue(SecretPlaceholder.IsPlaceholderOrEmpty((await _repository.LoadAsync()).FindAppRegistration("RIS-APP")!.ClientSecret), "Not set (placeholder).");
        await _repository.SaveAppRegistrationAsync(Registration("new-secret"), "tester");
        saved = (await _repository.LoadAsync()).FindAppRegistration("RIS-APP")!;
        Assert.AreEqual("new-secret", saved.ClientSecret);
        Assert.IsNull(saved.CertificateThumbprint);
    }

    [TestMethod]
    public async Task SftpSwitchingToKey_RemovesPassword_AndBack()
    {
        var server = new SftpServerOptions { Name = "PARTNER", Host = "sftp.contoso.com", UserName = "ris", Password = "pw" };
        await _repository.SaveSftpServerAsync(server, "tester");

        await _repository.SaveSftpServerAsync(new SftpServerOptions { Name = "PARTNER", Host = "sftp.contoso.com", UserName = "ris", UseKey = true, KeyPath = @"C:\keys\id" }, "tester");
        var saved = (await _repository.LoadAsync()).FindSftpServer("PARTNER")!;
        Assert.IsNull(saved.Password);
        Assert.AreEqual(@"C:\keys\id", saved.KeyPath);

        await _repository.SaveSftpServerAsync(new SftpServerOptions { Name = "PARTNER", Host = "sftp.contoso.com", UserName = "ris" }, "tester");
        Assert.IsTrue(SecretPlaceholder.IsPlaceholderOrEmpty((await _repository.LoadAsync()).FindSftpServer("PARTNER")!.Password), "The old password does not come back.");
        await _repository.SaveSftpServerAsync(server, "tester");
        saved = (await _repository.LoadAsync()).FindSftpServer("PARTNER")!;
        Assert.AreEqual("pw", saved.Password);
        Assert.IsNull(saved.KeyPath);
    }

    [TestMethod]
    public async Task IntervalWindow_IsSavedAndLoaded()
    {
        await _repository.SaveConnectionAsync(Connection(), "tester");
        var job = Job();
        job.Schedule = new ScheduleOptions
        {
            Interval = TimeSpan.FromMinutes(10), StartAt = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.FromHours(2)),
            ActiveFrom = new TimeOnly(8, 0), ActiveTo = new TimeOnly(16, 0), Days = [DayOfWeek.Monday, DayOfWeek.Friday]
        };
        await _repository.SaveJobAsync(job, "tester");

        var schedule = (await _repository.LoadAsync()).Jobs.Single().Schedule;
        Assert.AreEqual(new TimeOnly(8, 0), schedule.ActiveFrom);
        Assert.AreEqual(new TimeOnly(16, 0), schedule.ActiveTo);
        CollectionAssert.AreEqual(new[] { DayOfWeek.Monday, DayOfWeek.Friday }, schedule.Days);
    }

    [TestMethod]
    public void DefinitionHash_IgnoresPauseState_AndIsUnchangedForJobsThatAreNotPaused()
    {
        var job = Job();
        // Formula before one pause state (5.0.0-local.29): hashes of running jobs must stay, or every job is rescheduled once.
        var before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(job.Schedule, RisJson.Options) + "\n" + System.Text.Json.JsonSerializer.Serialize(job.Options, RisJson.Options))));
        Assert.AreEqual(before, ConfigurationRepository.ComputeHash(job));

        var paused = Job();
        paused.Schedule.SetPaused(true, "maintenance", "operator", DateTimeOffset.UtcNow);
        Assert.AreEqual(before, ConfigurationRepository.ComputeHash(paused), "Pause or resume is no reschedule.");
    }

    [TestMethod]
    public async Task IndefinitePauseFrom4x_IsLoadedAsPause()
    {
        await _repository.SaveConnectionAsync(Connection(), "tester");
        var json = System.Text.Json.JsonSerializer.Serialize(Job(), RisJson.Options).Replace("\"indefinitePause\": false", "\"indefinitePause\": true");
        StringAssert.Contains(json, "indefinitePause\": true");
        await _repository.ImportAsync(new RisConfiguration { Connections = [Connection()], Jobs = [System.Text.Json.JsonSerializer.Deserialize<JobDefinition>(json, RisJson.Options)!] }, "import");

        var schedule = (await _repository.LoadAsync()).Jobs.Single().Schedule;
        Assert.IsTrue(schedule.Paused);
        Assert.AreEqual(PauseReasons.IndefinitePause, schedule.PauseReason);
        Assert.AreEqual("import", schedule.PausedBy);
    }

    [TestMethod]
    public async Task Audit_MarksSecretChanges_WithoutValues_AndIsReadable()
    {
        await _repository.SaveAppRegistrationAsync(Registration(), "tester");                  // created with a secret
        await _repository.SaveAppRegistrationAsync(Registration("replaced-secret"), "tester"); // secret replaced
        await _repository.SaveAppRegistrationAsync(Registration(secret: ""), "tester");         // saved without a new secret

        var audit = (await _repository.GetAuditAsync()).OrderBy(a => a.TimestampUtc).ThenBy(a => a.Id).ToList();
        Assert.AreEqual(3, audit.Count);
        Assert.IsTrue(audit.All(a => a.EntityType == "AppRegistration"));
        StringAssert.Contains(audit[0].After, $"\"clientSecret\": \"{ConfigurationRepository.AuditSecretChanged}\"");
        StringAssert.Contains(audit[1].Before, $"\"clientSecret\": \"{ConfigurationRepository.AuditSecretStored}\"");
        StringAssert.Contains(audit[1].After, $"\"clientSecret\": \"{ConfigurationRepository.AuditSecretChanged}\"");
        StringAssert.Contains(audit[2].After, $"\"clientSecret\": \"{ConfigurationRepository.AuditSecretStored}\"");
        Assert.IsTrue(audit.All(a => !(a.Before + a.After).Contains("secret-value") && !(a.Before + a.After).Contains("replaced-secret")));
        Assert.IsTrue(audit.All(a => !(a.Before + a.After).Contains(@"\u00")), @"No \u escapes in audit JSON.");
    }

    [TestMethod]
    public async Task Audit_DoesNotContainSecrets_AndExportUsesPlaceholders()
    {
        await _repository.SaveAppRegistrationAsync(Registration(), "tester");
        await _repository.SaveConnectionAsync(Instance(), "tester");
        // "Secret of the instance" (ris db set-secret connection PROD) is the secret of its app registration.
        await _repository.SetSecretAsync(SecretOwner.Connection, "PROD", "another-secret", "tester");

        var audit = await _repository.GetAuditAsync();
        Assert.AreEqual(3, audit.Count);
        Assert.AreEqual("AppRegistration", audit[0].EntityType, "Newest first: the secret change of the app registration.");
        Assert.IsTrue(audit.All(a => !(a.After ?? "").Contains("s3cr3t") && !(a.After ?? "").Contains("another-secret")));
        Assert.AreEqual("tester", audit[0].User);
        Assert.AreEqual(SecretPlaceholder.Value, (await _repository.ExportAsync()).FindAppRegistration("RIS-APP")!.ClientSecret);
        Assert.AreEqual("another-secret", (await _repository.LoadAsync()).ResolveConnection("PROD").ClientSecret);
    }

    [TestMethod]
    public async Task Import_WithPlaceholderSecret_KeepsStoredSecret()
    {
        await _repository.SaveAppRegistrationAsync(Registration(), "tester");
        await _repository.SaveConnectionAsync(Instance(), "tester");
        var exported = await _repository.ExportAsync();
        exported.Jobs.Add(Job());

        await _repository.ImportAsync(exported, "tester");

        var loaded = await _repository.LoadAsync();
        Assert.AreEqual("s3cr3t-value", loaded.ResolveConnection("PROD").ClientSecret);
        Assert.AreEqual(1, loaded.Jobs.Count);
        Assert.AreEqual(JobType.Export, loaded.Jobs[0].Type);
    }

    [TestMethod]
    public async Task InstancesWithInlineSignIn_AreMovedToSharedAppRegistrations()
    {
        // API/CLI of older versions and files from before app registrations: the sign-in comes with the instance.
        var clientId = Guid.NewGuid().ToString();
        D365ConnectionOptions Inline(string name, string secret) => new()
        {
            Name = name, AosUri = new Uri($"https://{name.ToLowerInvariant()}.operations.dynamics.com"), Tenant = "contoso.onmicrosoft.com", ClientId = clientId, ClientSecret = secret
        };
        await _repository.SaveConnectionAsync(Inline("UAT", "shared-secret"), "tester");
        await _repository.ImportAsync(new RisConfiguration { Connections = [Inline("TEST", "shared-secret"), Inline("DEV", "other-secret")] }, "tester");

        var configuration = await _repository.LoadAsync();
        Assert.AreEqual(2, configuration.AppRegistrations.Count, "Same sign-in shared, a different secret of the same app separate.");
        Assert.AreEqual(configuration.FindConnection("UAT")!.AppRegistration, configuration.FindConnection("TEST")!.AppRegistration);
        Assert.AreNotEqual(configuration.FindConnection("UAT")!.AppRegistration, configuration.FindConnection("DEV")!.AppRegistration);
        Assert.AreEqual("other-secret", configuration.ResolveConnection("DEV").ClientSecret);
        Assert.AreEqual("", RawColumn("SELECT GROUP_CONCAT(ProtectedClientSecret, '') FROM Connections"), "No secrets left in the instances.");
        Assert.IsTrue((await _repository.GetAuditAsync()).Any(a => a.EntityType == "AppRegistration" && a.Action == "Created"));
    }

    [TestMethod]
    public async Task Startup_MovesInlineSignInOfAnOlderDatabase()
    {
        // A database written by a version without app registrations: secret in the instance row.
        var protector = _services.GetRequiredService<ISecretProtector>();
        var contexts = _services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<RisDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync())
        {
            foreach (var name in new[] { "PROD", "TEST" })
            {
                db.Connections.Add(new ConnectionEntity
                {
                    Name = name, AosUri = $"https://{name.ToLowerInvariant()}.operations.dynamics.com/", Tenant = "contoso.onmicrosoft.com",
                    Authority = "https://login.microsoftonline.com/", ClientId = "c72d6454-e523-4f05-ac23-8d4d89e27fe5", ProtectedClientSecret = protector.Protect("old-db-secret")
                });
            }
            await db.SaveChangesAsync();
        }

        await DatabaseMaintenance.MigrateAsync(_services);

        var configuration = await _repository.LoadAsync();
        Assert.AreEqual("APP-C72D6454", configuration.AppRegistrations.Single().Name);
        Assert.AreEqual("old-db-secret", configuration.ResolveConnection("PROD").ClientSecret);
        Assert.AreEqual("old-db-secret", configuration.ResolveConnection("TEST").ClientSecret);
        Assert.AreEqual(2, (await _repository.GetAuditAsync()).Count(a => a.Action == "Migrated"));
        Assert.AreEqual(0, await _repository.MoveInlineSignInAsync("again"), "Once.");
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
        await store.RefreshAsync();
        var reloaded = 0;
        store.Reloaded += () => reloaded++;

        await _repository.SaveConnectionAsync(Connection(), "tester");

        Assert.AreEqual(1, reloaded);
        Assert.IsNotNull(store.Current.FindConnection("PROD"));
    }

    [TestMethod]
    public async Task QueryAudit_PagesNewestFirst_AndSearchesInTheDatabase()
    {
        await _repository.SaveConnectionAsync(Connection(), "alice");
        for (var i = 0; i < 4; i++)
        {
            var job = Job();
            job.Name = $"Job {i}";
            job.Description = i == 2 ? "needle_100%" : null;
            await _repository.SaveJobAsync(job, "bob");
        }

        var (first, total) = await _repository.QueryAuditAsync(0, 2, null);
        Assert.AreEqual(6, total, "App registration created from the inline sign-in, instance, 4 jobs.");
        CollectionAssert.AreEqual(new[] { "Export jobs.Job 3", "Export jobs.Job 2" }, first.Select(e => e.EntityKey).ToArray());
        var (second, _) = await _repository.QueryAuditAsync(2, 2, null);
        CollectionAssert.AreEqual(new[] { "Export jobs.Job 1", "Export jobs.Job 0" }, second.Select(e => e.EntityKey).ToArray());

        Assert.AreEqual(2, (await _repository.QueryAuditAsync(0, 50, "alice")).Total);
        Assert.AreEqual("Export jobs.Job 2", (await _repository.QueryAuditAsync(0, 50, "needle_100%")).Items.Single().EntityKey, "Searches before/after JSON; _ and % are literal.");
        Assert.AreEqual(0, (await _repository.QueryAuditAsync(0, 50, "needle_1000")).Total);
    }

    [TestMethod]
    public async Task Import_SameJobTwice_LastOneWins_NoDuplicate()
    {
        await _repository.SaveConnectionAsync(Connection(), "tester");
        var first = Job();
        var second = Job("0 0 5 * * ?");
        await _repository.ImportAsync(new RisConfiguration { Jobs = [first, second] }, "tester");

        var jobs = (await _repository.LoadAsync()).Jobs;
        Assert.AreEqual(1, jobs.Count);
        Assert.AreEqual("0 0 5 * * ?", jobs[0].Schedule.Cron);
    }

    [TestMethod]
    public async Task DeleteJobs_OneTransaction_RejectedAsAWhole()
    {
        await _repository.SaveConnectionAsync(Connection(), "tester");
        var a = Job();
        var b = Job();
        b.Name = "Second";
        await _repository.SaveJobAsync(a, "tester");
        await _repository.SaveJobAsync(b, "tester");

        await _repository.DeleteJobsAsync([("Export jobs", "CustInvoiceJour"), ("Export jobs", "Second")], "tester");
        Assert.AreEqual(0, (await _repository.LoadAsync()).Jobs.Count);
        Assert.AreEqual(2, (await _repository.GetAuditAsync()).Count(e => e.Action == "Deleted"));

        await _repository.SaveJobAsync(a, "tester");
        await Assert.ThrowsExactlyAsync<ConfigurationNotFoundException>(() => _repository.DeleteJobsAsync([("Export jobs", "CustInvoiceJour"), ("Export jobs", "Missing")], "tester"));
        Assert.AreEqual(1, (await _repository.LoadAsync()).Jobs.Count, "Nothing deleted when one of them fails.");
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

    [TestMethod]
    public async Task SaveJobs_IsAllOrNothing_WithAuditPerJob()
    {
        await _repository.SaveConnectionAsync(Connection(), "tester");
        var first = Job();
        var second = Job();
        second.Name = "Vendors";
        await _repository.SaveJobAsync(first, "tester");
        await _repository.SaveJobAsync(second, "tester");
        var changed = 0;
        _repository.Changed += () => changed++;

        // One invalid job: nothing is saved.
        var valid = Job("0 30 3 * * ?");
        var invalid = Job();
        invalid.Name = "Vendors";
        invalid.Options!.Connection = "MISSING";
        await Assert.ThrowsExactlyAsync<ConfigurationValidationException>(() => _repository.SaveJobsAsync(
            [new JobChange("Export jobs", "CustInvoiceJour", valid), new JobChange("Export jobs", "Vendors", invalid)], "bulk"));
        Assert.AreEqual("0 0 2 * * ?", (await _repository.LoadAsync()).FindJob("CustInvoiceJour")!.Schedule.Cron);
        Assert.AreEqual(0, changed);

        // Valid changes, one of them moves the job to another group.
        var moved = Job("0 0 4 * * ?");
        moved.Name = "Vendors";
        moved.Group = "Master data";
        await _repository.SaveJobsAsync([new JobChange("Export jobs", "CustInvoiceJour", valid), new JobChange("Export jobs", "Vendors", moved)], "bulk");

        var configuration = await _repository.LoadAsync();
        Assert.AreEqual("0 30 3 * * ?", configuration.FindJob("Export jobs.CustInvoiceJour")!.Schedule.Cron);
        Assert.IsNotNull(configuration.FindJob("Master data.Vendors"));
        Assert.IsNull(configuration.FindJob("Export jobs.Vendors"));
        Assert.AreEqual(1, changed, "One change event for the whole bulk edit.");
        var audit = await _repository.GetAuditAsync();
        Assert.AreEqual(1, audit.Count(a => a.Action == "BulkUpdated"));
        Assert.AreEqual(2, audit.Count(a => a.Action == "Updated" && a.User == "bulk"));
        Assert.AreEqual(1, audit.Count(a => a.Action == "Moved"));
    }
}
