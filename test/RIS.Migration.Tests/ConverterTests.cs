using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using RIS.Core.Configuration;
using RIS.Core.Options;
using RIS.Persistence;

namespace RIS.Migration.Tests;

[TestClass]
public sealed partial class ConverterTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RIS5.slnx")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    private const string Production4xSchedule = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <job-scheduling-data xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" version="2.0" xmlns="http://quartznet.sourceforge.net/JobSchedulingData">
          <processing-directives><overwrite-existing-data>true</overwrite-existing-data></processing-directives>
          <schedule>
            <job>
              <name>CustInvoicejour interface D365_AX2012</name>
              <group>Export jobs</group>
              <description>Customer invoices to AX 2012</description>
              <job-type>RecurringIntegrationsScheduler.Job.Export,RecurringIntegrationsScheduler.Job.Export, Version=4.2.3.0, Culture=neutral, PublicKeyToken=null</job-type>
              <durable>true</durable><recover>false</recover>
              <job-data-map>
                <entry><key>AosUri</key><value>https://contoso-prod.operations.dynamics.com/</value></entry>
                <entry><key>AzureAuthEndpoint</key><value>https://login.microsoftonline.com</value></entry>
                <entry><key>AadTenant</key><value>contoso.onmicrosoft.com</value></entry>
                <entry><key>UseServiceAuthentication</key><value>True</value></entry>
                <entry><key>AadClientId</key><value>aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee</value></entry>
                <entry><key>AadClientSecret</key><value>AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA</value></entry>
                <entry><key>DownloadSuccessDir</key><value>\\files\RIS\Export\CustInvoiceJour</value></entry>
                <entry><key>DownloadErrorsDir</key><value>\\files\RIS\Export\Errors</value></entry>
                <entry><key>DataProject</key><value>CustInvoicejour interface D365_AX2012</value></entry>
                <entry><key>Company</key><value>2020</value></entry>
                <entry><key>UnzipPackage</key><value>True</value></entry>
                <entry><key>AddTimestamp</key><value>True</value></entry>
                <entry><key>DeletePackage</key><value>False</value></entry>
                <entry><key>DelayBetweenStatusCheck</key><value>5</value></entry>
                <entry><key>DelayBetweenFiles</key><value>10</value></entry>
                <entry><key>RetryCount</key><value>3</value></entry>
                <entry><key>RetryDelay</key><value>60</value></entry>
                <entry><key>PauseJobOnException</key><value>False</value></entry>
                <entry><key>IndefinitePause</key><value>False</value></entry>
                <entry><key>LogVerbose</key><value>True</value></entry>
                <entry><key>UseSftpOutbound</key><value>True</value></entry>
                <entry><key>SftpOutboundHost</key><value>sftp.contoso.com</value></entry>
                <entry><key>SftpOutboundPort</key><value>22</value></entry>
                <entry><key>SftpOutboundUsername</key><value>ris</value></entry>
                <entry><key>SftpOutboundPassword</key><value>AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA</value></entry>
                <entry><key>SftpOutboundRemoteFolder</key><value>/out/invoices</value></entry>
                <entry><key>SftpOutboundFileMask</key><value>*.zip</value></entry>
                <entry><key>SftpOutboundServerName</key><value>Contoso SFTP</value></entry>
                <entry><key>ExportToPackageActionPath</key><value>data/DataManagementDefinitionGroups/Microsoft.Dynamics.DataEntities.ExportToPackage</value></entry>
              </job-data-map>
            </job>
            <job>
              <name>Pending vendor invoice</name>
              <group>Import jobs</group>
              <job-type>RecurringIntegrationsScheduler.Job.Import,RecurringIntegrationsScheduler.Job.Import, Version=4.2.3.0</job-type>
              <job-data-map>
                <entry><key>AosUri</key><value>https://contoso-prod.operations.dynamics.com</value></entry>
                <entry><key>AadTenant</key><value>contoso.onmicrosoft.com</value></entry>
                <entry><key>UseServiceAuthentication</key><value>True</value></entry>
                <entry><key>AadClientId</key><value>aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee</value></entry>
                <entry><key>AadClientSecret</key><value>AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA</value></entry>
                <entry><key>InputDir</key><value>\\files\RIS\Import\Input</value></entry>
                <entry><key>UploadSuccessDir</key><value>\\files\RIS\Import\Uploaded</value></entry>
                <entry><key>UploadErrorsDir</key><value>\\files\RIS\Import\Errors</value></entry>
                <entry><key>DataProject</key><value>Pending vendor invoice</value></entry>
                <entry><key>PackageTemplate</key><value>\\files\RIS\Templates\vendinv.zip</value></entry>
                <entry><key>StatusFileExtension</key><value>Status</value></entry>
                <entry><key>ExecutionJobPresent</key><value>True</value></entry>
                <entry><key>ExecuteImport</key><value>True</value></entry>
                <entry><key>MultiCompanyImport</key><value>True</value></entry>
                <entry><key>GetLegalEntityFromFilename</key><value>True</value></entry>
                <entry><key>FilenameSeparator</key><value>_</value></entry>
                <entry><key>LegalEntityTokenPosition</key><value>2</value></entry>
                <entry><key>OrderBy</key><value>FileName</value></entry>
                <entry><key>SearchPattern</key><value>*.csv</value></entry>
              </job-data-map>
            </job>
            <job>
              <name>Pending vendor invoice-Execution monitor</name>
              <group>Import jobs</group>
              <job-type>RecurringIntegrationsScheduler.Job.ExecutionMonitor,RecurringIntegrationsScheduler.Job.ExecutionMonitor</job-type>
              <job-data-map>
                <entry><key>AosUri</key><value>https://contoso-prod.operations.dynamics.com</value></entry>
                <entry><key>AadTenant</key><value>contoso.onmicrosoft.com</value></entry>
                <entry><key>UseServiceAuthentication</key><value>True</value></entry>
                <entry><key>AadClientId</key><value>aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee</value></entry>
                <entry><key>AadClientSecret</key><value>AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA</value></entry>
                <entry><key>UploadSuccessDir</key><value>\\files\RIS\Import\Uploaded</value></entry>
                <entry><key>ProcessingSuccessDir</key><value>\\files\RIS\Import\Processed</value></entry>
                <entry><key>ProcessingErrorsDir</key><value>\\files\RIS\Import\Failed</value></entry>
                <entry><key>StatusFileExtension</key><value>.Status</value></entry>
                <entry><key>DelayBetweenStatusCheck</key><value>2</value></entry>
                <entry><key>GetExecutionErrors</key><value>True</value></entry>
                <entry><key>SomethingNew</key><value>x</value></entry>
              </job-data-map>
            </job>
          </schedule>
          <schedule>
          </schedule>
        </job-scheduling-data>
        """;

    private const string Triggers = """
            <trigger><cron><name>t1</name><group>Export jobs</group><job-name>CustInvoicejour interface D365_AX2012</job-name><job-group>Export jobs</job-group>
              <misfire-instruction>SmartPolicy</misfire-instruction><cron-expression>0 0 2 * * ?</cron-expression><time-zone>Central European Standard Time</time-zone></cron></trigger>
            <trigger><calendar-interval><name>t2</name><group>Import jobs</group><job-name>Pending vendor invoice</job-name><job-group>Import jobs</job-group>
              <repeat-interval>15</repeat-interval><repeat-interval-unit>Minute</repeat-interval-unit></calendar-interval></trigger>
            <trigger><simple><name>t3</name><group>Import jobs</group><job-name>Pending vendor invoice-Execution monitor</job-name><job-group>Import jobs</job-group>
              <repeat-count>-1</repeat-count><repeat-interval>300000</repeat-interval></simple></trigger>
        """;

    private static ConversionResult ConvertProductionSample() =>
        new ScheduleXmlConverter().Convert(XDocument.Parse(WithTriggers()));

    /// <summary>The sample with triggers added to its first schedule element (line endings normalized, the file may be checked out with CRLF).</summary>
    private static string WithTriggers() =>
        Production4xSchedule.ReplaceLineEndings("\n").Replace("</job>\n  </schedule>", "</job>" + Triggers + "</schedule>");

    [TestMethod]
    public void ProductionLikeSchedule_IsConvertedWithAllSettings()
    {
        var result = ConvertProductionSample();
        var configuration = result.Configuration;

        Assert.AreEqual(3, configuration.Jobs.Count, string.Join(Environment.NewLine, result.Warnings));
        Assert.AreEqual(1, configuration.Connections.Count, "Same environment must become one D365 instance.");
        Assert.AreEqual("CONTOSO-PROD", configuration.Connections[0].Name);
        Assert.AreEqual(new Uri("https://contoso-prod.operations.dynamics.com"), configuration.Connections[0].AosUri);
        Assert.AreEqual(1, configuration.AppRegistrations.Count, "Same app must become one app registration.");
        Assert.AreEqual("APP-AAAAAAAA", configuration.AppRegistrations[0].Name);
        Assert.AreEqual("<SET-SECRET>", configuration.AppRegistrations[0].ClientSecret);
        Assert.AreEqual("APP-AAAAAAAA", configuration.Connections[0].AppRegistration, "Default app registration of the instance.");
        Assert.IsFalse(configuration.Connections[0].HasInlineSignIn);
        Assert.IsTrue(configuration.Jobs.All(j => j.Options!.AppRegistration is null), "Jobs use the default of their instance.");

        var export = configuration.FindJob("CustInvoicejour interface D365_AX2012")!;
        var exportOptions = (ExportJobOptions)export.Options!;
        Assert.AreEqual("0 0 2 * * ?", export.Schedule.Cron);
        Assert.AreEqual("Central European Standard Time", export.Schedule.TimeZone);
        Assert.AreEqual("2020", exportOptions.Company);
        Assert.IsTrue(exportOptions.UnzipPackage && exportOptions.AddTimestamp && !exportOptions.DeletePackage);
        Assert.AreEqual(TimeSpan.FromSeconds(5), exportOptions.Polling.InitialDelay);
        Assert.AreEqual(TimeSpan.FromHours(12), exportOptions.Polling.Timeout);
        Assert.AreEqual(TimeSpan.FromSeconds(10), exportOptions.DelayBetweenFiles);
        Assert.AreEqual(3, exportOptions.Retry.Count);
        Assert.AreEqual("Contoso SFTP", exportOptions.SftpOutbound!.Server);
        Assert.AreEqual("/out/invoices", exportOptions.SftpOutbound.RemoteFolder);
        Assert.AreEqual("sftp.contoso.com", configuration.FindSftpServer("Contoso SFTP")!.Host);

        var import = configuration.FindJob("Pending vendor invoice")!;
        var importOptions = (ImportJobOptions)import.Options!;
        Assert.AreEqual(TimeSpan.FromMinutes(15), import.Schedule.Interval);
        Assert.AreEqual(".Status", importOptions.StatusFileExtension, "4.x stored extension without dot.");
        Assert.AreEqual(LegalEntitySource.FileName, importOptions.MultiCompany!.Source);
        Assert.AreEqual(2, importOptions.MultiCompany.TokenPosition);
        Assert.AreEqual(FileOrder.FileName, importOptions.Files.OrderBy);
        Assert.IsTrue(importOptions.WriteStatusFiles);
        Assert.AreEqual("Pending vendor invoice-Execution monitor", import.PairedJob);

        var monitor = configuration.FindJob("Pending vendor invoice-Execution monitor")!;
        Assert.AreEqual(TimeSpan.FromMinutes(5), monitor.Schedule.Interval);
        Assert.AreEqual(TimeSpan.FromSeconds(2), monitor.Options!.DelayBetweenFiles, "Monitors used DelayBetweenStatusCheck between files.");
        Assert.AreEqual("Pending vendor invoice", monitor.PairedJob);

        Assert.AreEqual(TimeSpan.Zero, import.Schedule.StartAt!.Value.TimeOfDay, "4.x without start-time: rhythm anchored at midnight.");
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("Import jobs.Pending vendor invoice: 4.x did not save the start time")));
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("'SomethingNew'")), "Unknown keys are reported.");
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("was not decrypted")));
        Assert.AreEqual(0, ConfigurationValidator.Validate(configuration).Count(e => !e.Contains("path")),
            string.Join(Environment.NewLine, ConfigurationValidator.Validate(configuration)));
    }

    [TestMethod]
    public void StartTimeSavedBy4x_IsUsedAsReferenceOfTheRhythm()
    {
        var xml = WithTriggers()
            .Replace("<repeat-interval>15</repeat-interval>", "<start-time>2026-10-01T12:00:00+02:00</start-time><repeat-interval>15</repeat-interval>");

        var result = new ScheduleXmlConverter().Convert(XDocument.Parse(xml));

        Assert.AreEqual(DateTimeOffset.Parse("2026-10-01T12:00:00+02:00"), result.Configuration.FindJob("Pending vendor invoice")!.Schedule.StartAt);
        Assert.IsFalse(result.Warnings.Any(w => w.Contains("Import jobs.Pending vendor invoice: 4.x did not save the start time")));
    }

    [TestMethod]
    public void TestFixturesFrom4x_AreConverted()
    {
        // Every job is either converted or reported with a reason - never silently lost.
        // (Schedule.Test.xml contains jobs that 4.x itself would reject, e.g. Export without DataProject.)
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot, "Tests", "TestData", "Schedules"), "*.xml"))
        {
            var document = XDocument.Load(file);
            var jobsInFile = document.Descendants().Count(e => e.Name.LocalName == "job");
            var result = new ScheduleXmlConverter().Convert(document);
            var rejected = result.Warnings.Count(w => w.Contains("job was not converted"));
            Assert.IsTrue(result.Configuration.Jobs.Count > 0, file);
            Assert.AreEqual(jobsInFile, result.Configuration.Jobs.Count + rejected, $"{file}: {string.Join("; ", result.Warnings)}");
        }
    }

    [GeneratedRegex("""const string (\w+) = "([^"]+)";""")]
    private static partial Regex ConstantPattern();

    [TestMethod]
    public void Every4xSettingKey_IsConvertedOrDeliberatelyIgnored()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "Common", "Contracts", "SettingsConstants.cs"));
        var keys = ConstantPattern().Matches(source).Select(m => m.Groups[2].Value)
            .Where(v => !v.StartsWith("RecurringIntegrationsScheduler.", StringComparison.Ordinal)) // job type names
            .ToList();
        var consumed = (HashSet<string>)typeof(ScheduleXmlConverter).GetField("ConsumedKeys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        var missing = keys.Where(k => !consumed.Contains(k) && !ScheduleXmlConverter.IgnoredKeys.ContainsKey(k)).ToList();

        Assert.IsTrue(keys.Count > 80, "SettingsConstants should be parsed.");
        Assert.AreEqual(0, missing.Count, "4.x keys without mapping: " + string.Join(", ", missing));
    }

    [TestMethod]
    public async Task Cli_ConvertValidateAndDryRun()
    {
        var folder = Directory.CreateTempSubdirectory("ris-cli-").FullName;
        var schedule = Path.Combine(folder, "Schedule.xml");
        var jobs = Path.Combine(folder, "jobs.json");
        await File.WriteAllTextAsync(schedule, WithTriggers());

        var convert = await Cli.Program.BuildCommand().Parse(["convert-schedule", schedule, "-o", jobs]).InvokeAsync();
        var dryRun = await Cli.Program.BuildCommand().Parse(["run", jobs, "--job", "CustInvoicejour interface D365_AX2012", "--dry-run"]).InvokeAsync();
        var missing = await Cli.Program.BuildCommand().Parse(["run", jobs, "--job", "does not exist", "--dry-run"]).InvokeAsync();

        Assert.AreEqual(0, convert);
        Assert.IsTrue(File.Exists(jobs));
        Assert.AreEqual(3, RisJson.LoadConfiguration(jobs).Jobs.Count);
        Assert.AreEqual(0, dryRun);
        Assert.AreEqual(2, missing);
    }

    [TestMethod]
    public async Task Cli_DbImportExportAndRunFromDatabase()
    {
        var folder = Directory.CreateTempSubdirectory("ris-cli-db-").FullName;
        var data = Path.Combine(folder, "data");
        var schedule = Path.Combine(folder, "Schedule.xml");
        var jobs = Path.Combine(folder, "jobs.json");
        var exported = Path.Combine(folder, "exported.json");
        await File.WriteAllTextAsync(schedule, WithTriggers());
        Assert.AreEqual(0, await Cli.Program.BuildCommand().Parse(["convert-schedule", schedule, "-o", jobs]).InvokeAsync());

        var import = await Cli.Program.BuildCommand().Parse(["db", "import", jobs, "--data", data]).InvokeAsync();
        var export = await Cli.Program.BuildCommand().Parse(["db", "export", "-o", exported, "--data", data]).InvokeAsync();
        Assert.AreEqual(0, await Cli.Program.BuildCommand().Parse(["db", "import", jobs, "--data", data, "--paused"]).InvokeAsync());
        var dryRun = await Cli.Program.BuildCommand().Parse(["run", "--db", "--data", data, "--job", "CustInvoicejour interface D365_AX2012", "--dry-run"]).InvokeAsync();
        var noSource = await Cli.Program.BuildCommand().Parse(["run", "--job", "x", "--dry-run"]).InvokeAsync();

        Assert.AreEqual(0, import);
        Assert.IsTrue(File.Exists(Path.Combine(data, "ris.db")));
        Assert.AreEqual(0, export);
        var configuration = RisJson.LoadConfiguration(exported);
        Assert.AreEqual(3, configuration.Jobs.Count);
        Assert.AreEqual(1, configuration.Connections.Count);
        Assert.AreEqual(0, dryRun);
        Assert.AreEqual(2, noSource);
        Assert.IsFalse(configuration.Jobs.All(j => j.Schedule.Paused));

        // --paused: imported jobs are paused (parallel run with 4.x).
        var pausedExport = Path.Combine(folder, "paused.json");
        Assert.AreEqual(0, await Cli.Program.BuildCommand().Parse(["db", "export", "-o", pausedExport, "--data", data]).InvokeAsync());
        Assert.IsTrue(RisJson.LoadConfiguration(pausedExport).Jobs.All(j => j.Schedule.Paused));

        // Invalid file: nothing is imported, exit code 1.
        configuration.Jobs[0].Options!.Connection = "MISSING";
        RisJson.SaveConfiguration(configuration, jobs);
        Assert.AreEqual(1, await Cli.Program.BuildCommand().Parse(["db", "import", jobs, "--data", data]).InvokeAsync());

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    [TestMethod]
    public async Task Cli_Import4x_TakesOverSecretsOfThisMachine_WithoutWritingThemToFiles()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("DPAPI is Windows only.");
        const string secret = "s3cret-from-4x";
        // As 4.x stored it: DPAPI LocalMachine of the 4.x server, UTF-16, base64.
        var encrypted = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.Unicode.GetBytes(secret), null, System.Security.Cryptography.DataProtectionScope.LocalMachine));
        var folder = Directory.CreateTempSubdirectory("ris-cli-4x-").FullName;
        var data = Path.Combine(folder, "data");
        var report = Path.Combine(folder, "report");
        var schedule = Path.Combine(folder, "Schedule.xml");
        // The SFTP password of the sample stays undecryptable (another machine).
        var xml = WithTriggers();
        var first = xml.IndexOf("AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA", StringComparison.Ordinal);
        await File.WriteAllTextAsync(schedule, xml[..first] + encrypted + xml[(first + 32)..].Replace(
            "<key>AadClientSecret</key><value>AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA", "<key>AadClientSecret</key><value>" + encrypted));

        var exit = await Cli.Program.BuildCommand().Parse(["db", "import-4x", schedule, "--data", data, "--paused", "--report", report, "--no-secret-check"]).InvokeAsync();

        Assert.AreEqual(0, exit);
        var reported = await File.ReadAllTextAsync(Path.Combine(report, "jobs.json"));
        Assert.IsFalse(reported.Contains(secret, StringComparison.Ordinal), "The report never contains secrets.");
        StringAssert.Contains(reported, "SET-SECRET");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(data, "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.IsFalse(Contains(bytes, System.Text.Encoding.UTF8.GetBytes(secret)) || Contains(bytes, System.Text.Encoding.Unicode.GetBytes(secret)), $"Plain secret in {file}");
        }

        var persistence = new PersistenceOptions { DataDirectory = data };
        await using (var services = new ServiceCollection().AddLogging().AddRisPersistence(persistence).BuildServiceProvider())
        {
            var repository = services.GetRequiredService<ConfigurationRepository>();
            var configuration = await repository.LoadAsync(CancellationToken.None);
            Assert.AreEqual(secret, configuration.ResolveConnection(configuration.Connections.Single().Name).ClientSecret, "Decrypted on the 4.x server and stored by RIS 5.");
            Assert.IsTrue(configuration.SftpServers.All(s => SecretPlaceholder.IsPlaceholderOrEmpty(s.Password)), "Not decryptable: left to be set.");
            Assert.IsTrue(configuration.Jobs.All(j => j.Schedule.Paused));
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    private static string Dpapi(string secret) => Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
        System.Text.Encoding.Unicode.GetBytes(secret), null, System.Security.Cryptography.DataProtectionScope.LocalMachine));

    /// <summary>The sample schedule with the client secrets of its three jobs (one connection) replaced in order.</summary>
    private static string WithClientSecrets(params string[] encrypted)
    {
        // Every DPAPI blob starts with the bytes of the sample value, so the jobs are numbered first, then filled in.
        const string placeholder = "<key>AadClientSecret</key><value>AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA";
        var xml = WithTriggers();
        for (var i = 0; xml.Contains(placeholder, StringComparison.Ordinal); i++)
        {
            var index = xml.IndexOf(placeholder, StringComparison.Ordinal);
            xml = xml[..index] + $"<key>AadClientSecret</key><value>@SECRET{i}@" + xml[(index + placeholder.Length)..];
        }
        for (var i = 0; i < encrypted.Length; i++) xml = xml.Replace($"@SECRET{i}@", encrypted[i], StringComparison.Ordinal);
        return xml;
    }

    [TestMethod]
    public void JobsWithDifferentSecrets_AllAreCandidates_TheMostUsedIsTheDefault()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("DPAPI is Windows only.");
        // 4.x copies the secret into every job; renewed in two jobs only.
        var result = new ScheduleXmlConverter(new ConversionOptions { DecryptSecrets = true })
            .Convert(XDocument.Parse(WithClientSecrets(Dpapi("old-secret"), Dpapi("new-secret"), Dpapi("new-secret"))));

        var registration = result.Configuration.AppRegistrations.Single();
        Assert.AreEqual("new-secret", registration.ClientSecret, "The secret carried by most jobs.");
        var candidates = result.ClientSecretCandidates[registration.Name];
        CollectionAssert.AreEqual(new[] { "new-secret", "old-secret" }, candidates.Select(c => c.Value).ToList());
        Assert.AreEqual(2, candidates[0].Jobs.Count);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("2 different client secrets") && !w.Contains("secret\"") && !w.Contains("new-secret")),
            "Warned, without the secret values.");
    }

    [TestMethod]
    public async Task Cli_Import4x_SecretsOnly_SetsTheSecrets_JobsUntouched()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("DPAPI is Windows only.");
        var folder = Directory.CreateTempSubdirectory("ris-cli-4x-secrets-").FullName;
        var data = Path.Combine(folder, "data");
        var schedule = Path.Combine(folder, "Schedule.xml");
        await File.WriteAllTextAsync(schedule, WithClientSecrets(Dpapi("old-secret"), Dpapi("old-secret"), Dpapi("old-secret")));
        Assert.AreEqual(0, await Cli.Program.BuildCommand().Parse(["db", "import-4x", schedule, "--data", data, "--paused", "--no-secret-check"]).InvokeAsync());

        // The secret was renewed in 4.x afterwards (in two of the three jobs).
        await File.WriteAllTextAsync(schedule, WithClientSecrets(Dpapi("old-secret"), Dpapi("new-secret"), Dpapi("new-secret")));
        Assert.AreEqual(0, await Cli.Program.BuildCommand().Parse(["db", "import-4x", schedule, "--data", data, "--secrets-only", "--no-secret-check"]).InvokeAsync());

        var persistence = new PersistenceOptions { DataDirectory = data };
        await using (var services = new ServiceCollection().AddLogging().AddRisPersistence(persistence).BuildServiceProvider())
        {
            var configuration = await services.GetRequiredService<ConfigurationRepository>().LoadAsync(CancellationToken.None);
            Assert.AreEqual("new-secret", configuration.ResolveConnection(configuration.Connections.Single().Name).ClientSecret);
            Assert.AreEqual(3, configuration.Jobs.Count);
            Assert.IsTrue(configuration.Jobs.All(j => j.Schedule.Paused), "Jobs not touched (still paused).");
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>portable.config of the 4.x client (PortableSettingsProvider), app registrations as nested XML.</summary>
    private static string ClientSettings(string clientId, string encryptedSecret) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <userSettings>
            <Roaming>
              <setting name="AadApplications" serializeAs="Xml">
                <value>
                  <ArrayOfAadApplication xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
                    <AadApplication><Name>OTHER APP</Name><ClientId>11111111-2222-3333-4444-555555555555</ClientId><Secret>{encryptedSecret}</Secret><AuthenticationType>Service</AuthenticationType></AadApplication>
                    <AadApplication><Name>CONTOSO APP</Name><ClientId>{clientId}</ClientId><Secret>{encryptedSecret}</Secret><AuthenticationType>Service</AuthenticationType></AadApplication>
                  </ArrayOfAadApplication>
                </value>
              </setting>
            </Roaming>
          </userSettings>
        </configuration>
        """;

    [TestMethod]
    public void ClientSettings_AppRegistrations_AreRead_AsNestedOrEscapedXml()
    {
        var folder = Directory.CreateTempSubdirectory("ris-client-settings-").FullName;
        var nested = Path.Combine(folder, "nested.config");
        var escaped = Path.Combine(folder, "escaped.config");
        var xml = ClientSettings("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "QUJD");
        File.WriteAllText(nested, xml);
        var start = xml.IndexOf("<ArrayOfAadApplication", StringComparison.Ordinal);
        var end = xml.IndexOf("</ArrayOfAadApplication>", StringComparison.Ordinal) + "</ArrayOfAadApplication>".Length;
        File.WriteAllText(escaped, xml[..start] + System.Net.WebUtility.HtmlEncode(xml[start..end]) + xml[end..]);

        foreach (var file in new[] { nested, escaped })
        {
            var registrations = ClientSettingsReader.ReadAppRegistrations(file);
            Assert.AreEqual(2, registrations.Count, file);
            Assert.AreEqual(new AppRegistration("CONTOSO APP", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "QUJD", true), registrations[1]);
        }
        Directory.Delete(folder, recursive: true);
    }

    [TestMethod]
    public async Task Cli_Import4x_SecretOfTheClientAppRegistration_ComesFirst()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("DPAPI is Windows only.");
        var folder = Directory.CreateTempSubdirectory("ris-cli-4x-client-").FullName;
        var data = Path.Combine(folder, "data");
        var schedule = Path.Combine(folder, "Schedule.xml");
        // The jobs carry the old secret; the secret was renewed in the client's app registration only (as found on a test server).
        await File.WriteAllTextAsync(schedule, WithClientSecrets(Dpapi("old-secret"), Dpapi("old-secret"), Dpapi("old-secret")));
        Assert.AreEqual(0, await Cli.Program.BuildCommand().Parse(["db", "import-4x", schedule, "--data", data, "--paused", "--no-secret-check"]).InvokeAsync());
        Assert.AreEqual("old-secret", await StoredSecretAsync(data));

        await File.WriteAllTextAsync(Path.Combine(folder, ClientSettingsReader.FileName), ClientSettings("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", Dpapi("renewed-secret")));
        Assert.AreEqual(0, await Cli.Program.BuildCommand().Parse(["db", "import-4x", schedule, "--data", data, "--secrets-only", "--no-secret-check"]).InvokeAsync());

        Assert.AreEqual("renewed-secret", await StoredSecretAsync(data), "portable.config next to Schedule.xml is read and its secret comes first.");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    [TestMethod]
    public void AppRegistrations_NamedAfterThe4xClient_JobWithAnotherApp_KeepsItsOwn()
    {
        // The third job signs in with another app registration than the other two jobs of the same environment.
        var xml = WithTriggers();
        var last = xml.LastIndexOf("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", StringComparison.Ordinal);
        Assert.IsTrue(last > 0);
        xml = xml[..last] + "11111111-2222-3333-4444-555555555555" + xml[(last + 36)..];
        var result = new ScheduleXmlConverter(new ConversionOptions
        {
            AppRegistrationNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["AAAAAAAA-bbbb-cccc-dddd-eeeeeeeeeeee"] = "CONTOSO APP" }
        }).Convert(XDocument.Parse(xml));
        var configuration = result.Configuration;

        CollectionAssert.AreEquivalent(new[] { "CONTOSO APP", "APP-11111111" }, configuration.AppRegistrations.Select(a => a.Name).ToList());
        var instance = configuration.Connections.Single();
        Assert.AreEqual("CONTOSO APP", instance.AppRegistration, "The app registration of most jobs is the default, named as in the 4.x client.");
        var own = configuration.Jobs.Where(j => j.Options!.AppRegistration is not null).ToList();
        Assert.AreEqual(1, own.Count);
        Assert.AreEqual("APP-11111111", own[0].Options!.AppRegistration);
        Assert.AreEqual("11111111-2222-3333-4444-555555555555", configuration.ResolveConnection(own[0].Options!.Connection, own[0].Options!.AppRegistration).ClientId);
        Assert.AreEqual(0, ConfigurationValidator.Validate(configuration).Count(e => e.Contains("pp registration", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, ConfigurationValidator.Validate(configuration)));
    }

    private static async Task<string?> StoredSecretAsync(string data)
    {
        await using var services = new ServiceCollection().AddLogging().AddRisPersistence(new PersistenceOptions { DataDirectory = data }).BuildServiceProvider();
        var configuration = await services.GetRequiredService<ConfigurationRepository>().LoadAsync(CancellationToken.None);
        return configuration.ResolveConnection(configuration.Connections.Single().Name).ClientSecret;
    }

    [TestMethod]
    public async Task Cli_Access_AddListRemove()
    {
        var data = Directory.CreateTempSubdirectory("ris-cli-access-").FullName;
        Task<int> Run(params string[] args) => Cli.Program.BuildCommand().Parse([.. args, "--data", data]).InvokeAsync();

        Assert.AreEqual(0, await Run("access", "add", "Viewer", "S-1-5-32-545"));
        Assert.AreEqual(0, await Run("access", "add", "viewer", "S-1-5-32-545"), "Again: already has the role, nothing changes.");
        Assert.AreEqual(1, await Run("access", "add", "Viewer", @"NO-SUCH-DOMAIN\nobody-ris5"));
        Assert.AreEqual(0, await Run("access", "list"));

        var persistence = new PersistenceOptions { DataDirectory = data };
        await using (var services = new ServiceCollection().AddLogging().AddRisPersistence(persistence).BuildServiceProvider())
        {
            var members = await services.GetRequiredService<ConfigurationRepository>().GetRoleMembersAsync();
            Assert.AreEqual(1, members.Count);
            Assert.AreEqual("Viewer", members[0].Role);
            Assert.AreEqual("S-1-5-32-545", members[0].Sid);
        }

        Assert.AreEqual(0, await Run("access", "remove", "Viewer", "S-1-5-32-545"));
        Assert.AreEqual(2, await Run("access", "remove", "Viewer", "S-1-5-32-545"), "Nothing to remove.");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(data, recursive: true); } catch (IOException) { }
    }

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}
