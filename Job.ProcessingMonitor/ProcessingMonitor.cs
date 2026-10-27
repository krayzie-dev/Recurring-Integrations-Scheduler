/* Copyright (c) Microsoft Corporation. All rights reserved.
   Licensed under the MIT License. */

using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Polly;
using Quartz;
using RecurringIntegrationsScheduler.Common.Contracts;
using RecurringIntegrationsScheduler.Common.Helpers;
using RecurringIntegrationsScheduler.Common.JobSettings;
using RecurringIntegrationsScheduler.Job.Properties;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace RecurringIntegrationsScheduler.Job
{
    /// <summary>
    /// Job that checks processing status of recurring data job
    /// </summary>
    /// <seealso cref="IJob" />
    [DisallowConcurrentExecution]
    public class ProcessingMonitor : IJob
    {
        /// <summary>
        /// The log
        /// </summary>
        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The settings
        /// </summary>
        private readonly ProcessingJobSettings _settings = new ProcessingJobSettings();

        /// <summary>
        /// The context
        /// </summary>
        private IJobExecutionContext _context;

        /// <summary>
        /// The HTTP client helper
        /// </summary>
        private IHttpClientHelper _httpClientHelper;
        private readonly IHttpClientHelperFactory _httpClientHelperFactory;

        /// <summary>
        /// Gets or sets the enqueued jobs.
        /// </summary>
        /// <value>
        /// The enqueued jobs.
        /// </value>
        private ConcurrentQueue<DataMessage> EnqueuedJobs { get; set; }

        /// <summary>
        /// Retry policy for IO operations
        /// </summary>
        private Policy _retryPolicyForIo;

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
        public ProcessingMonitor() : this(HttpClientHelperFactory.Default)
        {
        }

        internal ProcessingMonitor(IHttpClientHelperFactory httpClientHelperFactory)
            : this(httpClientHelperFactory, SystemPollingClock.Instance)
        {
        }

        internal ProcessingMonitor(IHttpClientHelperFactory httpClientHelperFactory, IPollingClock clock)
        {
            _httpClientHelperFactory = httpClientHelperFactory ?? throw new ArgumentNullException(nameof(httpClientHelperFactory));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>
        /// Time source used for timeouts and waiting
        /// </summary>
        private readonly IPollingClock _clock;

        public async Task Execute(IJobExecutionContext context)
        {
            try
            {
                _context = context;
                _settings.Initialize(context);

                if (_settings.IndefinitePause)
                {
                    await context.Scheduler.PauseJob(context.JobDetail.Key);
                    Log.InfoFormat(CultureInfo.InvariantCulture,
                        string.Format(Resources.Job_0_was_paused_indefinitely, _context.JobDetail.Key));
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
                // Job was interrupted (e.g. service stop). Remaining status files will be checked in the next run.
                Log.Warn(string.Format(Resources.Job_0_was_interrupted, context.JobDetail.Key));
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
                    throw new JobExecutionException(string.Format(Resources.Processing_monitor_job_0_failed, _context.JobDetail.Key), ex, false);
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
            EnqueuedJobs = new ConcurrentQueue<DataMessage>();
            using (_httpClientHelper = _httpClientHelperFactory.Create(_settings))
            {
                foreach (var dataMessage in FileOperationsHelper.GetStatusFiles(MessageStatus.InProcess, _settings.UploadSuccessDir, "*" + _settings.StatusFileExtension))
                {
                    Log.VerboseFormat(
                        _settings.LogVerbose,
                        CultureInfo.InvariantCulture,
                        Resources.Job_0_File_1_found_in_processing_location_and_added_to_queue_for_status_check,
                        _context.JobDetail.Key,
                        dataMessage.FullPath.Replace(@"{", @"{{").Replace(@"}", @"}}"));
                    EnqueuedJobs.Enqueue(dataMessage);
                }

                if (!EnqueuedJobs.IsEmpty)
                {
                    await ProcessEnqueuedQueue();
                }
            }
        }

        /// <summary>
        /// Process enqueued files
        /// </summary>
        /// <returns></returns>
        private async Task ProcessEnqueuedQueue()
        {
            var fileCount = 0;

            while (EnqueuedJobs.TryDequeue(out DataMessage dataMessage))
            {
                if (fileCount > 0 && _settings.StatusCheckInterval > 0) //Only delay after first file and never after last.
                {
                    await _clock.Delay(TimeSpan.FromSeconds(_settings.StatusCheckInterval), _context.CancellationToken);
                }
                fileCount++;

                // Check status for current item with message id - item.Key
                var jobStatusDetail = await GetStatus(dataMessage.MessageId);

                // If status was found and is not null,
                if (jobStatusDetail != null)
                {
                    await PostProcessMessage(jobStatusDetail, dataMessage);
                }
            }
        }

        /// <summary>
        /// Process message once status is received by moving document to the
        /// appropriate folder and writing out a log for the processed document
        /// </summary>
        /// <param name="jobStatusDetail">DataJobStatusDetail object</param>
        /// <param name="dataMessage">Name of the file whose status is being processed</param>
        private async Task PostProcessMessage(DataJobStatusDetail jobStatusDetail, DataMessage dataMessage)
        {
            if (jobStatusDetail?.DataJobStatus == null)
            {
                return;
            }
            dataMessage.DataJobState = jobStatusDetail.DataJobStatus.DataJobState;
            // Status file is rewritten below - keep original submission time of status files written by older versions.
            dataMessage.SubmittedUtc ??= FileOperationsHelper.GetSubmittedUtc(dataMessage, _settings.StatusFileExtension);

            _retryPolicyForIo.Execute(() => FileOperationsHelper.WriteStatusFile(dataMessage, _settings.StatusFileExtension));

            switch (dataMessage.DataJobState)
            {
                case DataJobState.Processed:
                {
                    // Move message file and delete processing status file
                    _retryPolicyForIo.Execute(() => FileOperationsHelper.MoveDataToTarget(dataMessage.FullPath, Path.Combine(_settings.ProcessingSuccessDir, dataMessage.Name), true, _settings.StatusFileExtension));
                }
                break;

                case DataJobState.PostProcessError:
                case DataJobState.PreProcessError:
                case DataJobState.ProcessedWithErrors:
                {
                    var targetDataMessage = new DataMessage(dataMessage)
                    {
                        FullPath = Path.Combine(_settings.ProcessingErrorsDir, dataMessage.Name),
                        MessageStatus = MessageStatus.Failed
                    };
                    _retryPolicyForIo.Execute(() => FileOperationsHelper.MoveDataToTarget(dataMessage.FullPath, targetDataMessage.FullPath, false, _settings.StatusFileExtension));
                    _retryPolicyForIo.Execute(() => FileOperationsHelper.WriteStatusLogFile(jobStatusDetail, targetDataMessage, null, _settings.StatusFileExtension));

                    if (_settings.GetExecutionErrors)
                    {
                        Log.VerboseFormat(_settings.LogVerbose, CultureInfo.InvariantCulture, Resources.Job_0_Trying_to_download_execution_errors, _context.JobDetail.Key);
                        var response = await _httpClientHelper.GetExecutionErrors(dataMessage.MessageId);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new JobExecutionException(string.Format(Resources.Job_0_download_of_execution_errors_failed_1, _context.JobDetail.Key, $"Status: {response.StatusCode}. Message: {await HttpClientHelper.ReadContentForLogAsync(response)}"));
                        }
                        using Stream downloadedStream = await response.Content.ReadAsStreamAsync();
                        var errorsFileName = $"{Path.GetFileNameWithoutExtension(dataMessage.Name)}-ExecutionErrors-{DateTime.Now:yyyy-MM-dd_HH-mm-ss-ffff}.txt";
                        var errorsFilePath = Path.Combine(Path.GetDirectoryName(dataMessage.FullPath.Replace(_settings.UploadSuccessDir, _settings.ProcessingErrorsDir)), errorsFileName);
                        var dataMessageForErrorsFile = new DataMessage()
                        {
                            FullPath = errorsFilePath,
                            Name = errorsFileName,
                            MessageStatus = MessageStatus.Failed
                        };
                        _retryPolicyForIo.Execute(() => FileOperationsHelper.Create(downloadedStream, dataMessageForErrorsFile.FullPath));
                    }
                }
                break;

                default: // still in progress (Enqueued, Dequeued, InProcess, ...)
                {
                    var submittedUtc = FileOperationsHelper.GetSubmittedUtc(dataMessage, _settings.StatusFileExtension);
                    if (submittedUtc == null || _clock.UtcNow - submittedUtc.Value < TimeSpan.FromMinutes(_settings.StatusCheckTimeoutMinutes))
                    {
                        break; // still waiting - status will be checked in the next run
                    }

                    Log.Error(string.Format(Resources.Job_0_Processing_did_not_finish_within_1_minutes_File_2_Message_Id_3_State_4_Submitted_5, _context.JobDetail.Key, _settings.StatusCheckTimeoutMinutes, dataMessage.FullPath, dataMessage.MessageId, dataMessage.DataJobState, submittedUtc.Value.ToLocalTime()));
                    var targetDataMessage = new DataMessage(dataMessage)
                    {
                        FullPath = Path.Combine(_settings.ProcessingErrorsDir, dataMessage.Name),
                        MessageStatus = MessageStatus.Failed
                    };
                    _retryPolicyForIo.Execute(() => FileOperationsHelper.MoveDataToTarget(dataMessage.FullPath, targetDataMessage.FullPath, false, _settings.StatusFileExtension));
                    _retryPolicyForIo.Execute(() => FileOperationsHelper.WriteStatusLogFile(jobStatusDetail, targetDataMessage, null, _settings.StatusFileExtension));
                }
                break;
            }
        }

        /// <summary>
        /// Get submitted job status using the Enqueue response -
        /// MessageId
        /// </summary>
        /// <param name="message">Correlation identifier for the submitted job returned as the Enqueue response</param>
        /// <returns>
        /// DataJobStatusDetail object that includes detailed job status
        /// </returns>
        private async Task<DataJobStatusDetail> GetStatus(string message)
        {
            //send a request to get the message status
            var response = await _httpClientHelper.GetRequestAsync(_httpClientHelper.GetJobStatusUri(message));

            if (response.IsSuccessStatusCode)
            {
                // Deserialize response to the DataJobStatusDetail object
                Log.VerboseFormat(_settings.LogVerbose, CultureInfo.InvariantCulture, Resources.Job_0_Successfully_received_job_status_for_message_id_1, _context.JobDetail.Key, message);
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<DataJobStatusDetail>(payload, new StringEnumConverter());
            }
            else
            {
                Log.ErrorFormat(CultureInfo.InvariantCulture, string.Format(Resources.Job_0_data_job_status_check_request_failed_Status_code_1_Reason_2, _context.JobDetail.Key, response.StatusCode, response.ReasonPhrase));
                return null;
            }
        }
    }
}
