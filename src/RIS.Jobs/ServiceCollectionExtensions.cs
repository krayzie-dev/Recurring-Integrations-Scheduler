using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RIS.Core.Options;
using RIS.Core.Polling;
using RIS.Jobs.Handlers;
using RIS.Jobs.Quartz;
using RIS.Transfer;

namespace RIS.Jobs;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers job handlers, pipeline, file/SFTP services and Quartz job. D365 clients are registered by AddRisD365.
    /// </summary>
    public static IServiceCollection AddRisJobs(this IServiceCollection services)
    {
        services.TryAddSingleton<IPollingClock>(SystemPollingClock.Instance);
        services.TryAddSingleton<IFileSystem, FileSystem>();
        services.TryAddSingleton<IFileStore, FileStore>();
        services.TryAddSingleton<StatusFileStore>();
        services.TryAddSingleton<ISftpTransfer, SshNetSftpTransfer>();
        services.TryAddSingleton<ExecutionPoller>();
        services.TryAddSingleton<JobFiles>();
        services.TryAddSingleton<LegalEntityCache>();

        services.TryAddTransient<IJobHandler<ExportJobOptions>, ExportHandler>();
        services.TryAddTransient<IJobHandler<ImportJobOptions>, ImportHandler>();
        services.TryAddTransient<IJobHandler<UploadJobOptions>, UploadHandler>();
        services.TryAddTransient<IJobHandler<DownloadJobOptions>, DownloadHandler>();
        services.TryAddTransient<IJobHandler<ExecutionMonitorJobOptions>, ExecutionMonitorHandler>();
        services.TryAddTransient<IJobHandler<ProcessingMonitorJobOptions>, ProcessingMonitorHandler>();

        services.TryAddSingleton<JobPipeline>();
        services.TryAddSingleton<RunningJobs>();
        services.TryAddTransient<RisQuartzJob>();
        return services;
    }
}
