/* Copyright (c) Microsoft Corporation. All rights reserved.
   Licensed under the MIT License. */

using Quartz;
using RecurringIntegrationsScheduler.Common.Contracts;
using RecurringIntegrationsScheduler.Common.Helpers;
using RecurringIntegrationsScheduler.Common.Properties;
using System;
using System.Globalization;
using System.IO;

namespace RecurringIntegrationsScheduler.Common.JobSettings
{
    /// <summary>
    /// Serialize/deserialize execution monitor job settings
    /// </summary>
    /// <seealso cref="RecurringIntegrationsScheduler.Common.JobSettings" />
    public class ExecutionJobSettings : Settings
    {
        /// <summary>
        /// Initialize and verify settings for job
        /// </summary>
        /// <param name="context">The context.</param>
        /// <exception cref="Quartz.JobExecutionException">
        /// </exception>
        public override void Initialize(IJobExecutionContext context)
        {
            var dataMap = context.JobDetail.JobDataMap;

            base.Initialize(context);

            UploadSuccessDir = GetOptionalString(dataMap, SettingsConstants.UploadSuccessDir);
            if (!string.IsNullOrEmpty(UploadSuccessDir))
            {
                try
                {
                    Directory.CreateDirectory(UploadSuccessDir);
                }
                catch (Exception ex)
                {
                    throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Upload_success_directory_does_not_exist_or_cannot_be_accessed), ex);
                }
            }
            else
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Upload_success_directory_is_missing_in_job_configuration));
            }

            ProcessingSuccessDir = GetOptionalString(dataMap, SettingsConstants.ProcessingSuccessDir);
            if (!string.IsNullOrEmpty(ProcessingSuccessDir))
            {
                try
                {
                    Directory.CreateDirectory(ProcessingSuccessDir);
                }
                catch (Exception ex)
                {
                    throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Processing_success_directory_does_not_exist_or_cannot_be_accessed), ex);
                }
            }
            else
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Processing_success_directory_is_missing_in_job_configuration));
            }

            ProcessingErrorsDir = GetOptionalString(dataMap, SettingsConstants.ProcessingErrorsDir);
            if (!string.IsNullOrEmpty(ProcessingErrorsDir))
            {
                try
                {
                    Directory.CreateDirectory(ProcessingErrorsDir);
                }
                catch (Exception ex)
                {
                    throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Processing_errors_directory_does_not_exist_or_cannot_be_accessed), ex);
                }
            }
            else
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Processing_errors_directory_is_missing_in_job_configuration));
            }

            StatusFileExtension = GetOptionalString(dataMap, "StatusFileExtension");
            if (string.IsNullOrEmpty(StatusFileExtension))
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Extension_of_status_files_is_missing_in_job_configuration));
            }

            GetImportTargetErrorKeysFile = GetOptionalBoolean(dataMap, SettingsConstants.GetImportTargetErrorKeysFile);

            PackageTemplate = GetOptionalString(dataMap, SettingsConstants.PackageTemplate);

            DelayBetweenStatusCheck = GetOptionalInt(dataMap, SettingsConstants.DelayBetweenStatusCheck);
            MaxDelayBetweenStatusCheck = GetOptionalPositiveInt(dataMap, SettingsConstants.MaxDelayBetweenStatusCheck, StatusCheckBackoff.DefaultMaxDelaySeconds);
            StatusCheckTimeoutMinutes = GetOptionalPositiveInt(dataMap, SettingsConstants.StatusCheckTimeoutMinutes, StatusCheckBackoff.DefaultStatusCheckTimeoutMinutes);
            ErrorKeysFileTimeoutMinutes = GetOptionalPositiveInt(dataMap, SettingsConstants.ErrorKeysFileTimeoutMinutes, StatusCheckBackoff.DefaultErrorKeysFileTimeoutMinutes);
        }

        #region Members

        /// <summary>
        /// Gets the upload success dir.
        /// </summary>
        /// <value>
        /// The upload success dir.
        /// </value>
        public string UploadSuccessDir { get; private set; }

        /// <summary>
        /// Gets the processing success dir.
        /// </summary>
        /// <value>
        /// The processing success dir.
        /// </value>
        public string ProcessingSuccessDir { get; private set; }

        /// <summary>
        /// Gets the processing errors dir.
        /// </summary>
        /// <value>
        /// The processing errors dir.
        /// </value>
        public string ProcessingErrorsDir { get; private set; }

        /// <summary>
        /// Gets the status file extension.
        /// </summary>
        /// <value>
        /// The status file extension.
        /// </value>
        public string StatusFileExtension { get; private set; }

        /// <summary>
        /// Gets a value indicating whether to download [error keys file].
        /// </summary>
        /// <value>
        /// <c>true</c> if [GetImportTargetErrorKeysFile] is set otherwise, <c>false</c>.
        /// </value>
        public bool GetImportTargetErrorKeysFile { get; private set; }

        /// <summary>
        /// Package template location.
        /// </summary>
        /// <value>
        /// Package template location.
        /// </value>
        public string PackageTemplate { get; private set; }

        /// <summary>
        /// Gets or sets delay between status check.
        /// </summary>
        /// <value>
        /// Delay between status checks.
        /// </value>
        public int DelayBetweenStatusCheck { get; private set; }

        /// <summary>
        /// Gets maximum delay in seconds between checks for error keys file.
        /// </summary>
        public int MaxDelayBetweenStatusCheck { get; private set; }

        /// <summary>
        /// Gets time in minutes after which unfinished import execution is moved to errors.
        /// </summary>
        public int StatusCheckTimeoutMinutes { get; private set; }

        /// <summary>
        /// Gets time in minutes to wait for import target error keys file.
        /// </summary>
        public int ErrorKeysFileTimeoutMinutes { get; private set; }

        #endregion
    }
}