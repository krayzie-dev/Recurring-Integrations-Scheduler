using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using RIS.Core.Configuration;
using RIS.D365;
using RIS.Jobs;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace RIS.Hosting;

/// <summary>
/// Host settings (appsettings.json section "Ris").
/// </summary>
public sealed class RisHostOptions
{
    public const string SectionName = "Ris";

    /// <summary>Path of jobs.json (connections, SFTP servers, jobs).</summary>
    public string ConfigurationFile { get; set; } = "jobs.json";

    /// <summary>Folder for log files: ris-YYYYMMDD.clef and jobs/{JobKey}/job-YYYYMMDD.clef.</summary>
    public string LogDirectory { get; set; } = "logs";

    public int LogRetentionDays { get; set; } = 30;

    /// <summary>
    /// Server: jobs start to run this long after the service started (UI and API are available at once). For maintenance,
    /// e.g. checking the configuration after an upgrade before anything runs. Missed runs follow the MissedRuns policy.
    /// </summary>
    public TimeSpan SchedulerStartDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Per-job log files kept open at the same time; more jobs than this means files are closed and reopened.</summary>
    public int JobLogFilesOpen { get; set; } = 2000;

    public LogEventLevel MinimumLevel { get; set; } = LogEventLevel.Information;

    /// <summary>Write to the Windows Event Log (source must exist; the installer creates it in <see cref="EventLogName"/>).</summary>
    public bool EventLog { get; set; }

    public string EventLogSource { get; set; } = "Recurring Integrations Scheduler 5";

    /// <summary>Own log under "Applications and Services Logs" (not mixed into Application).</summary>
    public string EventLogName { get; set; } = "RIS5";

    /// <summary>Lowest level written to the Event Log; service start/stop are always written.</summary>
    public LogEventLevel EventLogMinimumLevel { get; set; } = LogEventLevel.Warning;

    public bool Console { get; set; } = true;

    public D365HttpOptions Http { get; set; } = new();

    /// <summary>Server: how often to check the database for configuration changed by another process (ris db import, set-secret).</summary>
    public TimeSpan ConfigurationPollInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Loads RIS configuration from a JSON file and validates it.
/// </summary>
public sealed class JsonFileConfigurationStore : IRisConfigurationStore
{
    public JsonFileConfigurationStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Current = RisJson.LoadConfiguration(Path);
    }

    public string Path { get; }

    public RisConfiguration Current { get; }

    public IReadOnlyList<string> Validate() => ConfigurationValidator.Validate(Current);
}

public static class RisHosting
{
    /// <summary>
    /// Serilog configuration: console, daily CLEF file, one file per job (JobKey) and Windows Event Log (Warning+).
    /// </summary>
    public static LoggerConfiguration ConfigureLogging(LoggerConfiguration logger, RisHostOptions options)
    {
        const string ConsoleTemplate = "{Timestamp:HH:mm:ss} {Level:u3} {JobKey} {Message:lj}{NewLine}{Exception}";
        logger.MinimumLevel.Is(options.MinimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .MinimumLevel.Override("Polly", LogEventLevel.Warning)
            .MinimumLevel.Override("Quartz", LogEventLevel.Information)
            // "Overriding address(es) ... Binding to endpoints defined via IConfiguration and/or UseKestrel()" on every start:
            // expected, the HTTP/HTTPS endpoints are configured in code (ServerSetup.ConfigureHttps).
            .MinimumLevel.Override("Microsoft.AspNetCore.Server.Kestrel", LogEventLevel.Error)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "RIS5")
            .WriteTo.File(new CompactJsonFormatter(), System.IO.Path.Combine(options.LogDirectory, "ris-.clef"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: options.LogRetentionDays, shared: true)
            .WriteTo.Map("JobKey", "_scheduler", (jobKey, sink) => sink.File(new CompactJsonFormatter(),
                System.IO.Path.Combine(options.LogDirectory, "jobs", SafeFileName(jobKey), "job-.clef"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: options.LogRetentionDays, shared: true), sinkMapCountLimit: options.JobLogFilesOpen);

        if (options.Console)
        {
            logger.WriteTo.Console(outputTemplate: ConsoleTemplate, restrictedToMinimumLevel: options.MinimumLevel);
        }

        if (options.EventLog && OperatingSystem.IsWindows())
        {
            var minimum = options.EventLogMinimumLevel;
            logger.WriteTo.Logger(eventLog => eventLog
                .Filter.ByIncludingOnly(e => e.Level >= minimum || e.Properties.ContainsKey(RisEventIds.EventLogProperty))
                .WriteTo.EventLog(options.EventLogSource, options.EventLogName, manageEventSource: false,
                    outputTemplate: "{Message:lj}{NewLine}{NewLine}Job: {JobKey}{NewLine}Run: {RunId}{NewLine}{Exception}",
                    eventIdProvider: new RisEventIds()));
        }

        return logger;
    }

    /// <summary>
    /// Registers RIS services, Serilog and an in-memory Quartz scheduler with execution history and log capture.
    /// Stage 3 replaces the in-memory store with SQLite.
    /// </summary>
    public static IServiceCollection AddRis(this IServiceCollection services, IConfiguration configuration, Action<RisHostOptions>? configure = null)
    {
        var options = new RisHostOptions();
        configuration.GetSection(RisHostOptions.SectionName).Bind(options);
        configure?.Invoke(options);

        services.AddSingleton(options);
        // writeToProviders: keeps Quartz log capture provider working (job log lines in execution history).
        services.AddSerilog((_, logger) => ConfigureLogging(logger, options), writeToProviders: true);

        services.AddSingleton<IRisConfigurationStore>(_ => new JsonFileConfigurationStore(options.ConfigurationFile));
        services.AddRisD365(http =>
        {
            http.RetryCount = options.Http.RetryCount;
            http.RetryDelay = options.Http.RetryDelay;
            http.Timeout = options.Http.Timeout;
        });
        services.AddRisJobs();
        services.AddQuartz(q => q.UseExecutionLogCapture(capture => capture.MaxLines = 500));
        services.AddQuartzExecutionHistory(history => history.RecordInput = true);
        return services;
    }

    public static string SafeFileName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
