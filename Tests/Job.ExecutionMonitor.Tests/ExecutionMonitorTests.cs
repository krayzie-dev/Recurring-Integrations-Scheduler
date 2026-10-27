using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
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
using ExecutionMonitorJob = RecurringIntegrationsScheduler.Job.ExecutionMonitor;

namespace RecurringIntegrationsScheduler.JobExecutionMonitor.Tests
{
    [TestClass]
    public class ExecutionMonitorTests
    {
        [TestMethod]
        public async Task Execute_MovesSucceededJobsToProcessingSuccess()
        {
            var testDataRoot = FixtureManager.MaterializeTestData("ExecutionMonitorTests");
            var uploadSuccessDir = Path.Combine(testDataRoot, "Execution", "UploadSuccess");
            var processingSuccessDir = Path.Combine(testDataRoot, "Execution", "ProcessingSuccess");
            var processingErrorsDir = Path.Combine(testDataRoot, "Execution", "ProcessingErrors");
            Directory.CreateDirectory(uploadSuccessDir);
            Directory.CreateDirectory(processingSuccessDir);
            Directory.CreateDirectory(processingErrorsDir);

            var packagePath = Path.Combine(uploadSuccessDir, "customers.zip");
            File.WriteAllText(packagePath, "payload");
            var dataMessage = new DataMessage
            {
                FullPath = packagePath,
                Name = Path.GetFileName(packagePath),
                MessageId = "MSG-100",
                MessageStatus = MessageStatus.InProcess
            };
            FileOperationsHelper.WriteStatusFile(dataMessage, ".Status");

            var httpHelper = new StubHttpClientHelper
            {
                GetExecutionSummaryStatusHandler = id => Task.FromResult(JsonResponse("Succeeded")),
                GetExecutionSummaryPageUrlHandler = id => Task.FromResult(JsonResponse("https://contoso.test/execution"))
            };
            var httpFactory = new DelegatingHttpClientHelperFactory(_ => httpHelper);

            var job = new ExecutionMonitorJob(httpFactory);
            var context = TestSchedulerBuilder.CreateContext(job, CreateJobDataMap(uploadSuccessDir, processingSuccessDir, processingErrorsDir));

            await job.Execute(context);

            var targetPath = Path.Combine(processingSuccessDir, "customers.zip");
            Assert.IsTrue(File.Exists(targetPath), "Package should be moved to processing success directory.");
            Assert.IsFalse(File.Exists(packagePath), "Original package should be removed from upload success directory.");
            Assert.IsFalse(File.Exists(Path.ChangeExtension(packagePath, ".Status")), "Status file should be deleted after processing.");
            Assert.IsTrue(File.Exists(Path.ChangeExtension(targetPath, ".url")), "Link file should be created alongside the processed package.");
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(processingErrorsDir).Any(), "No error files should be produced for succeeded job.");
        }

        [TestMethod]
        [DataRow(13, true)]
        [DataRow(1, false)]
        public async Task Execute_ExecutingImport_MovedToErrorsOnlyAfterTimeout(int submittedHoursAgo, bool expectTimeout)
        {
            var testDataRoot = FixtureManager.MaterializeTestData("ExecutionMonitorTests");
            var uploadSuccessDir = Path.Combine(testDataRoot, "Execution", "UploadSuccess");
            var processingSuccessDir = Path.Combine(testDataRoot, "Execution", "ProcessingSuccess");
            var processingErrorsDir = Path.Combine(testDataRoot, "Execution", "ProcessingErrors");
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
                MessageId = "MSG-300",
                MessageStatus = MessageStatus.Enqueued,
                SubmittedUtc = clock.UtcNow.AddHours(-submittedHoursAgo)
            }, ".Status");

            var httpHelper = new StubHttpClientHelper
            {
                GetExecutionSummaryStatusHandler = id => Task.FromResult(JsonResponse("Executing")),
                GetExecutionSummaryPageUrlHandler = id => Task.FromResult(JsonResponse("https://contoso.test/execution"))
            };

            var job = new ExecutionMonitorJob(new DelegatingHttpClientHelperFactory(_ => httpHelper), clock);
            var context = TestSchedulerBuilder.CreateContext(job, CreateJobDataMap(uploadSuccessDir, processingSuccessDir, processingErrorsDir));

            await job.Execute(context);

            var errorPath = Path.Combine(processingErrorsDir, "vendors.zip");
            Assert.AreEqual(expectTimeout, File.Exists(errorPath), "Package should be moved to errors only after 12 hours.");
            Assert.AreEqual(!expectTimeout, File.Exists(packagePath));
            Assert.AreEqual(!expectTimeout, File.Exists(Path.ChangeExtension(packagePath, ".Status")), "Status file should stay while waiting and be removed after timeout.");
        }

        private static JobDataMap CreateJobDataMap(string uploadSuccess, string processingSuccess, string processingErrors)
        {
            return new JobDataMap
            {
                { SettingsConstants.AosUri, "https://test.operations.dynamics.com" },
                { SettingsConstants.AzureAuthEndpoint, "https://login.microsoftonline.com" },
                { SettingsConstants.AadTenant, "contoso.onmicrosoft.com" },
                { SettingsConstants.UseServiceAuthentication, true },
                { SettingsConstants.AadClientId, Guid.NewGuid().ToString() },
                { SettingsConstants.AadClientSecret, EncryptDecrypt.Encrypt("secret") },
                { SettingsConstants.UploadSuccessDir, uploadSuccess },
                { SettingsConstants.ProcessingSuccessDir, processingSuccess },
                { SettingsConstants.ProcessingErrorsDir, processingErrors },
                { "StatusFileExtension", ".Status" },
                { SettingsConstants.DelayBetweenStatusCheck, 0 },
                { SettingsConstants.GetImportTargetErrorKeysFile, false },
                { SettingsConstants.GetExecutionErrors, false },
                { SettingsConstants.RetryCount, 1 },
                { SettingsConstants.RetryDelay, 1 },
                { SettingsConstants.PauseJobOnException, false }
            };
        }

        private static HttpResponseMessage JsonResponse(string value)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { value }), Encoding.UTF8, "application/json")
            };
        }
    }
}
