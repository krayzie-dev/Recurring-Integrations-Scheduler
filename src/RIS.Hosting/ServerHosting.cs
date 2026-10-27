using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using RIS.D365;
using RIS.Jobs;
using RIS.Persistence;
using Serilog;

namespace RIS.Hosting;

/// <summary>
/// Prepares databases before the scheduler starts: EF migrations of ris.db (with backup), backup of quartz.db before a
/// Quartz version change (Weasel then updates its schema), loading of configuration.
/// Registered before the Quartz hosted service, so it runs first.
/// </summary>
public sealed class RisStartup(IServiceProvider services, PersistenceOptions persistence, DbConfigurationStore store, ILogger<RisStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await DatabaseMaintenance.MigrateAsync(services, cancellationToken);

        var marker = Path.Combine(persistence.DataDirectory, "quartz.version");
        var quartzVersion = typeof(IScheduler).Assembly.GetName().Version?.ToString() ?? "unknown";
        var previous = File.Exists(marker) ? await File.ReadAllTextAsync(marker, cancellationToken) : null;
        if (previous != quartzVersion)
        {
            var backup = await DatabaseMaintenance.BackupAsync(persistence.QuartzDatabase, persistence.BackupDirectory, persistence.BackupsToKeep,
                "before-quartz-" + quartzVersion, cancellationToken);
            if (backup is not null)
            {
                logger.LogInformation("Quartz version changed from {Previous} to {Current}; database backed up to {Backup} before schema update",
                    previous ?? "unknown", quartzVersion, backup);
            }
            await File.WriteAllTextAsync(marker, quartzVersion, cancellationToken);
        }

        store.Refresh();
        logger.LogInformation("Configuration loaded: {Jobs} job(s), {Connections} connection(s), {SftpServers} SFTP server(s). Data: {DataDirectory}",
            store.Current.Jobs.Count, store.Current.Connections.Count, store.Current.SftpServers.Count, persistence.DataDirectory);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ServerHosting
{
    /// <summary>
    /// RIS as a service: configuration in ris.db, scheduler and execution history in quartz.db (schema kept up to date by Weasel),
    /// synchronization of configuration with the scheduler, interruption of running jobs on stop.
    /// </summary>
    public static IServiceCollection AddRisServer(this IServiceCollection services, IConfiguration configuration,
        Action<RisHostOptions>? configureHost = null, Action<PersistenceOptions>? configurePersistence = null)
    {
        var hostOptions = new RisHostOptions();
        configuration.GetSection(RisHostOptions.SectionName).Bind(hostOptions);
        configureHost?.Invoke(hostOptions);

        var persistence = new PersistenceOptions();
        configuration.GetSection("Ris:Data").Bind(persistence);
        configurePersistence?.Invoke(persistence);
        if (!Path.IsPathRooted(hostOptions.LogDirectory))
        {
            hostOptions.LogDirectory = Path.Combine(persistence.DataDirectory, hostOptions.LogDirectory);
        }

        services.AddSingleton(hostOptions);
        services.AddSerilog((_, logger) => RisHosting.ConfigureLogging(logger, hostOptions), writeToProviders: true);
        services.AddRisPersistence(persistence);
        services.AddRisD365(http =>
        {
            http.RetryCount = hostOptions.Http.RetryCount;
            http.RetryDelay = hostOptions.Http.RetryDelay;
            http.Timeout = hostOptions.Http.Timeout;
        });
        services.AddRisJobs();

        // Order matters: hosted services start in registration order and stop in reverse order.
        services.AddHostedService<RisStartup>();
        services.AddQuartz(q =>
        {
            q.UseExecutionLogCapture(capture => capture.MaxLines = 500);
            q.UsePersistentStore(store =>
            {
                store.UseSqlite(persistence.QuartzConnectionString);
                store.UseSystemTextJsonSerializer();
                store.UseExecutionHistory();
                store.UseWeaselForSqlite(weasel => weasel.AutoCreate = JasperFx.AutoCreate.CreateOrUpdate);
            });
        });
        services.AddQuartzHostedService(quartz => quartz.WaitForJobsToComplete = true);
        services.AddSingleton<ScheduleSynchronizer>();
        services.AddHostedService(sp => sp.GetRequiredService<ScheduleSynchronizer>());
        services.AddHostedService<RunningJobsInterrupter>();
        return services;
    }
}
