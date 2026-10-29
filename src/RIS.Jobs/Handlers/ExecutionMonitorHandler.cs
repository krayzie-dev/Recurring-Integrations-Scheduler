using Microsoft.Extensions.Logging;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.Core.Polling;
using RIS.Core.Statuses;
using RIS.D365;
using RIS.Transfer;

namespace RIS.Jobs.Handlers;

/// <summary>
/// Checks status of Package API imports submitted by <see cref="ImportHandler"/> (status files). 4.x: Job.ExecutionMonitor.
/// 5.0: temporary D365 errors do not stop the run; imports waiting longer than the timeout are moved to errors.
/// </summary>
public sealed class ExecutionMonitorHandler(ID365ClientFactory clients, JobFiles jobFiles, ExecutionPoller poller, IPollingClock clock)
    : IJobHandler<ExecutionMonitorJobOptions>
{
    public async Task<JobOutcome> RunAsync(JobRunContext run, ExecutionMonitorJobOptions options, CancellationToken cancellationToken)
    {
        var logger = run.Logger;
        var entries = jobFiles.StatusFiles.ReadAll(options.UploadSuccessDir, options.StatusFileExtension);
        if (entries.Count == 0)
        {
            return JobOutcome.Skipped("No imports waiting for status");
        }

        var client = clients.Create(run.Connection, options.ActionPaths);
        var counters = new ItemCounters();
        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0 && options.DelayBetweenFiles > TimeSpan.Zero)
            {
                await clock.Delay(options.DelayBetweenFiles, cancellationToken);
            }

            var entry = entries[i];
            using var scope = logger.BeginScope(new Dictionary<string, object>
            {
                ["File"] = entry.Message.FullPath!,
                ["ExecutionId"] = entry.Message.MessageId ?? string.Empty
            });
            run.ReportProgress(i * 100 / entries.Count, $"Checking {entry.Message.Name}");
            await ItemProcessing.RunAsync(logger, counters, $"Status check of {entry.Message.FullPath} (execution {entry.Message.MessageId})",
                () => CheckAsync(run, client, options, entry, counters, cancellationToken));
        }

        return counters.ToOutcome("Checked imports");
    }

    private async Task CheckAsync(JobRunContext run, ID365Client client, ExecutionMonitorJobOptions options, StatusFileEntry entry,
        ItemCounters counters, CancellationToken cancellationToken)
    {
        var logger = run.Logger;
        var message = entry.Message;
        var executionId = message.MessageId ?? throw new RisException(ErrorCodes.FileOperationFailed, $"Status file {entry.StatusFilePath} has no execution id.");

        string rawStatus;
        try
        {
            rawStatus = await client.GetExecutionSummaryStatusAsync(executionId, cancellationToken);
        }
        catch (D365ApiException ex) when (ex.IsTransient)
        {
            counters.Pending++;
            logger.LogWarning("[{ErrorCode}] Status of execution {ExecutionId} not available now ({Error}); will check again in the next run",
                ex.ErrorCode, executionId, ex.Message);
            return;
        }
        catch (D365ApiException ex) when (IsExecutionNotFound(ex))
        {
            // D365 no longer knows the execution (history cleaned up, environment refreshed, status file from another
            // environment): asking again will never help, so the file goes to errors instead of being checked forever.
            var target = jobFiles.Rebase(message.FullPath!, options.UploadSuccessDir, options.ProcessingErrorsDir);
            await jobFiles.StatusFiles.MoveWithStatusAsync(message.FullPath!, target, options.StatusFileExtension, deleteStatusFile: true, options.Retry, cancellationToken);
            throw new RisException(ErrorCodes.MonitorExecutionNotFound,
                $"Execution {executionId} of {message.Name} does not exist in D365 (execution history cleaned up, environment refreshed or file submitted to another environment). File moved to {target}.",
                new Dictionary<string, object?> { ["ExecutionId"] = executionId }, ex);
        }

        var status = DmfExecutionStatusExtensions.Parse(rawStatus);
        logger.LogDebug("Execution {ExecutionId} status {Status}", executionId, rawStatus);

        if (status == DmfExecutionStatus.Succeeded)
        {
            var target = jobFiles.Rebase(message.FullPath!, options.UploadSuccessDir, options.ProcessingSuccessDir);
            await jobFiles.StatusFiles.MoveWithStatusAsync(message.FullPath!, target, options.StatusFileExtension, deleteStatusFile: true, options.Retry, cancellationToken);
            await WriteShortcutAsync(client, executionId, target, logger, cancellationToken);
            counters.Succeeded++;
            logger.LogInformation("Import of {File} succeeded (execution {ExecutionId})", message.Name, executionId);
            return;
        }

        if (status.IsInProgress() || status == DmfExecutionStatus.Unrecognized)
        {
            var submitted = jobFiles.StatusFiles.GetSubmittedUtc(entry);
            if (submitted is null || clock.UtcNow - submitted.Value < options.Polling.Timeout)
            {
                counters.Pending++;
                if (status == DmfExecutionStatus.Unrecognized)
                {
                    logger.LogWarning("[{ErrorCode}] Execution {ExecutionId} returned unrecognized status '{Status}'; will check again",
                        ErrorCodes.StatusUnrecognized, executionId, rawStatus);
                }
                return;
            }

            var timeoutTarget = await MoveToErrorsAsync(client, options, message.FullPath!, executionId, logger, cancellationToken);
            throw new RisException(ErrorCodes.MonitorTimeout,
                $"Import did not finish within {options.Polling.Timeout} (submitted {submitted:u}, status {rawStatus}). File moved to {timeoutTarget}.",
                new Dictionary<string, object?> { ["ExecutionId"] = executionId, ["Status"] = rawStatus });
        }

        // Failed, Canceled, Unknown, PartiallySucceeded (as in 4.x: partial import is an error)
        var errorTarget = await MoveToErrorsAsync(client, options, message.FullPath!, executionId, logger, cancellationToken);
        if (options.GetImportTargetErrorKeysFile)
        {
            await DownloadErrorKeysAsync(client, options, executionId, errorTarget, logger, cancellationToken);
        }

        if (options.GetExecutionErrors)
        {
            await DownloadExecutionErrorsAsync(client, executionId, errorTarget, logger, cancellationToken);
        }

        throw new RisException(ErrorCodes.MonitorExecutionFailed,
            $"Import of {message.Name} ended with status {rawStatus} in D365. File moved to {errorTarget}.",
            new Dictionary<string, object?> { ["ExecutionId"] = executionId, ["Status"] = rawStatus });
    }

    /// <summary>HTTP 400 "Execution details were not found for execution id: ..." of GetExecutionSummaryStatus.</summary>
    internal static bool IsExecutionNotFound(D365ApiException ex) =>
        ex.StatusCode == System.Net.HttpStatusCode.BadRequest
        && (ex.D365Message ?? ex.ResponseBody ?? string.Empty).Contains("were not found", StringComparison.OrdinalIgnoreCase);

    private async Task<string> MoveToErrorsAsync(ID365Client client, ExecutionMonitorJobOptions options, string file, string executionId,
        ILogger logger, CancellationToken cancellationToken)
    {
        var target = jobFiles.Rebase(file, options.UploadSuccessDir, options.ProcessingErrorsDir);
        await jobFiles.StatusFiles.MoveWithStatusAsync(file, target, options.StatusFileExtension, deleteStatusFile: true, options.Retry, cancellationToken);
        await WriteShortcutAsync(client, executionId, target, logger, cancellationToken);
        return target;
    }

    private async Task WriteShortcutAsync(ID365Client client, string executionId, string target, ILogger logger, CancellationToken cancellationToken)
    {
        string? url = null;
        try
        {
            url = await client.GetExecutionSummaryPageUrlAsync(executionId, cancellationToken);
        }
        catch (D365ApiException ex)
        {
            logger.LogWarning("Link to execution summary of {ExecutionId} not available: {Error}", executionId, ex.Message);
        }

        if (!string.IsNullOrEmpty(url))
        {
            await jobFiles.WriteShortcutAsync(target, url, cancellationToken);
        }
    }

    private async Task DownloadErrorKeysAsync(ID365Client client, ExecutionMonitorJobOptions options, string executionId, string errorTarget,
        ILogger logger, CancellationToken cancellationToken)
    {
        var fs = jobFiles.Files.FileSystem;
        IReadOnlyList<string> entities;
        try
        {
            var manifestSource = string.IsNullOrEmpty(options.PackageTemplate) ? errorTarget : options.PackageTemplate;
            await using var package = fs.File.OpenRead(manifestSource);
            entities = PackageManifest.GetEntityNames(package, manifestSource);
        }
        catch (Exception ex) when (ex is RisException or IOException or InvalidDataException)
        {
            logger.LogWarning("Entities of execution {ExecutionId} cannot be read from package manifest: {Error}", executionId, ex.Message);
            return;
        }

        foreach (var entity in entities)
        {
            try
            {
                if (!await client.GenerateImportTargetErrorKeysFileAsync(executionId, entity, cancellationToken))
                {
                    continue;
                }

                var started = clock.UtcNow;
                var url = await poller.PollAsync($"error keys file of {entity}",
                    async ct => await client.GetImportTargetErrorKeysFileUrlAsync(executionId, entity, ct),
                    value => !string.IsNullOrEmpty(value),
                    new PollingOptions { InitialDelay = options.Polling.InitialDelay, MaxDelay = options.Polling.MaxDelay },
                    started, started + options.ErrorKeysFileTimeout, ErrorCodes.MonitorTimeout,
                    new Dictionary<string, object?> { ["ExecutionId"] = executionId, ["Entity"] = entity }, logger, cancellationToken);

                var path = fs.Path.Combine(fs.Path.GetDirectoryName(errorTarget)!,
                    $"{fs.Path.GetFileNameWithoutExtension(errorTarget)}-{entity}-ErrorKeys-{JobFiles.Timestamp(DateTime.Now)}.txt");
                await using var download = await client.OpenDownloadAsync(new Uri(url.Value!), cancellationToken);
                await jobFiles.Files.WriteAsync(download, path, cancellationToken);
                logger.LogInformation("Error keys of entity {Entity} saved to {File}", entity, path);
            }
            catch (RisException ex)
            {
                logger.LogWarning("[{ErrorCode}] Error keys file of entity {Entity} not downloaded: {Error}", ex.ErrorCode, entity, ex.Message);
            }
        }
    }

    private async Task DownloadExecutionErrorsAsync(ID365Client client, string executionId, string errorTarget, ILogger logger, CancellationToken cancellationToken)
    {
        var fs = jobFiles.Files.FileSystem;
        try
        {
            var errors = await client.GetExecutionErrorsAsync(executionId, cancellationToken);
            var path = fs.Path.Combine(fs.Path.GetDirectoryName(errorTarget)!,
                $"{fs.Path.GetFileNameWithoutExtension(errorTarget)}-ExecutionErrors-{JobFiles.Timestamp(DateTime.Now)}.txt");
            await jobFiles.Files.WriteTextAsync(path, errors, cancellationToken);
            logger.LogInformation("Execution errors saved to {File}", path);
        }
        catch (D365ApiException ex)
        {
            logger.LogWarning("Execution errors of {ExecutionId} not downloaded: {Error}", executionId, ex.Message);
        }
    }
}
