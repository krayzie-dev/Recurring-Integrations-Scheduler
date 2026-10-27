using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Quartz;
using RecurringIntegrationsScheduler.Common.Contracts;
using RecurringIntegrationsScheduler.Common.Helpers;
using RecurringIntegrationsScheduler.Tests.TestCommon.Fixtures;
using RecurringIntegrationsScheduler.Tests.TestCommon.Mocks;
using RecurringIntegrationsScheduler.Tests.TestCommon.Quartz;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ProcessingMonitorJob = RecurringIntegrationsScheduler.Job.ProcessingMonitor;

namespace RecurringIntegrationsScheduler.JobProcessingMonitor.Tests
{
    [TestClass]
    public class ProcessingMonitorTests
    {
        [TestMethod]
        public async Task Execute_MovesProcessedJobsToSuccess()
        {
            var testDataRoot = FixtureManager.MaterializeTestData("ProcessingMonitorTests");
            var uploadSuccessDir = Path.Combine(testDataRoot, "Processing", "UploadSuccess");
            var processingSuccessDir = Path.Combine(testDataRoot, "Processing", "ProcessingSuccess");
            var processingErrorsDir = Path.Combine(testDataRoot, "Processing", "ProcessingErrors");
            Directory.CreateDirectory(uploadSuccessDir);
            Directory.CreateDirectory(processingSuccessDir);
            Directory.CreateDirectory(processingErrorsDir);

            var packagePath = Path.Combine(uploadSuccessDir, "customers.zip");
            File.WriteAllText(packagePath, "payload");
            var dataMessage = new DataMessage
            {
                FullPath = packagePath,
                Name = Path.GetFileName(packagePath),
                MessageId = "MSG-200",
                MessageStatus = MessageStatus.InProcess
            };
            FileOperationsHelper.WriteStatusFile(dataMessage, ".Status");

            var jobStatusDetail = new DataJobStatusDetail
            {
                DataJobStatus = new DataJobStatus
                {
                    DataJobState = DataJobState.Processed
                }
            };

            var httpHelper = new StubHttpClientHelper
            {
                GetJobStatusUriHandler = messageId => new Uri("https://test.operations.dynamics.com/jobstatus"),
                GetRequestAsyncHandler = (uri, addAuth) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(jobStatusDetail, new StringEnumConverter()), Encoding.UTF8, "application/json")
                })
            };
            var httpFactory = new DelegatingHttpClientHelperFactory(_ => httpHelper);

            var job = new ProcessingMonitorJob(httpFactory);
            var context = TestSchedulerBuilder.CreateContext(job, CreateJobDataMap(uploadSuccessDir, processingSuccessDir, processingErrorsDir));

            await job.Execute(context);

            var targetPath = Path.Combine(processingSuccessDir, "customers.zip");
            Assert.IsTrue(File.Exists(targetPath), "Processed package should move to processing success folder.");
            Assert.IsFalse(File.Exists(packagePath), "Original package should be removed from upload success folder.");
            Assert.IsFalse(File.Exists(Path.ChangeExtension(packagePath, ".Status")), "Status file should be deleted after processing.");
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(processingErrorsDir).Any(), "No errors expected for processed job.");
        }

        [TestMethod]
        [DataRow(13, true)]
        [DataRow(1, false)]
        public async Task Execute_InProcessJob_MovedToErrorsOnlyAfterTimeout(int submittedHoursAgo, bool expectTimeout)
        {
            var testDataRoot = FixtureManager.MaterializeTestData("ProcessingMonitorTests");
            var uploadSuccessDir = Path.Combine(testDataRoot, "Processing", "UploadSuccess");
            var processingSuccessDir = Path.Combine(testDataRoot, "Processing", "ProcessingSuccess");
            var processingErrorsDir = Path.Combine(testDataRoot, "Processing", "ProcessingErrors");
            Directory.CreateDirectory(uploadSuccessDir);
            Directory.CreateDirectory(processingSuccessDir);
            Directory.CreateDirectory(processingErrorsDir);

            var clock = new FakePollingClock();
            var packagePath = Path.Combine(uploadSuccessDir, "vendors.zip");
            File.WriteAllText(packagePath, "payload");
            FileOperationsHelper.WriteStatusFile(new DataMessage
            {
                FullPath = packagePath,
                Name = Path.GetFileName(packagePath),
                MessageId = "MSG-400",
                MessageStatus = MessageStatus.Enqueued,
                SubmittedUtc = clock.UtcNow.AddHours(-submittedHoursAgo)
            }, ".Status");

            var jobStatusDetail = new DataJobStatusDetail
            {
                DataJobStatus = new DataJobStatus { DataJobState = DataJobState.InProcess }
            };
            var httpHelper = new StubHttpClientHelper
            {
                GetJobStatusUriHandler = messageId => new Uri("https://test.operations.dynamics.com/jobstatus"),
                GetRequestAsyncHandler = (uri, addAuth) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(jobStatusDetail, new StringEnumConverter()), Encoding.UTF8, "application/json")
                })
            };

            var job = new ProcessingMonitorJob(new DelegatingHttpClientHelperFactory(_ => httpHelper), clock);
            var context = TestSchedulerBuilder.CreateContext(job, CreateJobDataMap(uploadSuccessDir, processingSuccessDir, processingErrorsDir));

            await job.Execute(context);

            var errorPath = Path.Combine(processingErrorsDir, "vendors.zip");
            Assert.AreEqual(expectTimeout, File.Exists(errorPath), "Package should be moved to errors only after 12 hours.");
            Assert.AreEqual(!expectTimeout, File.Exists(packagePath));
        }

        private static JobDataMap CreateJobDataMap(string uploadSuccessDir, string processingSuccessDir, string processingErrorsDir)
        {
            return new JobDataMap
            {
                { SettingsConstants.AosUri, "https://test.operations.dynamics.com" },
                { SettingsConstants.AzureAuthEndpoint, "https://login.microsoftonline.com" },
                { SettingsConstants.AadTenant, "contoso.onmicrosoft.com" },
                { SettingsConstants.UseServiceAuthentication, true },
                { SettingsConstants.AadClientId, Guid.NewGuid().ToString() },
                { SettingsConstants.AadClientSecret, EncryptDecrypt.Encrypt("secret") },
                { SettingsConstants.ActivityId, Guid.NewGuid().ToString() },
                { SettingsConstants.UploadSuccessDir, uploadSuccessDir },
                { SettingsConstants.ProcessingSuccessDir, processingSuccessDir },
                { SettingsConstants.ProcessingErrorsDir, processingErrorsDir },
                { SettingsConstants.StatusFileExtension, ".Status" },
                { SettingsConstants.DelayBetweenStatusCheck, 0 },
                { SettingsConstants.GetExecutionErrors, false },
                { SettingsConstants.RetryCount, 1 },
                { SettingsConstants.RetryDelay, 1 },
                { SettingsConstants.PauseJobOnException, false }
            };
        }
    }
}
