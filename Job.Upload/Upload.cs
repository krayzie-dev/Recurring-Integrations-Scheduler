/* Copyright (c) Microsoft Corporation. All rights reserved.
   Licensed under the MIT License. */

using System;
using log4net;
using Polly;
using Quartz;
using RecurringIntegrationsScheduler.Common.Contracts;
using RecurringIntegrationsScheduler.Common.Helpers;
using RecurringIntegrationsScheduler.Common.JobSettings;
using RecurringIntegrationsScheduler.Job.Properties;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace RecurringIntegrationsScheduler.Job
{
    /// <summary>
    /// Job that uses enqueue/dequeue API to upload data to D365FO
    /// </summary>
    /// <seealso cref="IJob" />
    [DisallowConcurrentExecution]
    public class Upload : IJob
    {
        /// <summary>
        /// The log
        /// </summary>
        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The settings
        /// </summary>
        private readonly UploadJobSettings _settings = new UploadJobSettings();

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
        /// Gets or sets the input queue.
        /// </summary>
        /// <value>
        /// The input queue.
        /// </value>
        private ConcurrentQueue<DataMessage> InputQueue { get; set; }

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
        public Upload() : this(HttpClientHelperFactory.Default, SftpTransferService.Instance)
        {
        }

        internal Upload(IHttpClientHelperFactory httpClientHelperFactory, ISftpTransferService sftpTransferService)
        {
            _httpClientHelperFactory = httpClientHelperFactory ?? throw new ArgumentNullException(nameof(httpClientHelperFactory));
            _sftpTransferService = sftpTransferService ?? throw new ArgumentNullException(nameof(sftpTransferService));
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
            catch (Exception ex)
            {
                if (_settings.PauseJobOnException)
                {
                    await context.Scheduler.PauseJob(context.JobDetail.Key);
                    Log.WarnFormat(CultureInfo.InvariantCulture,
                        string.Format(Resources.Job_0_was_paused_because_of_error, _context.JobDetail.Key));
                }
                if (!string.Equals(context.Scheduler.SchedulerName, RecurringIntegrationsScheduler.Common.Contracts.SchedulerConstants.PrivateSchedulerName, StringComparison.Ordinal))
                {
                    // Quartz JobRunShell logs the exception (with inner exception and stack trace) - log once only.
                    throw new JobExecutionException(string.Format(Resources.Upload_job_0_failed, _context.JobDetail.Key), ex, false);
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
            InputQueue = new ConcurrentQueue<DataMessage>();

            DownloadFilesFromSftp();

            foreach (var dataMessage in FileOperationsHelper.GetFiles(MessageStatus.Input, _settings.InputDir, _settings.SearchPattern, SearchOption.AllDirectories, _settings.OrderBy, _settings.ReverseOrder))
            {
                Log.VerboseFormat(
                    _settings.LogVerbose,
                    CultureInfo.InvariantCulture,
                    Resources.Job_0_File_1_found_in_input_location,
                    _context.JobDetail.Key,
                    dataMessage.FullPath.Replace(@"{", @"{{").Replace(@"}", @"}}"));

                InputQueue.Enqueue(dataMessage);
            }

            if (!InputQueue.IsEmpty)
            {
                Log.InfoFormat(CultureInfo.InvariantCulture, string.Format(Resources.Job_0_Found_1_file_s_in_input_folder, _context.JobDetail.Key, InputQueue.Count));
                await ProcessInputQueue();
            }
        }

        /// <summary>
        /// Processes input queue
        /// </summary>
        /// <returns>
        /// Task object for continuation
        /// </returns>
        private async Task ProcessInputQueue()
        {
            using (_httpClientHelper = _httpClientHelperFactory.Create(_settings))
            {
                var fileCount = 0;

                while (InputQueue.TryDequeue(out DataMessage dataMessage))
                {
                    try
                    {
                        if (fileCount > 0 && _settings.DelayBetweenFiles > 0) //Only delay after first file and never after last.
                        {
                            await Task.Delay(TimeSpan.FromSeconds(_settings.DelayBetweenFiles));
                        }
                        fileCount++;

                        var sourceStream = _retryPolicyForIo.Execute(() => FileOperationsHelper.Read(dataMessage.FullPath, FileShare.None));
                        if (sourceStream == null) continue;//Nothing to do here

                        sourceStream.Seek(0, SeekOrigin.Begin);

                        Log.VerboseFormat(
                            _settings.LogVerbose,
                            CultureInfo.InvariantCulture,
                            Resources.Job_0_Uploading_file_1_File_size_2_bytes,
                            _context.JobDetail.Key,
                            dataMessage.FullPath.Replace(@"{", @"{{").Replace(@"}", @"}}"),
                            sourceStream.Length);
                        // Post enqueue file request
                        var response = await _httpClientHelper.PostStreamRequestAsync(_httpClientHelper.GetEnqueueUri(), sourceStream, dataMessage.Name);
                        sourceStream.Close();
                        sourceStream.Dispose();

                        if (response.IsSuccessStatusCode)
                        {
                            var messageId = await response.Content.ReadAsStringAsync();
                            var targetDataMessage = new DataMessage(dataMessage)
                            {
                                MessageId = messageId,
                                FullPath = Path.Combine(_settings.UploadSuccessDir, dataMessage.Name),
                                MessageStatus = MessageStatus.Enqueued,
                                SubmittedUtc = DateTime.UtcNow
                            };

                            // Move to success location
                            _retryPolicyForIo.Execute(() => FileOperationsHelper.Move(dataMessage.FullPath, targetDataMessage.FullPath));

                            if (_settings.ProcessingJobPresent)
                                _retryPolicyForIo.Execute(() => FileOperationsHelper.WriteStatusFile(targetDataMessage, _settings.StatusFileExtension));

                            Log.VerboseFormat(
                                _settings.LogVerbose,
                                CultureInfo.InvariantCulture,
                                Resources.Job_0_File_1_uploaded_successfully_Message_Id_2,
                                _context.JobDetail.Key,
                                dataMessage.FullPath.Replace(@"{", @"{{").Replace(@"}", @"}}"),
                                targetDataMessage.MessageId);
                        }
                        else
                        {
                            // Enqueue failed. Move message to error location.
                            Log.ErrorFormat(CultureInfo.InvariantCulture, string.Format(Resources.Job_0_Enqueuing_failed_for_file_1_Failure_response_Status_2_Reason_3, _context.JobDetail.Key, dataMessage.FullPath.Replace(@"{", @"{{").Replace(@"}", @"}}"), response.StatusCode, response.ReasonPhrase));

                            var targetDataMessage = new DataMessage(dataMessage)
                            {
                                FullPath = Path.Combine(_settings.UploadErrorsDir, dataMessage.Name),
                                MessageStatus = MessageStatus.Failed
                            };

                            // Move data to error location
                            _retryPolicyForIo.Execute(() => FileOperationsHelper.Move(dataMessage.FullPath, targetDataMessage.FullPath));

                            // Save the log with enqueue failure details
                            _retryPolicyForIo.Execute(() => FileOperationsHelper.WriteStatusLogFile(targetDataMessage, response, _settings.StatusFileExtension));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(string.Format(Resources.Job_0_Failure_processing_file_1_Exception_2, _context.JobDetail.Key, dataMessage.FullPath, ex.Message), ex);
                        throw;
                    }
                }
            }
        }

        private void DownloadFilesFromSftp()
        {
            if (!_settings.UseSftpInbound || _settings.InboundSftpConfiguration == null)
            {
                return;
            }

            try
            {
                var downloaded = _sftpTransferService.DownloadFiles(_settings.InboundSftpConfiguration, _settings.InputDir, Log);
                if (downloaded.Count > 0)
                {
                    Log.InfoFormat(CultureInfo.InvariantCulture,
                        "Job {0}: Downloaded {1} file(s) from SFTP to input folder.",
                        _context.JobDetail.Key,
                        downloaded.Count);
                }
            }
            catch (Exception ex)
            {
                var message = string.Format(CultureInfo.InvariantCulture,
                    "Job {0}: Failed to download files from SFTP.",
                    _context.JobDetail.Key);
                Log.Error(message, ex);
                throw new JobExecutionException(message, ex, false);
            }
        }
    }
}
