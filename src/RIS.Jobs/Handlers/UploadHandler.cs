using Microsoft.Extensions.Logging;
using RIS.Core.Contracts;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.Core.Polling;
using RIS.D365;
using RIS.Transfer;

namespace RIS.Jobs.Handlers;

/// <summary>
/// Uploads files to Recurring Integrations enqueue endpoint. 4.x: Job.Upload.
/// Like 4.x, files are moved to success/errors folder without subfolders.
/// </summary>
public sealed class UploadHandler(ID365ClientFactory clients, JobFiles jobFiles, IPollingClock clock) : IJobHandler<UploadJobOptions>
{
    public async Task<JobOutcome> RunAsync(JobRunContext run, UploadJobOptions options, CancellationToken cancellationToken)
    {
        var logger = run.Logger;
        await jobFiles.DownloadInboundAsync(run, options.SftpInbound, options.InputDir, cancellationToken);

        var files = jobFiles.Files.GetFiles(options.InputDir, options.Files);
        if (files.Count == 0)
        {
            return JobOutcome.Skipped("No input files").With("Files", 0);
        }

        logger.LogInformation("Found {Count} file(s) in input folder {Folder}", files.Count, options.InputDir);
        var client = clients.Create(run.Connection, options.ActionPaths);
        var counters = new ItemCounters();

        for (var i = 0; i < files.Count; i++)
        {
            if (i > 0 && options.DelayBetweenFiles > TimeSpan.Zero)
            {
                await clock.Delay(options.DelayBetweenFiles, cancellationToken);
            }

            var file = files[i];
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["File"] = file });
            run.ReportProgress(i * 100 / files.Count, $"Uploading {jobFiles.Files.FileSystem.Path.GetFileName(file)}");
            await ItemProcessing.RunAsync(logger, counters, $"Upload of file {file}",
                () => UploadFileAsync(run, client, options, file, counters, cancellationToken));
        }

        return counters.ToOutcome("Uploaded files").With("ActivityId", options.ActivityId);
    }

    private async Task UploadFileAsync(JobRunContext run, ID365Client client, UploadJobOptions options, string file, ItemCounters counters, CancellationToken cancellationToken)
    {
        var fs = jobFiles.Files.FileSystem;
        var fileName = fs.Path.GetFileName(file);
        string messageId;

        await using (var content = await jobFiles.Files.OpenReadAsync(file, options.Retry, cancellationToken))
        {
            try
            {
                messageId = await client.EnqueueAsync(options.ActivityId, content, options.Company,
                    options.IsDataPackage ? null : options.EntityName, fileName, cancellationToken);
            }
            catch (D365ApiException ex)
            {
                content.Dispose();
                var errorPath = fs.Path.Combine(options.UploadErrorsDir, fileName);
                await jobFiles.Files.MoveAsync(file, errorPath, options.Retry, cancellationToken);
                await jobFiles.StatusFiles.WriteErrorReportAsync(errorPath, options.StatusFileExtension,
                    new ErrorReport(ErrorCodes.UploadFailed, ex.Message, null, (int?)ex.StatusCode, ex.ResponseBody, new { options.ActivityId, options.EntityName }),
                    cancellationToken);
                throw new RisException(ErrorCodes.UploadFailed, $"Enqueue failed for '{fileName}': {ex.Message}. File moved to errors.", null, ex);
            }
        }

        var target = fs.Path.Combine(options.UploadSuccessDir, fileName);
        await jobFiles.Files.MoveAsync(file, target, options.Retry, cancellationToken);
        if (options.WriteStatusFiles)
        {
            await jobFiles.StatusFiles.WriteAsync(new DataMessage
            {
                Name = fileName,
                FullPath = target,
                MessageId = messageId,
                MessageStatus = MessageStatus.Enqueued,
                SubmittedUtc = clock.UtcNow
            }, options.StatusFileExtension, cancellationToken);
        }

        counters.Succeeded++;
        run.Logger.LogInformation("File {File} enqueued, message id {MessageId}", fileName, messageId);
    }
}
