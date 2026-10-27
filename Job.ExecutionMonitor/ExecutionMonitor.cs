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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;

namespace RecurringIntegrationsScheduler.Job
{
    /// <summary>
    /// Job that checks execution status of import jobs
    /// </summary>
    /// <seealso cref="IJob" />
    [DisallowConcurrentExecution]
    public class ExecutionMonitor : IJob
    {
        /// <summary>
        /// The log
        /// </summary>
        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The settings
        /// </summary>
        private readonly ExecutionJobSettings _settings = new ExecutionJobSettings();

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

        private StreamWriter _streamWriter;

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
        public ExecutionMonitor() : this(HttpClientHelperFactory.Default)
        {
        }

        internal ExecutionMonitor(IHttpClientHelperFactory httpClientHelperFactory)
            : this(httpClientHelperFactory, SystemPollingClock.Instance)
        {
        }

        internal ExecutionMonitor(IHttpClientHelperFactory httpClientHelperFactory, IPollingClock clock)
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
                    throw new JobExecutionException(string.Format(Resources.Execution_monitor_job_0_failed, _context.JobDetail.Key), ex, false);
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
                if (fileCount > 0 && _settings.DelayBetweenStatusCheck > 0) //Only delay after first file and never after last.
                {
                    await _clock.Delay(TimeSpan.FromSeconds(_settings.DelayBetweenStatusCheck), _context.CancellationToken);
                }
                fileCount++;

