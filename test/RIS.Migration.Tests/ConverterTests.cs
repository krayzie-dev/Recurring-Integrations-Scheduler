using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RIS.Core.Configuration;
using RIS.Core.Options;

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
        new ScheduleXmlConverter().Convert(XDocument.Parse(Production4xSchedule.Replace("</schedule>\r\n  <schedule>", "</schedule>").Replace("<schedule>\n  </schedule>", "")
            .Replace("  </schedule>\n  <schedule>\n  </schedule>", Triggers + "  </schedule>")
            .Replace("</job>\r\n  </schedule>", "</job>" + Triggers + "</schedule>")
            .Replace("</job>\n  </schedule>", "</job>" + Triggers + "</schedule>")));

    [TestMethod]
    public void ProductionLikeSchedule_IsConvertedWithAllSettings()
    {
        var result = ConvertProductionSample();
        var configuration = result.Configuration;

        Assert.AreEqual(3, configuration.Jobs.Count, string.Join(Environment.NewLine, result.Warnings));
        Assert.AreEqual(1, configuration.Connections.Count, "Same environment and app must become one connection.");
        Assert.AreEqual("CONTOSO-PROD", configuration.Connections[0].Name);
        Assert.AreEqual(new Uri("https://contoso-prod.operations.dynamics.com"), configuration.Connections[0].AosUri);
        Assert.AreEqual("<SET-SECRET>", configuration.Connections[0].ClientSecret);

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

        Assert.IsTrue(result.Warnings.Any(w => w.Contains("'SomethingNew'")), "Unknown keys are reported.");
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("was not decrypted")));
        Assert.AreEqual(0, ConfigurationValidator.Validate(configuration).Count(e => !e.Contains("path")),
            string.Join(Environment.NewLine, ConfigurationValidator.Validate(configuration)));
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
        await File.WriteAllTextAsync(schedule, Production4xSchedule.Replace("</job>\r\n  </schedule>", "</job>" + Triggers + "</schedule>")
            .Replace("</job>\n  </schedule>", "</job>" + Triggers + "</schedule>"));

        var convert = await Cli.Program.BuildCommand().Parse(["convert-schedule", schedule, "-o", jobs]).InvokeAsync();
        var dryRun = await Cli.Program.BuildCommand().Parse(["run", jobs, "--job", "CustInvoicejour interface D365_AX2012", "--dry-run"]).InvokeAsync();
        var missing = await Cli.Program.BuildCommand().Parse(["run", jobs, "--job", "does not exist", "--dry-run"]).InvokeAsync();

        Assert.AreEqual(0, convert);
        Assert.IsTrue(File.Exists(jobs));
        Assert.AreEqual(3, RisJson.LoadConfiguration(jobs).Jobs.Count);
        Assert.AreEqual(0, dryRun);
        Assert.AreEqual(2, missing);
    }
}
