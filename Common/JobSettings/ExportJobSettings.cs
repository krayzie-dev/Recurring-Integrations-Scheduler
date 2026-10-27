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
    /// Serialize/deserialize download job settings
    /// </summary>
    /// <seealso cref="RecurringIntegrationsScheduler.Common.JobSettings" />
    public class ExportJobSettings : Settings
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

            DownloadSuccessDir = GetOptionalString(dataMap, SettingsConstants.DownloadSuccessDir);
            if (!string.IsNullOrEmpty(DownloadSuccessDir))
            {
                try
                {
                    Directory.CreateDirectory(DownloadSuccessDir);
                }
                catch (Exception ex)
                {
                    throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Download_success_directory_does_not_exist_or_cannot_be_accessed), ex);
                }
            }
            else
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Download_success_directory_is_missing_in_job_configuration));
            }

            DownloadErrorsDir = GetOptionalString(dataMap, SettingsConstants.DownloadErrorsDir);
            if (!string.IsNullOrEmpty(DownloadErrorsDir))
            {
                try
                {
                    Directory.CreateDirectory(DownloadErrorsDir);
                }
                catch (Exception ex)
                {
                    throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Download_errors_directory_does_not_exist_or_cannot_be_accessed), ex);
                }
            }
            else
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Download_errors_directory_is_missing_in_job_configuration));
            }

            UnzipPackage = GetOptionalBoolean(dataMap, SettingsConstants.UnzipPackage);

            AddTimestamp = GetOptionalBoolean(dataMap, SettingsConstants.AddTimestamp);

            DeletePackage = GetOptionalBoolean(dataMap, SettingsConstants.DeletePackage);

            UseSftpOutbound = GetOptionalBoolean(dataMap, SettingsConstants.UseSftpOutbound);
            if (UseSftpOutbound)
            {
                OutboundSftpConfiguration = ReadSftpConfiguration(
                    dataMap,
                    SettingsConstants.SftpOutboundHost,
                    SettingsConstants.SftpOutboundPort,
                    SettingsConstants.SftpOutboundUsername,
                    SettingsConstants.SftpOutboundPassword,
                    SettingsConstants.SftpOutboundUseKey,
                    SettingsConstants.SftpOutboundKeyPath,
                    SettingsConstants.SftpOutboundKeyPassphrase,
                    SettingsConstants.SftpOutboundRemoteFolder,
                    SettingsConstants.SftpOutboundFileMask);

                if (OutboundSftpConfiguration == null || !OutboundSftpConfiguration.IsConfigured)
                {
                    throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture,
                        "Job {0}: SFTP outbound configuration is incomplete.", context?.JobDetail?.Key));
                }
            }

            DataProject = GetOptionalString(dataMap, SettingsConstants.DataProject);

            if (string.IsNullOrEmpty(DataProject))
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Data_project_is_missing_in_job_configuration));
            }

            Company = GetOptionalString(dataMap, SettingsConstants.Company);

            if (string.IsNullOrEmpty(Company))
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Company_is_missing_in_job_configuration));
            }

            DelayBetweenStatusCheck = GetOptionalInt(dataMap, SettingsConstants.DelayBetweenStatusCheck);
            MaxDelayBetweenStatusCheck = GetOptionalPositiveInt(dataMap, SettingsConstants.MaxDelayBetweenStatusCheck, StatusCheckBackoff.DefaultMaxDelaySeconds);
            StatusCheckTimeoutMinutes = GetOptionalPositiveInt(dataMap, SettingsConstants.StatusCheckTimeoutMinutes, StatusCheckBackoff.DefaultStatusCheckTimeoutMinutes);
        }

        #region Members

        /// <summary>
        /// Gets the download success dir.
        /// </summary>
        /// <value>
        /// The download success dir.
        /// </value>
        public string DownloadSuccessDir { get; private set; }

        /// <summary>
        /// Gets the download errors dir.
        /// </summary>
        /// <value>
        /// The download errors dir.
        /// </value>
        public string DownloadErrorsDir { get; private set; }

        /// <summary>
        /// Gets a value indicating whether [unzip package].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [unzip package]; otherwise, <c>false</c>.
        /// </value>
        public bool UnzipPackage { get; private set; }

        /// <summary>
        /// Gets a value indicating whether [add timestamp].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [add timestamp]; otherwise, <c>false</c>.
        /// </value>
        public bool AddTimestamp { get; private set; }

        /// <summary>
        /// Gets a value indicating whether [delete package].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [delete package]; otherwise, <c>false</c>.
        /// </value>
        public bool DeletePackage { get; private set; }

        /// <summary>
        /// Gets data project.
        /// </summary>
        /// <value>
        /// Data project.
        /// </value>
        public string DataProject { get; private set; }

        /// <summary>
        /// Gets legal entity id.
        /// </summary>
        /// <value>
        /// Legal entity id.
        /// </value>
        public string Company { get; private set; }

        /// <summary>
        /// Gets or sets delay between status check.
        /// </summary>
        /// <value>
        /// Delay between status checks.
        /// </value>
        public int DelayBetweenStatusCheck { get; private set; }

        /// <summary>
        /// Gets maximum delay in seconds between status checks.
        /// </summary>
        public int MaxDelayBetweenStatusCheck { get; private set; }

        /// <summary>
        /// Gets time in minutes after which unfinished export is reported as an error.
        /// </summary>
        public int StatusCheckTimeoutMinutes { get; private set; }

        public bool UseSftpOutbound { get; private set; }

        public SftpConfiguration OutboundSftpConfiguration { get; private set; }

        #endregion
    }
}
