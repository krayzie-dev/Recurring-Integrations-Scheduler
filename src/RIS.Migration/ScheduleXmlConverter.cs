using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using RIS.Core.Configuration;
using RIS.Core.Options;

namespace RIS.Migration;

/// <summary>Result of converting a 4.x schedule.</summary>
public sealed class ConversionResult
{
    public RisConfiguration Configuration { get; } = new();

    /// <summary>Things that need attention (unknown keys, secrets not decrypted, defaults applied).</summary>
    public List<string> Warnings { get; } = [];
}

public sealed class ConversionOptions
{
    /// <summary>Decrypt 4.x secrets (DPAPI LocalMachine). Works only on the machine where they were encrypted.</summary>
    public bool DecryptSecrets { get; set; }

    /// <summary>Value used for secrets that are not decrypted.</summary>
    public string SecretPlaceholder { get; set; } = "<SET-SECRET>";
}

/// <summary>
/// Converts RIS 4.x Schedule.xml (Quartz job-scheduling-data with JobDataMap keys) into RIS 5.0 configuration.
/// Every 4.x key is either mapped, or listed in <see cref="IgnoredKeys"/> with a reason (see docs/ris5/parity-4x.md).
/// </summary>
public sealed class ScheduleXmlConverter(ConversionOptions? options = null)
{
    private static readonly XNamespace Ns = "http://quartznet.sourceforge.net/JobSchedulingData";
    private readonly ConversionOptions _options = options ?? new ConversionOptions();

