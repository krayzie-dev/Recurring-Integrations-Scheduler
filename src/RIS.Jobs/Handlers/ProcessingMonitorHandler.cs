using System.Text.Json;
using Microsoft.Extensions.Logging;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.Core.Polling;
using RIS.Core.Statuses;
using RIS.D365;
using RIS.Transfer;

namespace RIS.Jobs.Handlers;

/// <summary>
/// Checks processing status of messages uploaded by <see cref="UploadHandler"/> (status files). 4.x: Job.ProcessingMonitor.
/// 5.0: subfolders are kept in processing folders (4.x flattened them).
/// </summary>
public sealed class ProcessingMonitorHandler(ID365ClientFactory clients, JobFiles jobFiles, IPollingClock clock) : IJobHandler<ProcessingMonitorJobOptions>
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public async Task<JobOutcome> RunAsync(JobRunContext run, ProcessingMonitorJobOptions options, CancellationToken cancellationToken)
    {
        var logger = run.Logger;
        var entries = jobFiles.StatusFiles.ReadAll(options.UploadSuccessDir, options.StatusFileExtension);
        if (entries.Count == 0)
        {
            return JobOutcome.Skipped("No uploads waiting for processing");
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
                ["MessageId"] = entry.Message.MessageId ?? string.Empty
            });
            run.ReportProgress(i * 100 / entries.Count, $"Checking {entry.Message.Name}");
            await ItemProcessing.RunAsync(logger, counters, $"Status check of {entry.Message.FullPath} (message {entry.Message.MessageId})",
                () => CheckAsync(run, client, options, entry, counters, cancellationToken));
        }

        return counters.ToOutcome("Checked uploads");
    }

    private async Task CheckAsync(JobRunContext run, ID365Client client, ProcessingMonitorJobOptions options, StatusFileEntry entry,
        ItemCounters counters, CancellationToken cancellationToken)
    {
        var logger = run.Logger;
        var message = entry.Message;
        var messageId = message.MessageId ?? throw new RisException(ErrorCodes.FileOperationFailed, $"Status file {entry.StatusFilePath} has no message id.");
        var fs = jobFiles.Files.FileSystem;

        DataJobStatusDetail detail;
        try
        {
            detail = await client.GetJobStatusAsync(options.ActivityId, messageId, cancellationToken);
        }
        catch (D365ApiException ex) when (ex.IsTransient)
        {
            counters.Pending++;
            logger.LogWarning("[{ErrorCode}] Status of message {MessageId} not available now ({Error}); will check again in the next run",
                ex.ErrorCode, messageId, ex.Message);
            return;
        }

        var state = detail.DataJobStatus?.DataJobState ?? DataJobState.Unrecognized;
        // Keep submission time of 4.x status files before the status file is rewritten.
        message.SubmittedUtc ??= jobFiles.StatusFiles.GetSubmittedUtc(entry);
        message.DataJobState = state;
        await jobFiles.StatusFiles.WriteAsync(message, options.StatusFileExtension, cancellationToken);

        if (state.IsSuccess())
        {
            var target = jobFiles.Rebase(message.FullPath!, options.UploadSuccessDir, options.ProcessingSuccessDir);
            await jobFiles.StatusFiles.MoveWithStatusAsync(message.FullPath!, target, options.StatusFileExtension, deleteStatusFile: true, options.Retry, cancellationToken);
            counters.Succeeded++;
            logger.LogInformation("Message {MessageId} ({File}) processed", messageId, message.Name);
            return;
        }

        if (state.IsInProgress())
        {
            if (message.SubmittedUtc is null || clock.UtcNow - message.SubmittedUtc.Value < options.Polling.Timeout)
            {
                counters.Pending++;
                return;
            }

            var timeoutTarget = await MoveToErrorsAsync(options, message.FullPath!, detail, cancellationToken);
            throw new RisException(ErrorCodes.MonitorTimeout,
                $"Processing did not finish within {options.Polling.Timeout} (submitted {message.SubmittedUtc:u}, state {state}). File moved to {timeoutTarget}.",
                new Dictionary<string, object?> { ["MessageId"] = messageId, ["State"] = state.ToString() });
        }

        var errorTarget = await MoveToErrorsAsync(options, message.FullPath!, detail, cancellationToken);
        if (options.GetExecutionErrors)
        {
            try
            {
                var errors = await client.GetExecutionErrorsAsync(messageId, cancellationToken);
                var path = fs.Path.Combine(fs.Path.GetDirectoryName(errorTarget)!,
                    $"{fs.Path.GetFileNameWithoutExtension(errorTarget)}-ExecutionErrors-{JobFiles.Timestamp(DateTime.Now)}.txt");
                await jobFiles.Files.WriteTextAsync(path, errors, cancellationToken);
            }
            catch (D365ApiException ex)
            {
                logger.LogWarning("Execution errors of message {MessageId} not downloaded: {Error}", messageId, ex.Message);
            }
        }

        throw new RisException(ErrorCodes.MonitorExecutionFailed,
            $"Processing of {message.Name} ended with state {state}. File moved to {errorTarget}.",
            new Dictionary<string, object?> { ["MessageId"] = messageId, ["State"] = state.ToString() });
    }

    private async Task<string> MoveToErrorsAsync(ProcessingMonitorJobOptions options, string file, DataJobStatusDetail detail, CancellationToken cancellationToken)
    {
        var target = jobFiles.Rebase(file, options.UploadSuccessDir, options.ProcessingErrorsDir);
        await jobFiles.StatusFiles.MoveWithStatusAsync(file, target, options.StatusFileExtension, deleteStatusFile: false, options.Retry, cancellationToken);
        // 4.x wrote job status detail (with execution log) under the status file name in the errors folder.
        await jobFiles.Files.WriteTextAsync(jobFiles.StatusFiles.GetStatusFilePath(target, options.StatusFileExtension),
            JsonSerializer.Serialize(detail, ReportJson), cancellationToken);
        return target;
    }
}
