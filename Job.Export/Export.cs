/* Copyright (c) Microsoft Corporation. All rights reserved.
   Licensed under the MIT License. */

using log4net;
using Polly;
using Quartz;
using RecurringIntegrationsScheduler.Common.Contracts;
using RecurringIntegrationsScheduler.Common.Helpers;
using RecurringIntegrationsScheduler.Common.JobSettings;
using RecurringIntegrationsScheduler.Job.Properties;
using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace RecurringIntegrationsScheduler.Job
{
    /// <summary>
    /// Job that is used to request export of data using new method introduced in platform update 5
    /// </summary>
    /// <seealso cref="IJob" />
    [DisallowConcurrentExecution]
    public class Export : IJob
    {
        /// <summary>
        /// The log
        /// </summary>
        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The settings
        /// </summary>
        private readonly ExportJobSettings _settings = new ExportJobSettings();

        /// <summary>
        /// The HTTP client helper
        /// </summary>
        private IHttpClientHelper _httpClientHelper;
        private readonly IHttpClientHelperFactory _httpClientHelperFactory;
        private readonly ISftpTransferService _sftpTransferService;

        /// <summary>
        /// Job execution context
        /// </summary>
        private IJobExecutionContext _context;

        /// <summary>
        /// Retry policy for IO operations
        /// </summary>
        private Policy _retryPolicyForIo;

        /// <summary>
        /// Time source used for waiting between status checks
        /// </summary>
        private readonly IPollingClock _clock;

        /// <summary>
        /// Execution id of the export currently processed by this job instance
        /// </summary>
        private string _executionId;

        /// <summary>
        /// Checks whether D365 execution status means that export is still running
        /// </summary>
        /// <param name="executionStatus">Execution status returned by GetExecutionSummaryStatus</param>
        private static bool IsExecutionInProgress(string executionStatus)
        {
            return executionStatus == "NotRun" || executionStatus == "Executing" || executionStatus == "Bad request";
        }

        /// <summary>
        /// Called by the <see cref="T:Quartz.IScheduler" /> when a <see cref="T:Quartz.ITrigger" />
        /// fires that is associated with the <see cref="T:Quartz.IJob" />.
        /// </summary>
        /// <param name="context">The execution context.</param>
        /// <exception cref="JobExecutionException">false</exception>
        /// <remarks>
        /// The implementation may wish to set a  result object on the
        /// JobExecutionContext before this method exits.  The result itself
        /// is meaningless to Quartz, but may be informative to
        /// <see cref="T:Quartz.IJobListener" />s or
        /// <see cref="T:Quartz.ITriggerListener" />s that are watching the job's
        /// execution.
        /// </remarks>
        public Export() : this(HttpClientHelperFactory.Default, SftpTransferService.Instance, SystemPollingClock.Instance)
        {
        }

        internal Export(IHttpClientHelperFactory httpClientHelperFactory, ISftpTransferService sftpTransferService)
            : this(httpClientHelperFactory, sftpTransferService, SystemPollingClock.Instance)
        {
        }

        internal Export(IHttpClientHelperFactory httpClientHelperFactory, ISftpTransferService sftpTransferService, IPollingClock clock)
        {
            _httpClientHelperFactory = httpClientHelperFactory ?? throw new ArgumentNullException(nameof(httpClientHelperFactory));
            _sftpTransferService = sftpTransferService ?? throw new ArgumentNullException(nameof(sftpTransferService));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public async Task Execute(IJobExecutionContext context)
        {
            try
            {
                _context = context;
                _settings.Initialize(context);

                if (_settings.IndefinitePause)
                {
                    await context.Scheduler.PauseJob(context.JobDetail.Key);
                    Log.InfoFormat(CultureInfo.InvariantCulture, string.Format(Resources.Job_0_was_paused_indefinitely, _context.JobDetail.Key));
                    return;
                }

                _retryPolicyForIo = Policy.Handle<IOException>().WaitAndRetry(
                    retryCount: _settings.RetryCount,
                    sleepDurationProvider: attempt => TimeSpan.FromSeconds(_settings.RetryDelay),
                    onRetry: (exception, calculatedWaitDuration) =>
                    {
                        Log.WarnFormat(CultureInfo.InvariantCulture, string.Format(Resources.Job_0_Retrying_IO_operation_Exception_1, _context.JobDetail.Key, exception.Message));
                    });

                Log.VerboseFormat(_settings.LogVerbose, CultureInfo.InvariantCulture, Resources.Job_0_starting, _context.JobDetail.Key);
                await Process();

                Log.VerboseFormat(_settings.LogVerbose, CultureInfo.InvariantCulture, Resources.Job_0_ended, _context.JobDetail.Key);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                // Job was interrupted (e.g. service stop). Export may still run in D365 - not a job error.
                Log.Warn(string.Format(Resources.Job_0_was_interrupted_Execution_Id_1_may_still_run_in_D365, context.JobDetail.Key, _executionId ?? "n/a"));
            }
            catch (Exception ex)
            {
                if (_settings.PauseJobOnException)
                {
                    await context.Scheduler.PauseJob(context.JobDetail.Key);
                    Log.WarnFormat(CultureInfo.InvariantCulture, string.Format(Resources.Job_0_was_paused_because_of_error, _context.JobDetail.Key));
                }
                if (!string.Equals(context.Scheduler.SchedulerName, RecurringIntegrationsScheduler.Common.Contracts.SchedulerConstants.PrivateSchedulerName, StringComparison.Ordinal))
                {
                    // Quartz JobRunShell logs the exception (with inner exception and stack trace) - log once only.
                    throw new JobExecutionException(string.Format(Resources.Job_0_failed, _context.JobDetail.Key), ex, false);
                }
                Log.Error(string.Format(Resources.Job_0_thrown_an_error_1, _context.JobDetail.Key, ex.Message), ex);
            }
        }

        /// <summary>
        /// Processes this instance.
        /// </summary>
        /// <returns></returns>
        private async Task Process()
        {
            using (_httpClientHelper = _httpClientHelperFactory.Create(_settings))
            {
                var executionId = $"{_settings.DataProject}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{Guid.NewGuid()}";
                _executionId = executionId;

                var responseExportToPackage = await _httpClientHelper.ExportToPackage(_settings.DataProject, executionId, executionId, _settings.Company);

                if (!responseExportToPackage.IsSuccessStatusCode)
                {
                    throw new JobExecutionException($@"Job: {_settings.JobKey}. ExportToPackage request failed. Execution Id: {executionId}. Status: {responseExportToPackage.StatusCode}. Message: {await HttpClientHelper.ReadContentForLogAsync(responseExportToPackage)}");
                }

                // One deadline for the whole export: waiting for status and for package URL.
                var exportStartedUtc = _clock.UtcNow;
                var deadlineUtc = exportStartedUtc.AddMinutes(_settings.StatusCheckTimeoutMinutes);

                var statusResult = await PollAsync(
                    () => _httpClientHelper.GetExecutionSummaryStatus(executionId),
                    status => !IsExecutionInProgress(status),
                    new StatusCheckBackoff(_settings.DelayBetweenStatusCheck, _settings.MaxDelayBetweenStatusCheck),
                    exportStartedUtc,
                    deadlineUtc,
                    (check, status, nextDelay) => string.Format(Resources.Job_0_Checking_if_export_is_completed_Try_1_Status_2_Next_check_in_3, _context.JobDetail.Key, check, status, nextDelay),
                    (checks, status, elapsed, lastError) => string.Format(Resources.Job_0_Export_did_not_finish_within_1_minutes_Status_2_Execution_Id_3_Elapsed_4_Checks_5_Last_error_6, _context.JobDetail.Key, _settings.StatusCheckTimeoutMinutes, status ?? "n/a", executionId, elapsed, checks, lastError ?? "none"));
                var executionStatus = statusResult.Value;

                Log.Info(string.Format(Resources.Job_0_Export_finished_with_status_1_Execution_Id_2_Elapsed_3_Checks_4, _context.JobDetail.Key, executionStatus, executionId, statusResult.Elapsed, statusResult.Checks));

                if (executionStatus == "Succeeded" || executionStatus == "PartiallySucceeded")
                {
                    var urlResult = await PollAsync(
                        () => _httpClientHelper.GetExportedPackageUrl(executionId),
                        url => !string.IsNullOrEmpty(url),
                        new StatusCheckBackoff(_settings.DelayBetweenFiles, _settings.MaxDelayBetweenStatusCheck),
                        exportStartedUtc,
                        deadlineUtc,
                        (check, url, nextDelay) => string.Format(Resources.Job_0_Trying_to_get_exported_package_URL_Try_1, _context.JobDetail.Key, check),
                        (checks, url, elapsed, lastError) => string.Format(Resources.Job_0_Exported_package_URL_not_available_within_1_minutes_Execution_Id_2_Elapsed_3_Checks_4_Last_error_5, _context.JobDetail.Key, _settings.StatusCheckTimeoutMinutes, executionId, elapsed, checks, lastError ?? "none"));
                    var packageUrl = urlResult.Value;

                    var packageUri = new Uri(packageUrl);
                    var response = await _httpClientHelper.GetRequestAsync(packageUri, false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new JobExecutionException(string.Format(Resources.Job_0_Download_failure_1, _context.JobDetail.Key, $"Status: {response.StatusCode}. Execution Id: {executionId}. Message: {await HttpClientHelper.ReadContentForLogAsync(response)}"));
                    }
                    using Stream downloadedStream = await response.Content.ReadAsStreamAsync();

                    var fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-ffff}.zip";
                    var dataMessage = new DataMessage()
                    {
                        FullPath = Path.Combine(_settings.DownloadSuccessDir, fileName),
                        Name = fileName,
                        MessageStatus = MessageStatus.Succeeded
                    };
                    _retryPolicyForIo.Execute(() => FileOperationsHelper.Create(downloadedStream, dataMessage.FullPath));

                    var uploadedToSftp = UploadFileToSftp(dataMessage.FullPath);

                    if (_settings.UnzipPackage)
                    {
                        _retryPolicyForIo.Execute(() => FileOperationsHelper.UnzipPackage(dataMessage.FullPath, _settings.DeletePackage, _settings.AddTimestamp));
                    }

                    if (uploadedToSftp)
                    {
                        MoveToUploadedFolderIfExists(dataMessage.FullPath);
                    }
                }
                else if (executionStatus == "Unknown" || executionStatus == "Failed" || executionStatus == "Canceled")
                {
                    throw new JobExecutionException(string.Format(Resources.Export_execution_failed_for_job_0_Status_1, _context.JobDetail.Key, executionStatus, executionId));
                }
                else
                {
                    Log.Error(string.Format(Resources.Job_0_Execution_status_1_Execution_Id_2, _context.JobDetail.Key, executionStatus, executionId));
                }
            }
        }

        /// <summary>
        /// Result of polling D365 for a value
        /// </summary>
        private sealed class PollResult
        {
            public string Value { get; set; }
            public int Checks { get; set; }
            public TimeSpan Elapsed { get; set; }
        }

        /// <summary>
        /// Repeats D365 request with growing delays until returned value is final or deadline passes.
        /// Transient failures (timeouts, throttling, 5xx, network errors) are logged and retried until the deadline.
        /// </summary>
        /// <param name="sendRequest">Request returning D365 response with "value" property</param>
        /// <param name="isFinal">Checks whether returned value ends polling</param>
        /// <param name="backoff">Delay calculator</param>
        /// <param name="startedUtc">Start of the whole export (for elapsed time)</param>
        /// <param name="deadlineUtc">Time after which polling fails</param>
        /// <param name="verboseMessage">Verbose message for each check (check number, value, next delay)</param>
        /// <param name="timeoutMessage">Error message when deadline passes (checks, last value, elapsed, last error)</param>
        /// <returns>Final value with polling statistics</returns>
        private async Task<PollResult> PollAsync(
            Func<Task<HttpResponseMessage>> sendRequest,
            Func<string, bool> isFinal,
            StatusCheckBackoff backoff,
            DateTime startedUtc,
            DateTime deadlineUtc,
            Func<int, string, TimeSpan, string> verboseMessage,
            Func<int, string, TimeSpan, string, string> timeoutMessage)
        {
            var cancellationToken = _context.CancellationToken;
            var checks = 0;
            string lastValue = null;
            string lastError = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checks++;

                try
                {
                    var response = await sendRequest();
                    if (response.IsSuccessStatusCode)
                    {
                        lastValue = await HttpClientHelper.ReadResponseStringAsync(response);
                        lastError = null;
                        if (isFinal(lastValue))
                        {
                            return new PollResult { Value = lastValue, Checks = checks, Elapsed = Elapsed(startedUtc) };
                        }
                    }
                    else if (HttpRetryHandler.IsTransientStatusCode(response.StatusCode))
                    {
                        lastError = $"HTTP {(int)response.StatusCode} {response.StatusCode}: {await HttpClientHelper.ReadContentForLogAsync(response)}";
                    }
                    else
                    {
                        throw new JobExecutionException($"Job: {_context.JobDetail.Key}. D365 request failed. Execution Id: {_executionId}. Status: {response.StatusCode}. Message: {await HttpClientHelper.ReadContentForLogAsync(response)}");
                    }
                }
                catch (HttpRequestException ex)
                {
                    // Network problem, e.g. DNS name cannot be resolved
                    lastError = ex.InnerException != null ? $"{ex.Message} {ex.InnerException.Message}" : ex.Message;
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // HttpClient timeout
                    lastError = $"Request timed out: {ex.Message}";
                }

                var now = _clock.UtcNow;
                if (now >= deadlineUtc)
                {
                    throw new JobExecutionException(timeoutMessage(checks, lastValue, Elapsed(startedUtc), lastError));
                }

                var delay = backoff.Next();
                if (now + delay > deadlineUtc)
                {
                    delay = deadlineUtc - now; // make the last check exactly at the deadline
                }

                if (lastError != null)
                {
                    Log.Warn(string.Format(Resources.Job_0_Temporary_error_while_waiting_for_D365_Execution_Id_1_Error_2_Next_check_in_3, _context.JobDetail.Key, _executionId, lastError, Round(delay)));
                }
                else
                {
                    Log.Verbose(_settings.LogVerbose, () => verboseMessage(checks, lastValue, Round(delay)));
                }

                await _clock.Delay(delay, cancellationToken);
            }
        }

        private TimeSpan Elapsed(DateTime startedUtc)
        {
            return Round(_clock.UtcNow - startedUtc);
        }

        private static TimeSpan Round(TimeSpan value)
        {
            return TimeSpan.FromSeconds(Math.Round(value.TotalSeconds));
        }

        private bool UploadFileToSftp(string filePath)
        {
            if (!_settings.UseSftpOutbound || _settings.OutboundSftpConfiguration == null)
            {
                return false;
            }

            if (!File.Exists(filePath))
            {
                Log.WarnFormat(CultureInfo.InvariantCulture,
                    "Job {0}: Local file '{1}' was not found for SFTP upload.",
                    _context.JobDetail.Key,
                    filePath);
                return false;
            }

            try
            {
                _sftpTransferService.UploadFile(_settings.OutboundSftpConfiguration, filePath, Log);
                return true;
            }
            catch (Exception ex)
            {
                var message = string.Format(CultureInfo.InvariantCulture,
                    "Job {0}: SFTP upload failed for file '{1}'.",
                    _context.JobDetail.Key,
                    filePath);
                Log.Error(message, ex);
                throw new JobExecutionException(message, ex, false);
            }
        }

        private void MoveToUploadedFolderIfExists(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            MoveToUploadedFolder(filePath);
        }

        private void MoveToUploadedFolder(string filePath)
        {
            var uploadedDir = Path.Combine(_settings.DownloadSuccessDir, "Uploaded");
            Directory.CreateDirectory(uploadedDir);
            var targetPath = Path.Combine(uploadedDir, Path.GetFileName(filePath));
            _retryPolicyForIo.Execute(() => FileOperationsHelper.Move(filePath, targetPath));
        }
    }
}
