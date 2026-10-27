using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RIS.Core.Configuration;
using RIS.Core.Errors;
using RIS.Core.Options;

namespace RIS.Jobs;

/// <summary>
/// Runs a job: logging scope (JobKey, JobType, RunId), validation, handler dispatch, single error log with code and context,
/// summary line at the end. Never throws except on cancellation, so Quartz does not log the same error again.
/// </summary>
public sealed class JobPipeline(IServiceProvider services, IRisConfigurationStore configurationStore, ILoggerFactory loggerFactory)
{
    public async Task<JobOutcome> RunAsync(string jobKey, JobOptionsBase options, string runId, Action<int, string>? progress, CancellationToken cancellationToken)
    {
        var jobType = JobTypeOf(options);
        var logger = loggerFactory.CreateLogger("RIS.Jobs." + jobType);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["JobKey"] = jobKey, ["JobType"] = jobType.ToString(), ["RunId"] = runId });
        var stopwatch = Stopwatch.StartNew();

        JobOutcome outcome;
        try
        {
            var configuration = configurationStore.Current;
            var errors = ConfigurationValidator.ValidateObject(options, jobKey);
            if (errors.Count > 0)
            {
                throw new RisException(ErrorCodes.ConfigurationInvalid, "Job configuration is invalid: " + string.Join("; ", errors));
            }

            var connection = configuration.FindConnection(options.Connection)
                             ?? throw new RisException(ErrorCodes.ConnectionNotFound, $"Connection '{options.Connection}' is not defined.");

            logger.LogInformation("Job {JobKey} started (environment {Environment})", jobKey, connection.AosUri);
            var run = new JobRunContext
            {
                JobKey = jobKey,
                JobType = jobType,
                RunId = runId,
                Connection = connection,
                Configuration = configuration,
                Logger = logger,
                Progress = progress
            };

            outcome = options switch
            {
                ExportJobOptions o => await Handler<ExportJobOptions>().RunAsync(run, o, cancellationToken),
                ImportJobOptions o => await Handler<ImportJobOptions>().RunAsync(run, o, cancellationToken),
                UploadJobOptions o => await Handler<UploadJobOptions>().RunAsync(run, o, cancellationToken),
                DownloadJobOptions o => await Handler<DownloadJobOptions>().RunAsync(run, o, cancellationToken),
                ExecutionMonitorJobOptions o => await Handler<ExecutionMonitorJobOptions>().RunAsync(run, o, cancellationToken),
                ProcessingMonitorJobOptions o => await Handler<ProcessingMonitorJobOptions>().RunAsync(run, o, cancellationToken),
                _ => throw new RisException(ErrorCodes.ConfigurationInvalid, $"Unsupported job options {options.GetType().Name}.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Job {JobKey} was interrupted after {Elapsed} (service stop or manual interrupt)", jobKey, ExecutionPoller.Round(stopwatch.Elapsed));
            throw;
        }
        catch (RisException ex)
        {
            logger.LogError(ex, "[{ErrorCode}] Job {JobKey} failed: {Error}. Context: {@ErrorContext}", ex.ErrorCode, jobKey, ex.Message, ex.Context);
            outcome = JobOutcome.Failed(ex.ErrorCode, $"[{ex.ErrorCode}] {ex.Message}");
            foreach (var (key, value) in ex.Context)
            {
                outcome.With(key, value);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{ErrorCode}] Job {JobKey} failed with unexpected error: {Error}", ErrorCodes.Unexpected, jobKey, ex.Message);
            outcome = JobOutcome.Failed(ErrorCodes.Unexpected, $"[{ErrorCodes.Unexpected}] {ex.Message}");
        }

        outcome.With("DurationSeconds", (int)stopwatch.Elapsed.TotalSeconds);
        logger.Log(outcome.Status == JobOutcomeStatus.Failed ? LogLevel.Warning : LogLevel.Information,
            "Job {JobKey} finished: {Outcome} - {Summary} ({Elapsed}). Metrics: {@Metrics}",
            jobKey, outcome.Status, outcome.Summary, ExecutionPoller.Round(stopwatch.Elapsed), outcome.Metrics);
        return outcome;
    }

    public static JobType JobTypeOf(JobOptionsBase options) => options switch
    {
        ExportJobOptions => JobType.Export,
        ImportJobOptions => JobType.Import,
        UploadJobOptions => JobType.Upload,
        DownloadJobOptions => JobType.Download,
        ExecutionMonitorJobOptions => JobType.ExecutionMonitor,
        ProcessingMonitorJobOptions => JobType.ProcessingMonitor,
        _ => throw new ArgumentOutOfRangeException(nameof(options))
    };

    private IJobHandler<T> Handler<T>() where T : JobOptionsBase => services.GetRequiredService<IJobHandler<T>>();
}
