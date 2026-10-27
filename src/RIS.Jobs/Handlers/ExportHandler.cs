using Microsoft.Extensions.Logging;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.Core.Statuses;
using RIS.D365;

namespace RIS.Jobs.Handlers;

/// <summary>
/// Exports a data project to a package and downloads it (Package API). 4.x: Job.Export.
/// </summary>
public sealed class ExportHandler(ID365ClientFactory clients, ExecutionPoller poller, JobFiles jobFiles) : IJobHandler<ExportJobOptions>
{
    public async Task<JobOutcome> RunAsync(JobRunContext run, ExportJobOptions options, CancellationToken cancellationToken)
    {
        var logger = run.Logger;
        var client = clients.Create(run.Connection, options.ActionPaths);
        var executionId = JobFiles.NewExecutionId(options.DataProject, DateTime.Now);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["ExecutionId"] = executionId, ["DataProject"] = options.DataProject });
        var context = new Dictionary<string, object?> { ["ExecutionId"] = executionId, ["DataProject"] = options.DataProject, ["Company"] = options.Company };

        logger.LogInformation("Starting export of {DataProject} for company {Company}", options.DataProject, options.Company);
        await client.ExportToPackageAsync(options.DataProject, executionId, executionId, options.Company, reExecute: false, cancellationToken);

        try
        {
            return await WaitAndDownloadAsync(run, client, options, executionId, context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Export {ExecutionId} was interrupted while RIS waited for it; the export may still run in D365", executionId);
            throw;
        }
    }

    private async Task<JobOutcome> WaitAndDownloadAsync(JobRunContext run, ID365Client client, ExportJobOptions options, string executionId,
        Dictionary<string, object?> context, CancellationToken cancellationToken)
    {
        var logger = run.Logger;

        // One deadline for the whole export: waiting for status and for package URL.
        var started = poller.Clock.UtcNow;
        var deadline = started + options.Polling.Timeout;

        var status = await poller.PollAsync("export status",
            async ct => await client.GetExecutionSummaryStatusAsync(executionId, ct),
            value => !DmfExecutionStatusExtensions.Parse(value).IsInProgress(),
            options.Polling, started, deadline, ErrorCodes.ExportTimeout, context, logger, cancellationToken,
            (check, value, elapsed) => run.ReportProgress(10, $"Waiting for D365: {value} ({elapsed:hh\\:mm\\:ss}, check {check})"));

        var executionStatus = DmfExecutionStatusExtensions.Parse(status.Value);
        logger.LogInformation("Export finished with status {Status} after {Elapsed} ({Checks} status checks)",
            executionStatus, ExecutionPoller.Round(status.Elapsed), status.Checks);

        if (!executionStatus.IsSuccess(partialIsSuccess: true))
        {
            var code = executionStatus == DmfExecutionStatus.Unrecognized ? ErrorCodes.StatusUnrecognized : ErrorCodes.ExportFailed;
            throw new RisException(code, $"Export execution ended with status '{status.Value}'. Check the execution in D365 Data management (execution id {executionId}).", context);
        }

        if (executionStatus == DmfExecutionStatus.PartiallySucceeded)
        {
            logger.LogWarning("Export {ExecutionId} only partially succeeded; the package is downloaded anyway (same as 4.x)", executionId);
        }

        run.ReportProgress(60, "Waiting for package URL");
        var url = await poller.PollAsync("exported package URL",
            async ct => await client.GetExportedPackageUrlAsync(executionId, ct),
            value => !string.IsNullOrEmpty(value),
            new PollingOptions { InitialDelay = options.DelayBetweenFiles, MaxDelay = options.Polling.MaxDelay, Timeout = options.Polling.Timeout },
            started, deadline, ErrorCodes.ExportPackageUrlTimeout, context, logger, cancellationToken);

        run.ReportProgress(70, "Downloading package");
        var fileName = JobFiles.Timestamp(DateTime.Now) + ".zip";
        var packagePath = jobFiles.Files.FileSystem.Path.Combine(options.DownloadSuccessDir, fileName);
        try
        {
            await using var download = await client.OpenDownloadAsync(new Uri(url.Value!), cancellationToken);
            await jobFiles.Files.WriteAsync(download, packagePath, cancellationToken);
        }
        catch (D365ApiException ex)
        {
            throw new RisException(ErrorCodes.ExportDownloadFailed, $"Download of exported package failed: {ex.Message}", context, ex);
        }

        logger.LogInformation("Exported package saved as {File}", packagePath);
        run.ReportProgress(90, "Delivering package");
        var extracted = await jobFiles.DeliverPackageAsync(run, packagePath, options.SftpOutbound,
            options.UnzipPackage, options.DeletePackage, options.AddTimestamp, options.Retry, cancellationToken);

        return JobOutcome.Succeeded($"Exported {options.DataProject} ({executionStatus}) to {fileName} in {ExecutionPoller.Round(status.Elapsed)}")
            .With("ExecutionId", executionId)
            .With("Status", executionStatus.ToString())
            .With("File", packagePath)
            .With("ExtractedFiles", extracted.Count)
            .With("WaitSeconds", (int)status.Elapsed.TotalSeconds)
            .With("StatusChecks", status.Checks);
    }
}