                // Check status for current item with message id - item.Key
                var responseGetExecutionSummaryStatus = await _httpClientHelper.GetExecutionSummaryStatus(dataMessage.MessageId);
                if(!responseGetExecutionSummaryStatus.IsSuccessStatusCode)
                {
                    throw new JobExecutionException($@"Job: {_settings.JobKey}. GetExecutionSummaryStatus request failed.");
                }
                var jobStatusDetail = await HttpClientHelper.ReadResponseStringAsync(responseGetExecutionSummaryStatus);

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
        /// <param name="executionStatus">Execution status</param>
        /// <param name="dataMessage">Name of the file whose status is being processed</param>
        private async Task PostProcessMessage(string executionStatus, DataMessage dataMessage)
        {
            Log.VerboseFormat(_settings.LogVerbose, CultureInfo.InvariantCulture, Resources.Job_0_ExecutionId_1_status_check_returned_2, _context.JobDetail.Key, dataMessage.MessageId, executionStatus);
            switch (executionStatus)
            {
                case "Succeeded":
                    {
                        // Move message file and delete processing status file
                        var processingSuccessDestination = dataMessage.FullPath.Replace(_settings.UploadSuccessDir, _settings.ProcessingSuccessDir);
                        _retryPolicyForIo.Execute(() => FileOperationsHelper.MoveDataToTarget(dataMessage.FullPath, processingSuccessDestination, true, _settings.StatusFileExtension));
                        await CreateLinkToExecutionSummaryPage(dataMessage.MessageId, processingSuccessDestination);
                    }
                    break;
                case "Unknown":
                case "PartiallySucceeded":
                case "Failed":
                case "Canceled":
                    {
                        var processingErrorDestination = dataMessage.FullPath.Replace(_settings.UploadSuccessDir, _settings.ProcessingErrorsDir);
                        _retryPolicyForIo.Execute(() => FileOperationsHelper.MoveDataToTarget(dataMessage.FullPath, processingErrorDestination, true, _settings.StatusFileExtension));
                        await CreateLinkToExecutionSummaryPage(dataMessage.MessageId, processingErrorDestination);
                        if (_settings.GetImportTargetErrorKeysFile)
                        {
                            Log.VerboseFormat(_settings.LogVerbose, CultureInfo.InvariantCulture, Resources.Job_0_Checking_if_error_keys_file_was_generated, _context.JobDetail.Key);
                            var fileWithManifest = processingErrorDestination;
                            if(!string.IsNullOrEmpty(_settings.PackageTemplate))
                            {
                                fileWithManifest = _settings.PackageTemplate;
                            }
                            var entitiesInPackage = GetEntitiesNamesInPackage(fileWithManifest);
                            foreach(var entity in entitiesInPackage)
                            {
                                Log.VerboseFormat(_settings.LogVerbose, CultureInfo.InvariantCulture, Resources.Job_0_Checking_for_error_keys_for_data_entity_1, _context.JobDetail.Key, entity);
                                var errorsExistResponse = await _httpClientHelper.GenerateImportTargetErrorKeysFile(dataMessage.MessageId, entity);
                                if (errorsExistResponse.IsSuccessStatusCode && Convert.ToBoolean(await HttpClientHelper.ReadResponseStringAsync(errorsExistResponse)))
                                {
                                    var errorFileUrl = string.Empty;
                                    HttpResponseMessage errorFileUrlResponse;
                                    var errorFileBackoff = new StatusCheckBackoff(_settings.DelayBetweenStatusCheck, _settings.MaxDelayBetweenStatusCheck);
                                    var errorFileDeadline = _clock.UtcNow.AddMinutes(_settings.ErrorKeysFileTimeoutMinutes);
                                    while (true)
                                    {
                                        errorFileUrlResponse = await _httpClientHelper.GetImportTargetErrorKeysFileUrl(dataMessage.MessageId, entity);
                                        if (!errorFileUrlResponse.IsSuccessStatusCode)
                                        {
                                            break;
                                        }
                                        errorFileUrl = await HttpClientHelper.ReadResponseStringAsync(errorFileUrlResponse);
                                        if (errorFileUrl.Length > 0 || _clock.UtcNow >= errorFileDeadline)
                                        {
                                            break;
                                        }
                                        await _clock.Delay(errorFileBackoff.Next(), _context.CancellationToken);
                                    }

                                    if (string.IsNullOrEmpty(errorFileUrl))
                                    {
                                        Log.Warn($@"Job: {_settings.JobKey}. Error keys file for entity '{entity}' was not available within {_settings.ErrorKeysFileTimeoutMinutes} minutes. Execution Id: {dataMessage.MessageId}. Job will continue.");
                                        continue;
                                    }

                                    var response = await _httpClientHelper.GetRequestAsync(new UriBuilder(errorFileUrl).Uri, false);
                                    if (response.IsSuccessStatusCode)
                                    {
                                        using Stream downloadedStream = await response.Content.ReadAsStreamAsync();
                                        var errorsFileName = $"{Path.GetFileNameWithoutExtension(dataMessage.Name)}-{entity}-ErrorKeys-{DateTime.Now:yyyy-MM-dd_HH-mm-ss-ffff}.txt";
                                        var errorsFilePath = Path.Combine(Path.GetDirectoryName(dataMessage.FullPath.Replace(_settings.UploadSuccessDir, _settings.ProcessingErrorsDir)), errorsFileName);
                                        var dataMessageForErrorsFile = new DataMessage()
                                        {
                                            FullPath = errorsFilePath,
                                            Name = errorsFileName,
                                            MessageStatus = MessageStatus.Failed
                                        };
                                        _retryPolicyForIo.Execute(() => FileOperationsHelper.Create(downloadedStream, dataMessageForErrorsFile.FullPath));
                                    }
                                    else
                                    {
                                        Log.Warn($@"Job: {_settings.JobKey}. Download of error keys file failed. Job will continue processing other packages.
Uploaded file: {dataMessage.FullPath}
Execution status: {executionStatus}
Error file URL: {errorFileUrl}");
                                    }
                                }
                            }
                        }

                        if (_settings.GetExecutionErrors)
                        {
                            Log.VerboseFormat(_settings.LogVerbose, CultureInfo.InvariantCulture, Resources.Job_0_Trying_to_download_execution_errors, _context.JobDetail.Key);
                            var response = await _httpClientHelper.GetExecutionErrors(dataMessage.MessageId);
                            if (response.IsSuccessStatusCode)
                            {
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
                            else
                            {
                                Log.Warn($@"Job: {_settings.JobKey}. Download of execution error details failed. Job will continue processing other packages.
Uploaded file: {dataMessage.FullPath}
Execution status: {executionStatus}
Message Id: {dataMessage.MessageId}");
                            }
                        }
                    }
                    break;
                default: //"NotRun", "Executing"
                    {
                        var submittedUtc = FileOperationsHelper.GetSubmittedUtc(dataMessage, _settings.StatusFileExtension);
                        if (submittedUtc == null || _clock.UtcNow - submittedUtc.Value < TimeSpan.FromMinutes(_settings.StatusCheckTimeoutMinutes))
                        {
                            break; // still waiting - status will be checked in the next run
                        }

                        Log.Error(string.Format(Resources.Job_0_Import_did_not_finish_within_1_minutes_File_2_Execution_Id_3_Status_4_Submitted_5, _context.JobDetail.Key, _settings.StatusCheckTimeoutMinutes, dataMessage.FullPath, dataMessage.MessageId, executionStatus, submittedUtc.Value.ToLocalTime()));
                        var processingErrorDestination = dataMessage.FullPath.Replace(_settings.UploadSuccessDir, _settings.ProcessingErrorsDir);
                        _retryPolicyForIo.Execute(() => FileOperationsHelper.MoveDataToTarget(dataMessage.FullPath, processingErrorDestination, true, _settings.StatusFileExtension));
                        await CreateLinkToExecutionSummaryPage(dataMessage.MessageId, processingErrorDestination);
                    }
                    break;
            }
        }

        private async Task CreateLinkToExecutionSummaryPage(string messageId, string filePath)
        {
            var directoryName = Path.GetDirectoryName(filePath);
            if (directoryName == null)
                throw new NullReferenceException();

            var logFilePath = Path.Combine(directoryName, Path.GetFileNameWithoutExtension(filePath) + ".url");

            var linkUrlResponse = await _httpClientHelper.GetExecutionSummaryPageUrl(messageId);
            var linkUrl = string.Empty;
            if (linkUrlResponse.IsSuccessStatusCode)
            {
                linkUrl = await HttpClientHelper.ReadResponseStringAsync(linkUrlResponse);
            }

            using (_streamWriter = new StreamWriter(logFilePath))
            {
                _streamWriter.WriteLine("[InternetShortcut]");
                _streamWriter.WriteLine("URL=" + linkUrl);
                _streamWriter.Flush();
            }
        }

        /// <summary>
        /// Reads the Manifest.xml file of the datapackage to get entity names to query for error key file
        /// </summary>
        /// <param name="filename">The data package file</param>
        /// <returns>Entities list</returns>
        private List<string> GetEntitiesNamesInPackage(string fileName)
        {
            Log.VerboseFormat(_settings.LogVerbose, CultureInfo.InvariantCulture, Resources.Job_0_Looking_for_data_entities_in_manifest_file_1, _context.JobDetail.Key, fileName);

            var enitiesList = new List<string>();

            using (var package = ZipFile.OpenRead(fileName))
            {
                foreach (var entry in package.Entries)
                {
                    if (entry.FullName.Equals("Manifest.xml", StringComparison.OrdinalIgnoreCase))
                    {
                        var manifestPath = Path.Combine(Path.GetTempPath(), $"{_context.JobDetail.Key}-{entry.FullName}");
                        entry.ExtractToFile(manifestPath, true);

                        var doc = new XmlDocument();
                        XmlNodeList entities;
                        using (var manifestFile = new StreamReader(manifestPath))
                        {
                            doc.Load(new XmlTextReader(manifestFile) { Namespaces = false });
                            entities = doc.SelectNodes("//EntityName");
                        }

                        foreach (XmlNode node in entities)
                        {
                            enitiesList.Add(node.InnerText);
                        }
                    }
                }
            }
            return enitiesList;
        }
    }
}
