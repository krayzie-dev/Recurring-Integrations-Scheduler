using System.Globalization;
using Microsoft.Extensions.Logging;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.D365;
using RIS.Transfer;

namespace RIS.Jobs.Handlers;

/// <summary>
/// Downloads exported messages from Recurring Integrations queue. 4.x: Job.Download.
/// 5.0: dequeue → download → ack per message (4.x dequeued everything first and lost un-acked messages on the first error).
/// </summary>
public sealed class DownloadHandler(ID365ClientFactory clients, JobFiles jobFiles, Core.Polling.IPollingClock clock) : IJobHandler<DownloadJobOptions>
{
    /// <summary>Safety limit of messages in one run.</summary>
    public const int MaxMessagesPerRun = 1000;

    public async Task<JobOutcome> RunAsync(JobRunContext run, DownloadJobOptions options, CancellationToken cancellationToken)
    {
        var logger = run.Logger;
        var client = clients.Create(run.Connection, options.ActionPaths);
        var counters = new ItemCounters();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var number = 1; number <= MaxMessagesPerRun; number++)
        {
            var message = await client.DequeueAsync(options.ActivityId, cancellationToken);
            if (message is null)
            {
                break;
            }

            if (!seen.Add(message.CorrelationId))
            {
                logger.LogWarning("Message {CorrelationId} was returned again by dequeue; stopping this run to avoid a loop", message.CorrelationId);
                break;
            }

            if (number > 1 && options.DelayBetweenFiles > TimeSpan.Zero)
            {
                await clock.Delay(options.DelayBetweenFiles, cancellationToken);
            }

            using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = message.CorrelationId });
            run.ReportProgress(Math.Min(95, number), $"Downloading message {number}");
            await ItemProcessing.RunAsync(logger, counters, $"Message {message.CorrelationId}",
                () => ProcessMessageAsync(run, client, options, message, number, counters, cancellationToken));
        }

        return counters.ToOutcome("Downloaded messages").With("ActivityId", options.ActivityId);
    }

    private async Task ProcessMessageAsync(JobRunContext run, ID365Client client, DownloadJobOptions options, DequeuedMessage message,
        int number, ItemCounters counters, CancellationToken cancellationToken)
    {
        var logger = run.Logger;
        // Same file name as 4.x: timestamp and number in this run (downloaded package has no name).
        var fileName = $"{JobFiles.Timestamp(DateTime.Now)}-{number.ToString("D6", CultureInfo.InvariantCulture)}.zip";
        var path = jobFiles.Files.FileSystem.Path.Combine(options.DownloadSuccessDir, fileName);

        try
        {
            await using (var download = await client.OpenDownloadAsync(message.DownloadLocation, cancellationToken))
            {
                await jobFiles.Files.WriteAsync(download, path, cancellationToken);
            }
        }
        catch (D365ApiException ex)
        {
            jobFiles.Files.Delete(path);
            throw new RisException(ex.ErrorCode,
                $"Download of message {message.CorrelationId} failed; it was not acknowledged and will be offered again by D365: {ex.Message}", null, ex);
        }

        logger.LogInformation("Message {CorrelationId} downloaded to {File}", message.CorrelationId, path);

        try
        {
            await client.AckAsync(options.ActivityId, message, cancellationToken);
        }
        catch (D365ApiException ex)
        {
            var errorPath = jobFiles.Files.FileSystem.Path.Combine(options.DownloadErrorsDir, fileName);
            await jobFiles.Files.MoveAsync(path, errorPath, options.Retry, cancellationToken);
            throw new RisException(ErrorCodes.AckFailed,
                $"Acknowledgement of message {message.CorrelationId} failed; file moved to {errorPath}: {ex.Message}", null, ex);
        }

        await jobFiles.DeliverPackageAsync(run, path, options.SftpOutbound, options.UnzipPackage, options.DeletePackage,
            options.AddTimestamp, options.Retry, cancellationToken);
        counters.Succeeded++;
    }
}