    /// <summary>4.x keys intentionally not converted.</summary>
    public static readonly IReadOnlyDictionary<string, string> IgnoredKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["UseADAL"] = "ADAL was removed in 4.x already.",
        ["LogVerbose"] = "Replaced by log levels (Serilog configuration).",
        ["JobKey"] = "Derived from job group and name.",
        ["UploadInOrder"] = "Not used by 4.x.",
        ["DelayBetweenStatusCheck"] = "Mapped per job type (polling initial delay / delay between files).",
        ["SftpInboundServerName"] = "Used as SFTP server name.",
        ["SftpOutboundServerName"] = "Used as SFTP server name.",
        ["SftpInboundCredentialName"] = "Credentials are part of the SFTP server entry.",
        ["SftpOutboundCredentialName"] = "Credentials are part of the SFTP server entry."
    };

    public ConversionResult Convert(XDocument document)
    {
        var result = new ConversionResult();
        var schedule = document.Root?.Element(Ns + "schedule") ?? throw new InvalidDataException("Not a Quartz job-scheduling-data document (missing <schedule>).");
        var triggers = schedule.Elements(Ns + "trigger").Select(t => t.Elements().First()).ToList();

        foreach (var job in schedule.Elements(Ns + "job"))
        {
            var name = Value(job, "name") ?? "unnamed";
            var group = Value(job, "group") ?? "DEFAULT";
            var key = $"{group}.{name}";
            try
            {
                var data = ReadJobDataMap(job);
                var definition = new JobDefinition
                {
                    Name = name,
                    Group = group,
                    Description = Value(job, "description"),
                    Options = ConvertOptions(Value(job, "job-type") ?? string.Empty, data, key, result),
                    Schedule = ConvertTrigger(triggers.FirstOrDefault(t => Value(t, "job-name") == name && (Value(t, "job-group") ?? "DEFAULT") == group), key, result)
                };

                foreach (var dataKey in data.Keys.Where(k => !ConsumedKeys.Contains(k) && !IgnoredKeys.ContainsKey(k)))
                {
                    result.Warnings.Add($"{key}: 4.x setting '{dataKey}' is not known and was not converted.");
                }
                result.Configuration.Jobs.Add(definition);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException)
            {
                result.Warnings.Add($"{key}: job was not converted: {ex.Message}");
            }
        }

        LinkMonitors(result.Configuration);
        return result;
    }

    private static readonly HashSet<string> ConsumedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "AosUri", "AzureAuthEndpoint", "AadTenant", "UseServiceAuthentication", "AadClientId", "AadClientSecret", "UserName", "UserPassword",
        "ActivityId", "DelayBetweenFiles", "MaxDelayBetweenStatusCheck", "StatusCheckTimeoutMinutes", "ErrorKeysFileTimeoutMinutes", "RetryCount", "RetryDelay",
        "PauseJobOnException", "IndefinitePause",
        "ImportFromPackageActionPath", "GetAzureWriteUrlActionPath", "GetExecutionSummaryStatusActionPath", "GetExportedPackageUrlActionPath",
        "GetExecutionSummaryPageUrlActionPath", "DeleteExecutionHistoryJobActionPath", "ExportToPackageActionPath", "ExportFromPackageActionPath",
        "GetMessageStatusActionPath", "GenerateImportTargetErrorKeysFilePath", "GetImportTargetErrorKeysFileUrlPath", "GetExecutionErrorsPath",
        "InputDir", "UseSftpInbound", "UseSftpOutbound",
        "SftpInboundHost", "SftpInboundPort", "SftpInboundUsername", "SftpInboundPassword", "SftpInboundUseKey", "SftpInboundKeyPath", "SftpInboundKeyPassphrase", "SftpInboundRemoteFolder", "SftpInboundFileMask",
        "SftpOutboundHost", "SftpOutboundPort", "SftpOutboundUsername", "SftpOutboundPassword", "SftpOutboundUseKey", "SftpOutboundKeyPath", "SftpOutboundKeyPassphrase", "SftpOutboundRemoteFolder", "SftpOutboundFileMask",
        "UploadSuccessDir", "UploadErrorsDir", "EntityName", "IsDataPackage", "Company", "StatusFileExtension", "ProcessingJobPresent", "SearchPattern",
        "OrderBy", "ReverseOrder", "ProcessingSuccessDir", "ProcessingErrorsDir", "DownloadSuccessDir", "DownloadErrorsDir", "UnzipPackage", "AddTimestamp",
        "DeletePackage", "ExecutionJobPresent", "DataProject", "OverwriteDataProject", "ExecuteImport", "PackageTemplate", "GetImportTargetErrorKeysFile",
        "MultiCompanyImport", "GetLegalEntityFromFilename", "GetLegalEntityFromSubfolder", "FilenameSeparator", "LegalEntityTokenPosition",
        "InputFilesArePackages", "GetExecutionErrors"
    };

    private JobOptionsBase ConvertOptions(string jobType, JobData data, string key, ConversionResult result)
    {
        var type = jobType.Split(',')[0].Trim();
        JobOptionsBase options = type switch
        {
            "RecurringIntegrationsScheduler.Job.Export" => new ExportJobOptions
            {
                DataProject = data.String("DataProject"),
                Company = data.String("Company"),
                DownloadSuccessDir = data.String("DownloadSuccessDir"),
                DownloadErrorsDir = data.OptionalString("DownloadErrorsDir"),
                UnzipPackage = data.Bool("UnzipPackage"),
                AddTimestamp = data.Bool("AddTimestamp"),
                DeletePackage = data.Bool("DeletePackage"),
                Polling = Polling(data),
                SftpOutbound = Sftp(data, "Outbound", key, result)
            },
            "RecurringIntegrationsScheduler.Job.Import" => new ImportJobOptions
            {
                DataProject = data.String("DataProject"),
                Company = data.OptionalString("Company"),
                InputDir = data.String("InputDir"),
                UploadSuccessDir = data.String("UploadSuccessDir"),
                UploadErrorsDir = data.String("UploadErrorsDir"),
                Files = Files(data),
                InputFilesArePackages = data.Bool("InputFilesArePackages"),
                PackageTemplate = data.OptionalString("PackageTemplate"),
                OverwriteDataProject = data.Bool("OverwriteDataProject"),
                ExecuteImport = data.Bool("ExecuteImport"),
                MultiCompany = MultiCompany(data),
                WriteStatusFiles = data.Bool("ExecutionJobPresent"),
                StatusFileExtension = StatusExtension(data),
                SftpInbound = Sftp(data, "Inbound", key, result)
            },
            "RecurringIntegrationsScheduler.Job.Upload" => new UploadJobOptions
            {
                ActivityId = data.Guid("ActivityId"),
                EntityName = data.OptionalString("EntityName"),
                IsDataPackage = data.Bool("IsDataPackage"),
                Company = data.OptionalString("Company"),
                InputDir = data.String("InputDir"),
                UploadSuccessDir = data.String("UploadSuccessDir"),
                UploadErrorsDir = data.String("UploadErrorsDir"),
                Files = Files(data),
                WriteStatusFiles = data.Bool("ProcessingJobPresent"),
                StatusFileExtension = StatusExtension(data),
                SftpInbound = Sftp(data, "Inbound", key, result)
            },
            "RecurringIntegrationsScheduler.Job.Download" => new DownloadJobOptions
            {
                ActivityId = data.Guid("ActivityId"),
                DownloadSuccessDir = data.String("DownloadSuccessDir"),
                DownloadErrorsDir = data.String("DownloadErrorsDir"),
                UnzipPackage = data.Bool("UnzipPackage"),
                AddTimestamp = data.Bool("AddTimestamp"),
                DeletePackage = data.Bool("DeletePackage"),
                SftpOutbound = Sftp(data, "Outbound", key, result)
            },
            "RecurringIntegrationsScheduler.Job.ExecutionMonitor" => new ExecutionMonitorJobOptions
            {
                UploadSuccessDir = data.String("UploadSuccessDir"),
                ProcessingSuccessDir = data.String("ProcessingSuccessDir"),
                ProcessingErrorsDir = data.String("ProcessingErrorsDir"),
                StatusFileExtension = StatusExtension(data),
                GetImportTargetErrorKeysFile = data.Bool("GetImportTargetErrorKeysFile"),
                PackageTemplate = data.OptionalString("PackageTemplate"),
                GetExecutionErrors = data.Bool("GetExecutionErrors"),
                Polling = Polling(data),
                ErrorKeysFileTimeout = TimeSpan.FromMinutes(data.Int("ErrorKeysFileTimeoutMinutes", 30))
            },
            "RecurringIntegrationsScheduler.Job.ProcessingMonitor" => new ProcessingMonitorJobOptions
            {
                ActivityId = data.Guid("ActivityId"),
                UploadSuccessDir = data.String("UploadSuccessDir"),
                ProcessingSuccessDir = data.String("ProcessingSuccessDir"),
                ProcessingErrorsDir = data.String("ProcessingErrorsDir"),
                StatusFileExtension = StatusExtension(data),
                GetExecutionErrors = data.Bool("GetExecutionErrors"),
                Polling = Polling(data)
            },
            _ => throw new InvalidDataException($"Unknown job type '{type}'.")
        };

        // Monitors used DelayBetweenStatusCheck as delay between checked files; other jobs use DelayBetweenFiles.
        options.DelayBetweenFiles = TimeSpan.FromSeconds(options is ExecutionMonitorJobOptions or ProcessingMonitorJobOptions
            ? data.Int("DelayBetweenStatusCheck", 0)
            : data.Int("DelayBetweenFiles", 0));
        options.Retry = new RetryOptions
        {
            Count = data.Int("RetryCount", 1) <= 0 ? 1 : data.Int("RetryCount", 1),
            Delay = TimeSpan.FromSeconds(data.Int("RetryDelay", 10) <= 0 ? 10 : data.Int("RetryDelay", 10))
        };
        options.PauseOnError = data.Bool("PauseJobOnException");
        options.IndefinitePause = data.Bool("IndefinitePause");
        options.ActionPaths = ActionPaths(data);
        options.Connection = Connection(data, key, result).Name;
        return options;
    }

    private D365ConnectionOptions Connection(JobData data, string key, ConversionResult result)
    {
        var aosUri = new Uri(data.String("AosUri").TrimEnd('/'));
        var clientId = data.String("AadClientId");
        var tenant = data.String("AadTenant");
        var serviceAuth = data.Bool("UseServiceAuthentication");

        var existing = result.Configuration.Connections.FirstOrDefault(c =>
            c.AosUri == aosUri && string.Equals(c.ClientId, clientId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Tenant, tenant, StringComparison.OrdinalIgnoreCase) &&
            (c.AuthMode == D365AuthMode.ClientSecret) == serviceAuth &&
            (serviceAuth || string.Equals(c.UserName, data.OptionalString("UserName"), StringComparison.OrdinalIgnoreCase)));
        if (existing is not null)
        {
            return existing;
        }

        var baseName = aosUri.Host.Split('.')[0].ToUpperInvariant();
        var name = baseName;
        for (var i = 2; result.Configuration.Connections.Any(c => c.Name == name); i++)
        {
            name = $"{baseName}-{i}";
        }

#pragma warning disable CS0618 // username/password is converted as-is from 4.x
        var connection = new D365ConnectionOptions
        {
            Name = name,
            AosUri = aosUri,
            Tenant = tenant,
            Authority = new Uri(data.OptionalString("AzureAuthEndpoint") ?? "https://login.microsoftonline.com"),
            ClientId = clientId,
            AuthMode = serviceAuth ? D365AuthMode.ClientSecret : D365AuthMode.UsernamePassword,
            ClientSecret = serviceAuth ? Secret(data.OptionalString("AadClientSecret"), $"{name}.ClientSecret", key, result) : null,
            UserName = serviceAuth ? null : data.OptionalString("UserName"),
            UserPassword = serviceAuth ? null : Secret(data.OptionalString("UserPassword"), $"{name}.UserPassword", key, result)
        };
#pragma warning restore CS0618
        if (!serviceAuth)
        {
            result.Warnings.Add($"Connection {name}: uses username/password authentication (no MFA). Consider an app registration with client secret or certificate.");
        }
        result.Configuration.Connections.Add(connection);
        return connection;
    }

    private SftpTransferOptions? Sftp(JobData data, string direction, string key, ConversionResult result)
    {
        if (!data.Bool($"UseSftp{direction}"))
        {
            return null;
        }

        var host = data.String($"Sftp{direction}Host");
        var port = data.Int($"Sftp{direction}Port", 22);
        var user = data.String($"Sftp{direction}Username");
        var server = result.Configuration.SftpServers.FirstOrDefault(s =>
            string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase) && s.Port == port && string.Equals(s.UserName, user, StringComparison.Ordinal));
        if (server is null)
        {
            var name = data.OptionalString($"Sftp{direction}ServerName") ?? host;
            if (result.Configuration.SftpServers.Any(s => s.Name == name))
            {
                name = $"{name}-{user}";
            }

            var useKey = data.Bool($"Sftp{direction}UseKey");
            server = new SftpServerOptions
            {
                Name = name,
                Host = host,
                Port = port,
                UserName = user,
                UseKey = useKey,
                KeyPath = data.OptionalString($"Sftp{direction}KeyPath"),
                Password = useKey ? null : Secret(data.OptionalString($"Sftp{direction}Password"), $"SFTP {name}.Password", key, result),
                KeyPassphrase = useKey ? Secret(data.OptionalString($"Sftp{direction}KeyPassphrase"), $"SFTP {name}.KeyPassphrase", key, result, optional: true) : null
            };
            result.Configuration.SftpServers.Add(server);
        }

        return new SftpTransferOptions
        {
            Server = server.Name,
            RemoteFolder = data.OptionalString($"Sftp{direction}RemoteFolder") ?? "/",
            FileMask = data.OptionalString($"Sftp{direction}FileMask") ?? "*.*"
        };
    }

    private string? Secret(string? encrypted, string what, string key, ConversionResult result, bool optional = false)
    {
        if (string.IsNullOrEmpty(encrypted))
        {
            if (!optional)
            {
                result.Warnings.Add($"{key}: secret {what} is empty in 4.x schedule.");
            }
            return optional ? null : _options.SecretPlaceholder;
        }

        if (!_options.DecryptSecrets)
        {
            result.Warnings.Add($"{key}: secret {what} was not decrypted; set it before running the job.");
            return _options.SecretPlaceholder;
        }

        if (!OperatingSystem.IsWindows())
        {
            result.Warnings.Add($"{key}: secret {what} cannot be decrypted outside Windows.");
            return _options.SecretPlaceholder;
        }

        try
        {
            return DecryptDpapi(encrypted);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            result.Warnings.Add($"{key}: secret {what} cannot be decrypted on this machine ({ex.Message}); set it manually.");
            return _options.SecretPlaceholder;
        }
    }

    /// <summary>4.x: Base64 of DPAPI (LocalMachine, no entropy) over UTF-16 text.</summary>
    [SupportedOSPlatform("windows")]
    public static string DecryptDpapi(string base64) =>
        Encoding.Unicode.GetString(ProtectedData.Unprotect(System.Convert.FromBase64String(base64), null, DataProtectionScope.LocalMachine));

    private static PollingOptions Polling(JobData data) => new()
    {
        InitialDelay = TimeSpan.FromSeconds(Math.Max(data.Int("DelayBetweenStatusCheck", 5), 1)),
        MaxDelay = TimeSpan.FromSeconds(data.Int("MaxDelayBetweenStatusCheck", 300)),
        Timeout = TimeSpan.FromMinutes(data.Int("StatusCheckTimeoutMinutes", 720))
    };

    private static FileSelectionOptions Files(JobData data) => new()
    {
        SearchPattern = data.OptionalString("SearchPattern") ?? "*.*",
        OrderBy = Enum.TryParse<FileOrder>(data.OptionalString("OrderBy"), ignoreCase: true, out var order) ? order : FileOrder.Created,
        Reverse = data.Bool("ReverseOrder")
    };

    private static MultiCompanyOptions? MultiCompany(JobData data)
    {
        if (!data.Bool("MultiCompanyImport"))
        {
            return null;
        }

        return data.Bool("GetLegalEntityFromFilename")
            ? new MultiCompanyOptions { Source = LegalEntitySource.FileName, FileNameSeparator = data.OptionalString("FilenameSeparator"), TokenPosition = data.Int("LegalEntityTokenPosition", 1) }
            : new MultiCompanyOptions { Source = LegalEntitySource.Subfolder };
    }

    private static string StatusExtension(JobData data)
    {
        var extension = data.OptionalString("StatusFileExtension") ?? ".Status";
        return extension.StartsWith('.') ? extension : "." + extension; // 4.x UI added the dot automatically
    }

    private static D365ActionPaths ActionPaths(JobData data)
    {
        var paths = new D365ActionPaths();
        void Set(string key, Action<string> setter)
        {
            if (data.OptionalString(key) is { } value)
            {
                setter(value);
            }
        }

        Set("GetAzureWriteUrlActionPath", v => paths.GetAzureWriteUrl = v);
        Set("GetExecutionSummaryStatusActionPath", v => paths.GetExecutionSummaryStatus = v);
        Set("GetExportedPackageUrlActionPath", v => paths.GetExportedPackageUrl = v);
        Set("GetExecutionSummaryPageUrlActionPath", v => paths.GetExecutionSummaryPageUrl = v);
        Set("ImportFromPackageActionPath", v => paths.ImportFromPackage = v);
        Set("DeleteExecutionHistoryJobActionPath", v => paths.DeleteExecutionHistoryJob = v);
        Set("ExportToPackageActionPath", v => paths.ExportToPackage = v);
        Set("ExportFromPackageActionPath", v => paths.ExportFromPackage = v);
        Set("GetMessageStatusActionPath", v => paths.GetMessageStatus = v);
        Set("GetImportTargetErrorKeysFileUrlPath", v => paths.GetImportTargetErrorKeysFileUrl = v);
        Set("GenerateImportTargetErrorKeysFilePath", v => paths.GenerateImportTargetErrorKeysFile = v);
        Set("GetExecutionErrorsPath", v => paths.GetExecutionErrors = v);
        return paths;
    }

    private static ScheduleOptions ConvertTrigger(XElement? trigger, string key, ConversionResult result)
    {
        switch (trigger?.Name.LocalName)
        {
            case "cron":
                return new ScheduleOptions { Cron = Value(trigger, "cron-expression"), TimeZone = Value(trigger, "time-zone") };
            case "simple":
                var interval = Value(trigger, "repeat-interval") ?? "0";
                return new ScheduleOptions
                {
                    Interval = long.TryParse(interval, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Parse(interval, CultureInfo.InvariantCulture),
                    StartAt = DateTimeOffset.TryParse(Value(trigger, "start-time"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ? start : null
                };
            case "calendar-interval":
                var count = int.Parse(Value(trigger, "repeat-interval") ?? "1", CultureInfo.InvariantCulture);
                var unit = Value(trigger, "repeat-interval-unit") ?? "Minute";
                return new ScheduleOptions
                {
                    Interval = unit switch
                    {
                        "Second" => TimeSpan.FromSeconds(count),
                        "Minute" => TimeSpan.FromMinutes(count),
                        "Hour" => TimeSpan.FromHours(count),
                        "Day" => TimeSpan.FromDays(count),
                        "Week" => TimeSpan.FromDays(7 * count),
                        _ => throw new InvalidDataException($"Unsupported interval unit '{unit}'.")
                    }
                };
            default:
                result.Warnings.Add($"{key}: no trigger found; job is converted as paused with a daily schedule.");
                return new ScheduleOptions { Cron = "0 0 0 * * ?", Paused = true };
        }
    }

    /// <summary>Links import/upload jobs with their monitors (4.x: naming convention "name-Execution monitor").</summary>
    private static void LinkMonitors(RisConfiguration configuration)
    {
        foreach (var monitor in configuration.Jobs.Where(j => j.Options is ExecutionMonitorJobOptions or ProcessingMonitorJobOptions))
        {
            foreach (var suffix in new[] { "-Execution monitor", "-Processing monitor" })
            {
                if (!monitor.Name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var parent = configuration.Jobs.FirstOrDefault(j => j.Group == monitor.Group && j.Name == monitor.Name[..^suffix.Length]);
                if (parent is not null)
                {
                    parent.PairedJob = monitor.Name;
                    monitor.PairedJob = parent.Name;
                }
            }
        }
    }

    private static JobData ReadJobDataMap(XElement job)
    {
        var data = new JobData();
        foreach (var entry in job.Element(Ns + "job-data-map")?.Elements(Ns + "entry") ?? [])
        {
            // 4.x client writes <entry><key/><value/></entry>; some files use <entry key="" value=""/>.
            var k = Value(entry, "key") ?? (string?)entry.Attribute("key");
            var v = Value(entry, "value") ?? (string?)entry.Attribute("value");
            if (!string.IsNullOrEmpty(k))
            {
                data[k] = v ?? string.Empty;
            }
        }
        return data;
    }

    private static string? Value(XElement parent, string name)
    {
        var value = parent.Element(Ns + name)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class JobData : Dictionary<string, string>
    {
        public JobData() : base(StringComparer.OrdinalIgnoreCase) { }

        public string String(string key) => OptionalString(key) ?? throw new InvalidDataException($"Required setting '{key}' is missing.");

        public string? OptionalString(string key) => TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        public bool Bool(string key) => OptionalString(key) is { } v && bool.TryParse(v, out var b) && b;

        public int Int(string key, int defaultValue) =>
            OptionalString(key) is { } v && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (int)d : defaultValue;

        public Guid Guid(string key) => System.Guid.Parse(String(key));
    }
}
