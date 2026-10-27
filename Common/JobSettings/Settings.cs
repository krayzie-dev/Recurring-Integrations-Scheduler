/* Copyright (c) Microsoft Corporation. All rights reserved.
   Licensed under the MIT License. */

using log4net;
using Quartz;
using RecurringIntegrationsScheduler.Common.Contracts;
using RecurringIntegrationsScheduler.Common.Helpers;
using RecurringIntegrationsScheduler.Common.Properties;
using System;
using System.Globalization;
using System.Security;

namespace RecurringIntegrationsScheduler.Common.JobSettings
{
    /// <summary>
    /// Serialize/deserialize common job settings
    /// </summary>
    public class Settings
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(Settings));

        /// <summary>
        /// Initialize and verify settings for job
        /// </summary>
        /// <param name="context">The context.</param>
        /// <exception cref="JobExecutionException">
        /// </exception>
        public virtual void Initialize(IJobExecutionContext context)
        {
            var dataMap = context.JobDetail.JobDataMap;

            AosUri = GetOptionalString(dataMap, SettingsConstants.AosUri);
            if (string.IsNullOrEmpty(AosUri))
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.AOS_URL_is_missing_in_job_configuration));
            }
            //remove trailing slash if any
            AosUri = AosUri.TrimEnd('/');

            AzureAuthEndpoint = GetOptionalString(dataMap, SettingsConstants.AzureAuthEndpoint);
            if (string.IsNullOrEmpty(AzureAuthEndpoint))
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Azure_authentication_endpoint_URL_is_missing_in_job_configuration));
            }

            AadTenant = GetOptionalString(dataMap, SettingsConstants.AadTenant);
            if (string.IsNullOrEmpty(AadTenant))
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.AAD_tenant_id_is_missing_in_job_configuration));
            }

            if (dataMap.ContainsKey(SettingsConstants.UseADAL))
            {
                UseADAL = GetOptionalBoolean(dataMap, SettingsConstants.UseADAL);
            }
            else
            {
                UseADAL = false;
            }
            if (UseADAL)
            {
                Log.WarnFormat(CultureInfo.InvariantCulture,
                    "Job {0}: 'UseADAL' is deprecated and will be ignored. MSAL authentication will be used instead.",
                    context?.JobDetail?.Key);
                UseADAL = false;
            }

            UseServiceAuthentication = GetOptionalBoolean(dataMap, SettingsConstants.UseServiceAuthentication);

            var aadClientIdStr = GetOptionalString(dataMap, SettingsConstants.AadClientId);
            if (!Guid.TryParse(aadClientIdStr, out Guid aadClientGuid) || (Guid.Empty == aadClientGuid))
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Azure_application_client_id_is_missing_or_is_not_a_GUID_in_job_configuration));
            }
            AadClientId = aadClientGuid;

            if (UseServiceAuthentication)
            {
                AadClientSecret = GetOptionalString(dataMap, SettingsConstants.AadClientSecret);
                if (string.IsNullOrEmpty(AadClientSecret))
                {
                    throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.Azure_web_application_secret_is_missing_in_job_configuration));
                }
                AadClientSecret = EncryptDecrypt.Decrypt(AadClientSecret);
            }
            else
            {
                UserName = GetOptionalString(dataMap, SettingsConstants.UserName);
                if (string.IsNullOrEmpty(UserName))
                {
                    throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.User_principal_name_is_missing_in_job_configuration));
                }

                var encryptedPassword = GetOptionalString(dataMap, SettingsConstants.UserPassword);
                if (string.IsNullOrEmpty(encryptedPassword))
                {
                    throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.User_password_is_missing_in_job_configuration));
                }
                var decryptedPassword = EncryptDecrypt.Decrypt(encryptedPassword);
                SetUserPassword(decryptedPassword);
            }

            DelayBetweenFiles = GetOptionalInt(dataMap, SettingsConstants.DelayBetweenFiles);

            RetryCount = GetOptionalInt(dataMap, SettingsConstants.RetryCount);
            if (RetryCount == 0) 
            {
                RetryCount = 1;
            }

            RetryDelay = GetOptionalInt(dataMap, SettingsConstants.RetryDelay);
            if (RetryDelay == 0) 
            {
                RetryDelay = 10; //seconds
            }

            PauseJobOnException = GetOptionalBoolean(dataMap, SettingsConstants.PauseJobOnException);

            IndefinitePause = GetOptionalBoolean(dataMap, SettingsConstants.IndefinitePause);

            ImportFromPackageActionPath = GetOptionalString(dataMap, SettingsConstants.ImportFromPackageActionPath);
            if (string.IsNullOrEmpty(ImportFromPackageActionPath))
            {
                ImportFromPackageActionPath = PackageApiActions.ImportFromPackageActionPath;
            }

            GetAzureWriteUrlActionPath = GetOptionalString(dataMap, SettingsConstants.GetAzureWriteUrlActionPath);
            if (string.IsNullOrEmpty(GetAzureWriteUrlActionPath))
            {
                GetAzureWriteUrlActionPath = PackageApiActions.GetAzureWriteUrlActionPath;
            }

            GetExecutionSummaryStatusActionPath = GetOptionalString(dataMap, SettingsConstants.GetExecutionSummaryStatusActionPath);
            if (string.IsNullOrEmpty(GetExecutionSummaryStatusActionPath))
            {
                GetExecutionSummaryStatusActionPath = PackageApiActions.GetExecutionSummaryStatusActionPath;
            }

            GetExportedPackageUrlActionPath = GetOptionalString(dataMap, SettingsConstants.GetExportedPackageUrlActionPath);
            if (string.IsNullOrEmpty(GetExportedPackageUrlActionPath))
            {
                GetExportedPackageUrlActionPath = PackageApiActions.GetExportedPackageUrlActionPath;
            }

            GetExecutionSummaryPageUrlActionPath = GetOptionalString(dataMap, SettingsConstants.GetExecutionSummaryPageUrlActionPath);
            if (string.IsNullOrEmpty(GetExecutionSummaryPageUrlActionPath))
            {
                GetExecutionSummaryPageUrlActionPath = PackageApiActions.GetExecutionSummaryPageUrlActionPath;
            }

            DeleteExecutionHistoryJobActionPath = GetOptionalString(dataMap, SettingsConstants.DeleteExecutionHistoryJobActionPath);
            if (string.IsNullOrEmpty(DeleteExecutionHistoryJobActionPath))
            {
                DeleteExecutionHistoryJobActionPath = PackageApiActions.DeleteExecutionHistoryJobActionPath;
            }

            ExportToPackageActionPath = GetOptionalString(dataMap, SettingsConstants.ExportToPackageActionPath);
            if (string.IsNullOrEmpty(ExportToPackageActionPath))
            {
                ExportToPackageActionPath = PackageApiActions.ExportToPackageActionPath;
            }

            ExportFromPackageActionPath = GetOptionalString(dataMap, SettingsConstants.ExportFromPackageActionPath);
            if (string.IsNullOrEmpty(ExportFromPackageActionPath))
            {
                ExportFromPackageActionPath = PackageApiActions.ExportFromPackageActionPath;
            }

            GetMessageStatusActionPath = GetOptionalString(dataMap, SettingsConstants.GetMessageStatusActionPath);
            if (string.IsNullOrEmpty(GetMessageStatusActionPath))
            {
                GetMessageStatusActionPath = PackageApiActions.GetMessageStatusActionPath;
            }

            GetImportTargetErrorKeysFileUrlPath = GetOptionalString(dataMap, SettingsConstants.GetImportTargetErrorKeysFileUrlPath);
            if (string.IsNullOrEmpty(GetImportTargetErrorKeysFileUrlPath))
            {
                GetImportTargetErrorKeysFileUrlPath = PackageApiActions.GetImportTargetErrorKeysFileUrlPath;
            }

            GenerateImportTargetErrorKeysFilePath = GetOptionalString(dataMap, SettingsConstants.GenerateImportTargetErrorKeysFilePath);
            if (string.IsNullOrEmpty(GenerateImportTargetErrorKeysFilePath))
            {
                GenerateImportTargetErrorKeysFilePath = PackageApiActions.GenerateImportTargetErrorKeysFilePath;
            }

            GetExecutionErrorsPath = GetOptionalString(dataMap, SettingsConstants.GetExecutionErrorsPath);
            if (string.IsNullOrEmpty(GetExecutionErrorsPath))
            {
                GetExecutionErrorsPath = PackageApiActions.GetExecutionErrorsPath;
            }

            GetExecutionErrors = GetOptionalBoolean(dataMap, SettingsConstants.GetExecutionErrors);

            LogVerbose = GetOptionalBoolean(dataMap, SettingsConstants.LogVerbose);

            JobKey = context.JobDetail.Key.ToString();
        }

        #region Members

        /// <summary>
        /// Gets or sets the aos URI.
        /// </summary>
        /// <value>
        /// The aos URI.
        /// </value>
        public string AosUri { get; set; }

        /// <summary>
        /// Gets or sets the azure authentication endpoint.
        /// </summary>
        /// <value>
        /// The azure authentication endpoint.
        /// </value>
        public string AzureAuthEndpoint { get; set; }

        /// <summary>
        /// Gets or sets the aad tenant.
        /// </summary>
        /// <value>
        /// The aad tenant.
        /// </value>
        public string AadTenant { get; set; }

        /// <summary>
        /// <summary>
        /// Legacy flag for ADAL authentication (retained for backward compatibility, no longer used).
        /// </summary>
        public bool UseADAL { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether [use service authentication].
        /// </summary>
        /// <value>
        /// <c>true</c> if [use service authentication]; otherwise, <c>false</c>.
        /// </value>
        public bool UseServiceAuthentication { get; set; }

        /// <summary>
        /// Gets or sets the aad client identifier.
        /// </summary>
        /// <value>
        /// The aad client identifier.
        /// </value>
        public Guid AadClientId { get; set; }

        /// <summary>
        /// Gets or sets the aad client secret.
        /// </summary>
        /// <value>
        /// The aad client secret.
        /// </value>
        public string AadClientSecret { get; set; }

        /// <summary>
        /// Gets or sets the name of the user.
        /// </summary>
        /// <value>
        /// The name of the user.
        /// </value>
        public string UserName { get; set; }

        /// <summary>
        /// Gets or sets the user password.
        /// </summary>
        /// <value>
        /// The user password.
        /// </value>
        public SecureString UserPassword { get; private set; }

        /// <summary>
        /// Gets or sets execution interval.
        /// </summary>
        /// <value>
        /// The execution interval.
        /// </value>
        public int DelayBetweenFiles { get; private set; }

        /// <summary>
        /// Gets or sets retry count.
        /// </summary>
        /// <value>
        /// Retry count.
        /// </value>
        public int RetryCount { get; set; }

        /// <summary>
        /// Gets or sets delay between retries.
        /// </summary>
        /// <value>
        /// Delay between retries in seconds.
        /// </value>
        public int RetryDelay { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether [pause job when exception occurs].
        /// </summary>
        /// <value>
        /// <c>true</c> if [pause job when exception occurs]; otherwise, <c>false</c>.
        /// </value>
        public bool PauseJobOnException { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether the job is paused indefinitely or not.
        /// </summary>
        /// <value>
        /// <c>true</c> if [pause job indefinitely]; otherwise, <c>false</c>.
        /// </value>
        public bool IndefinitePause { get; private set; }

        /// <summary>
        /// Get the ImportFromPackage Odata action relative path
        /// </summary>
        /// <value>
        /// The relative path to the ImportFromPackage Odata action
        /// </value>
        public string ImportFromPackageActionPath { get; private set; } = PackageApiActions.ImportFromPackageActionPath;

        /// <summary>
        /// Get the GetAzureWriteUrl Odata action relative path
        /// </summary>
        /// <value>
        /// The relative path to the GetAzureWriteUrl Odata action
        /// </value>
        public string GetAzureWriteUrlActionPath { get; private set; } = PackageApiActions.GetAzureWriteUrlActionPath;

        /// <summary>
        /// Get the GetExecutionSummaryStatus Odata action relative path
        /// </summary>
        /// <value>
        /// The relative path to the GetExecutionSummaryStatus Odata action
        /// </value>
        public string GetExecutionSummaryStatusActionPath { get; private set; } = PackageApiActions.GetExecutionSummaryStatusActionPath;

        /// <summary>
        /// Get the GetExportedPackageUrl Odata action relative path
        /// </summary>
        /// <value>
        /// The relative path to the GetExportedPackageUrl Odata action
        /// </value>
        public string GetExportedPackageUrlActionPath { get; private set; } = PackageApiActions.GetExportedPackageUrlActionPath;

        /// <summary>
        /// Get the GetExecutionSummaryPageUrl Odata action relative path
        /// </summary>
        /// <value>
        /// The relative path to the GetExecutionSummaryPageUrl Odata action
        /// </value>
        public string GetExecutionSummaryPageUrlActionPath { get; private set; } = PackageApiActions.GetExecutionSummaryPageUrlActionPath;

        /// <summary>
        /// Get the DeleteExecutionHistoryJob Odata action relative path
        /// </summary>
        /// <value>
        /// The relative path to the DeleteExecutionHistoryJob Odata action
        /// </value>
        public string DeleteExecutionHistoryJobActionPath { get; private set; } = PackageApiActions.DeleteExecutionHistoryJobActionPath;

        /// <summary>
        /// Get the ExportToPackage Odata action relative path
        /// </summary>
        /// <value>
        /// The relative path to the ExportToPackage Odata action
        /// </value>
        public string ExportToPackageActionPath { get; private set; } = PackageApiActions.ExportToPackageActionPath;

        /// <summary>
        /// Get the ExportFromPackage Odata action relative path
        /// </summary>
        /// <value>
        /// The relative path to the ExportFromPackage Odata action
        /// </value>
        public string ExportFromPackageActionPath { get; private set; } = PackageApiActions.ExportFromPackageActionPath;

        /// <summary>
        /// Get the GetMessageStatus Odata action relative path
        /// </summary>
        /// <value>
        /// The relative path to the GetMessageStatus Odata action
        /// </value>
        public string GetMessageStatusActionPath { get; private set; } = PackageApiActions.GetMessageStatusActionPath;

        /// <summary>
        /// Get the GetImportTargetErrorKeysFileUrl Odata action relative path
        /// </summary>
        /// <value>
        /// The relative path to the GetImportTargetErrorKeysFileUrl Odata action
        /// </value>
        public string GetImportTargetErrorKeysFileUrlPath { get; private set; } = PackageApiActions.GetImportTargetErrorKeysFileUrlPath;

        /// <summary>
        /// Get the GenerateImportTargetErrorKeysFile Odata action relative path
        /// </summary>
        /// <value>
        /// The relative path to the GenerateImportTargetErrorKeysFile Odata action
        /// </value>
        public string GenerateImportTargetErrorKeysFilePath { get; private set; } = PackageApiActions.GenerateImportTargetErrorKeysFilePath;

        /// <summary>
        /// Get the GetExecutionErrors Odata action relative path
        /// </summary>
        /// <value>
        /// The relative path to the GetExecutionErrors Odata action
        /// </value>
        public string GetExecutionErrorsPath { get; private set; } = PackageApiActions.GetExecutionErrorsPath;

        /// <summary>
        /// Gets or sets a value indicating whether to retrieve execution errors.
        /// </summary>
        /// <value>
        /// <c>true</c> if [get executon errors]; otherwise, <c>false</c>.
        /// </value>
        public bool GetExecutionErrors { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether to log verbose.
        /// </summary>
        /// <value>
        /// <c>true</c> if [Log verbose]; otherwise, <c>false</c>.
        /// </value>
        public bool LogVerbose { get; private set; }

        /// <summary>
        /// Get the Job key
        /// </summary>
        /// <value>
        /// Job key identifier
        /// </value>
        public string JobKey { get; private set; }

        #endregion

        protected static string DecryptSecret(string encrypted)
        {
            return string.IsNullOrWhiteSpace(encrypted) ? null : EncryptDecrypt.Decrypt(encrypted);
        }

        protected static string GetOptionalString(JobDataMap map, string key)
        {
            return map.ContainsKey(key) ? map.GetString(key) : null;
        }

        protected static bool GetOptionalBoolean(JobDataMap map, string key, bool defaultValue = false)
        {
            return map.ContainsKey(key) ? map.GetBooleanValue(key) : defaultValue;
        }

        protected static int GetOptionalInt(JobDataMap map, string key, int defaultValue = 0)
        {
            return map.ContainsKey(key) ? map.GetInt(key) : defaultValue;
        }

        protected static int GetOptionalPositiveInt(JobDataMap map, string key, int defaultValue)
        {
            var value = GetOptionalInt(map, key, defaultValue);
            return value > 0 ? value : defaultValue;
        }

        protected SftpConfiguration ReadSftpConfiguration(
            JobDataMap dataMap,
            string hostKey,
            string portKey,
            string usernameKey,
            string passwordKey,
            string useKeyKey,
            string keyPathKey,
            string keyPassphraseKey,
            string remoteFolderKey,
            string fileMaskKey)
        {
            var host = GetOptionalString(dataMap, hostKey);
            if (string.IsNullOrWhiteSpace(host))
            {
                return null;
            }

            var port = GetOptionalInt(dataMap, portKey);
            var username = GetOptionalString(dataMap, usernameKey);
            var password = DecryptSecret(GetOptionalString(dataMap, passwordKey));
            var useKey = GetOptionalBoolean(dataMap, useKeyKey);
            var keyPath = GetOptionalString(dataMap, keyPathKey);
            var keyPassphrase = DecryptSecret(GetOptionalString(dataMap, keyPassphraseKey));
            var remoteFolder = GetOptionalString(dataMap, remoteFolderKey);
            var fileMask = GetOptionalString(dataMap, fileMaskKey);

            return new SftpConfiguration(
                host,
                port,
                username,
                password,
                useKey,
                keyPath,
                keyPassphrase,
                remoteFolder,
                fileMask);
        }

        /// <summary>
        /// Assigns a decrypted password to the SecureString holder.
        /// </summary>
        /// <param name="plainText">The decrypted password.</param>
        /// <exception cref="JobExecutionException">Thrown when password is empty.</exception>
        public void SetUserPassword(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                throw new JobExecutionException(string.Format(CultureInfo.InvariantCulture, Resources.User_password_is_missing_in_job_configuration));
            }

            UserPassword?.Dispose();
            UserPassword = ConvertToSecureString(plainText);
        }

        private static SecureString ConvertToSecureString(string plainText)
        {
            if (plainText == null)
            {
                return null;
            }

            var secureString = new SecureString();
            var buffer = plainText.ToCharArray();
            foreach (var c in buffer)
            {
                secureString.AppendChar(c);
            }

            secureString.MakeReadOnly();
            Array.Clear(buffer, 0, buffer.Length);
            return secureString;
        }
    }
}
