#pragma warning disable MSTEST0037
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Quartz;
using Quartz.Impl;
using RecurringIntegrationsScheduler.Common.Contracts;
using RecurringIntegrationsScheduler.Common.Helpers;
using RecurringIntegrationsScheduler.Tests.TestCommon.Fixtures;
using RecurringIntegrationsScheduler.Tests.TestCommon.Mocks;
using RecurringIntegrationsScheduler.Tests.TestCommon.Quartz;
using RecurringIntegrationsScheduler.Tests.TestCommon.Sftp;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ExportJobType = RecurringIntegrationsScheduler.Job.Export;

namespace RecurringIntegrationsScheduler.JobExport.Tests
{
    [TestClass]
    public class ExportJobTests
    {
        [TestMethod]
        public async Task Execute_ExportsPackageAndUploadsToSftp()
        {
            var testDataRoot = FixtureManager.MaterializeTestData("ExportJobTests");
            var downloadDir = Path.Combine(testDataRoot, "FileSystem", "Export", "Staging");
            var errorDir = Path.Combine(testDataRoot, "FileSystem", "Export", "Delivered");

            var blobPayload = Encoding.UTF8.GetBytes("export payload");
            var exportToPackageResponse = new HttpResponseMessage(HttpStatusCode.OK);

            var statusResponses = new[] { "Executing", "Succeeded" };
            var statusIndex = 0;

            static HttpResponseMessage JsonValue(string value)
            {
                var payload = $"{{\"value\":\"{value}\"}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            }

            var httpHelper = new StubHttpClientHelper
            {
                ExportToPackageHandler = (definition, packageName, execId, company, re) =>
                {
                    Assert.AreEqual(packageName, execId, "Export job should use the generated execution id as package name.");
                    return Task.FromResult(exportToPackageResponse);
                },
                GetExecutionSummaryStatusHandler = id =>
                {
                    var status = statusResponses[Math.Min(statusIndex++, statusResponses.Length - 1)];
                    return Task.FromResult(JsonValue(status));
                },
                GetExportedPackageUrlHandler = id =>
                    Task.FromResult(JsonValue("https://blob.contoso.com/export/package.zip")),
                GetRequestAsyncHandler = (uri, addAuth) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(blobPayload)
                })
            };

            var httpFactory = new DelegatingHttpClientHelperFactory(_ => httpHelper);

            var remoteOutboundFolder = SftpTestEnvironment.PrepareRemoteFolder("ExportJobOutbound");

            var job = new ExportJobType(httpFactory, SftpTransferService.Instance);
            var context = TestSchedulerBuilder.CreateContext(job, CreateJobDataMap(downloadDir, errorDir, remoteOutboundFolder));

            await job.Execute(context);

            var uploadedDir = Path.Combine(downloadDir, "Uploaded");
            Assert.IsTrue(Directory.Exists(uploadedDir), "Uploaded folder should be created after SFTP push.");
            Assert.AreEqual(1, Directory.GetFiles(uploadedDir).Length, "Package should exist in the Uploaded folder.");
            Assert.AreEqual(1, SftpTestEnvironment.ListFiles(remoteOutboundFolder).Count, "SFTP outbound folder should contain the uploaded package.");
        }

        [TestMethod]
        public async Task Execute_ExportSucceedsAfterLongWait_DownloadsPackageWithGrowingDelays()
        {
            var (downloadDir, errorDir) = PrepareFolders();
            var clock = new FakePollingClock();
            var started = clock.UtcNow;

            var httpHelper = CreateHttpHelper(
                statusResponse: () => JsonValueResponse(clock.UtcNow - started < TimeSpan.FromHours(2) ? "NotRun" : "Succeeded"),
                packageUrlResponse: () => JsonValueResponse(PackageUrl));

            var job = new ExportJobType(new DelegatingHttpClientHelperFactory(_ => httpHelper), new StubSftpTransferService(), clock);
            var context = TestSchedulerBuilder.CreateContext(job, CreateJobDataMap(downloadDir, errorDir, null));

            await job.Execute(context);

            Assert.AreEqual(1, Directory.GetFiles(downloadDir, "*.zip").Length, "Export finished after 2 hours must be downloaded.");
            Assert.AreEqual(TimeSpan.FromSeconds(5), clock.Delays.First(), "First delay should default to 5 seconds.");
            for (var i = 1; i < clock.Delays.Count; i++)
            {
                Assert.IsTrue(clock.Delays[i] >= clock.Delays[i - 1], "Delays must not decrease.");
            }
            Assert.AreEqual(TimeSpan.FromSeconds(300), clock.Delays.Max(), "Delay should grow up to 5 minutes.");
        }

        [TestMethod]
        public async Task Execute_StillNotRunAfter12Hours_ThrowsWithExecutionId()
        {
            var (downloadDir, errorDir) = PrepareFolders();
            var clock = new FakePollingClock();
            var started = clock.UtcNow;
            string executionId = null;

            var httpHelper = CreateHttpHelper(
                statusResponse: () => JsonValueResponse("NotRun"),
                packageUrlResponse: () => throw new AssertFailedException("Package URL must not be requested for unfinished export."),
                onExport: id => executionId = id);

            var job = new ExportJobType(new DelegatingHttpClientHelperFactory(_ => httpHelper), new StubSftpTransferService(), clock);
            var context = TestSchedulerBuilder.CreateContext(job, CreateJobDataMap(downloadDir, errorDir, null));

            var exception = await Assert.ThrowsExactlyAsync<JobExecutionException>(() => job.Execute(context));

            var message = exception.InnerException?.Message ?? string.Empty;
            StringAssert.Contains(message, "did not finish within 720 minutes");
            StringAssert.Contains(message, "Status: NotRun");
            StringAssert.Contains(message, executionId);
            Assert.AreEqual(TimeSpan.FromHours(12), clock.UtcNow - started, "Last check should happen exactly at the 12 hour deadline.");
            Assert.AreEqual(0, Directory.GetFiles(downloadDir, "*.zip").Length);
        }

        [TestMethod]
        public async Task Execute_SucceededAtDeadline_DownloadsPackage()
        {
            var (downloadDir, errorDir) = PrepareFolders();
            var clock = new FakePollingClock();
            var deadline = clock.UtcNow.AddHours(12);

            var httpHelper = CreateHttpHelper(
                statusResponse: () => JsonValueResponse(clock.UtcNow < deadline ? "Executing" : "Succeeded"),
                packageUrlResponse: () => JsonValueResponse(PackageUrl));

            var job = new ExportJobType(new DelegatingHttpClientHelperFactory(_ => httpHelper), new StubSftpTransferService(), clock);
            var context = TestSchedulerBuilder.CreateContext(job, CreateJobDataMap(downloadDir, errorDir, null));

            await job.Execute(context);

            Assert.AreEqual(1, Directory.GetFiles(downloadDir, "*.zip").Length, "Final status returned at the deadline must win over the timeout.");
        }

        [TestMethod]
        public async Task Execute_CustomTimeout_IsRespected()
        {
            var (downloadDir, errorDir) = PrepareFolders();
            var clock = new FakePollingClock();
            var started = clock.UtcNow;

            var httpHelper = CreateHttpHelper(
                statusResponse: () => JsonValueResponse("Executing"),
                packageUrlResponse: () => JsonValueResponse(PackageUrl));

            var job = new ExportJobType(new DelegatingHttpClientHelperFactory(_ => httpHelper), new StubSftpTransferService(), clock);
            var dataMap = CreateJobDataMap(downloadDir, errorDir, null);
            dataMap.Add(SettingsConstants.StatusCheckTimeoutMinutes, 30);
            var context = TestSchedulerBuilder.CreateContext(job, dataMap);

            var exception = await Assert.ThrowsExactlyAsync<JobExecutionException>(() => job.Execute(context));

            StringAssert.Contains(exception.InnerException?.Message ?? string.Empty, "did not finish within 30 minutes");
            Assert.AreEqual(TimeSpan.FromMinutes(30), clock.UtcNow - started);
        }

        [TestMethod]
        public async Task Execute_TransientErrorsDuringPolling_AreRetried()
        {
            var (downloadDir, errorDir) = PrepareFolders();
            var clock = new FakePollingClock();
            var statusCalls = 0;

            var httpHelper = CreateHttpHelper(
                statusResponse: () =>
                {
                    switch (++statusCalls)
                    {
                        case 1: return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("maintenance") };
                        case 2: throw new HttpRequestException("An error occurred while sending the request.", new WebException("The remote name could not be resolved"));
                        case 3: return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
                        default: return JsonValueResponse("Succeeded");
                    }
                },
                packageUrlResponse: () => JsonValueResponse(PackageUrl));

            var job = new ExportJobType(new DelegatingHttpClientHelperFactory(_ => httpHelper), new StubSftpTransferService(), clock);
            var context = TestSchedulerBuilder.CreateContext(job, CreateJobDataMap(downloadDir, errorDir, null));

            await job.Execute(context);

            Assert.AreEqual(4, statusCalls);
            Assert.AreEqual(1, Directory.GetFiles(downloadDir, "*.zip").Length, "Temporary D365 or network errors must not abort the export.");
        }

        [TestMethod]
        public async Task Execute_UnauthorizedDuringPolling_FailsImmediately()
        {
            var (downloadDir, errorDir) = PrepareFolders();
            var clock = new FakePollingClock();
            var statusCalls = 0;

            var httpHelper = CreateHttpHelper(
                statusResponse: () => { statusCalls++; return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("token expired") }; },
                packageUrlResponse: () => JsonValueResponse(PackageUrl));

            var job = new ExportJobType(new DelegatingHttpClientHelperFactory(_ => httpHelper), new StubSftpTransferService(), clock);
            var context = TestSchedulerBuilder.CreateContext(job, CreateJobDataMap(downloadDir, errorDir, null));

            var exception = await Assert.ThrowsExactlyAsync<JobExecutionException>(() => job.Execute(context));

            Assert.AreEqual(1, statusCalls);
            StringAssert.Contains(exception.InnerException?.Message ?? string.Empty, "token expired");
        }

        [TestMethod]
        public async Task Execute_PackageUrlEmptyForAWhile_DownloadsPackage()
        {
            var (downloadDir, errorDir) = PrepareFolders();
            var clock = new FakePollingClock();
            var urlCalls = 0;

            var httpHelper = CreateHttpHelper(
                statusResponse: () => JsonValueResponse("Succeeded"),
                packageUrlResponse: () => JsonValueResponse(++urlCalls < 6 ? string.Empty : PackageUrl));

            var job = new ExportJobType(new DelegatingHttpClientHelperFactory(_ => httpHelper), new StubSftpTransferService(), clock);
            var context = TestSchedulerBuilder.CreateContext(job, CreateJobDataMap(downloadDir, errorDir, null));

            await job.Execute(context);

            Assert.AreEqual(6, urlCalls);
            Assert.AreEqual(1, Directory.GetFiles(downloadDir, "*.zip").Length);
        }

        [TestMethod]
        public async Task Execute_PackageUrlNeverAvailable_ThrowsAfterDeadline()
        {
            var (downloadDir, errorDir) = PrepareFolders();
            var clock = new FakePollingClock();
            string executionId = null;

            var httpHelper = CreateHttpHelper(
                statusResponse: () => JsonValueResponse("Succeeded"),
                packageUrlResponse: () => JsonValueResponse(string.Empty),
                onExport: id => executionId = id);

            var job = new ExportJobType(new DelegatingHttpClientHelperFactory(_ => httpHelper), new StubSftpTransferService(), clock);
            var context = TestSchedulerBuilder.CreateContext(job, CreateJobDataMap(downloadDir, errorDir, null));

            var exception = await Assert.ThrowsExactlyAsync<JobExecutionException>(() => job.Execute(context));

            var message = exception.InnerException?.Message ?? string.Empty;
            StringAssert.Contains(message, "package URL was not available within 720 minutes");
            StringAssert.Contains(message, executionId);
        }

        [TestMethod]
        public async Task Execute_InterruptedWhileWaiting_EndsWithoutError()
        {
            var (downloadDir, errorDir) = PrepareFolders();
            var clock = new FakePollingClock();

            var httpHelper = CreateHttpHelper(
                statusResponse: () => JsonValueResponse("NotRun"),
                packageUrlResponse: () => JsonValueResponse(PackageUrl));

            var job = new ExportJobType(new DelegatingHttpClientHelperFactory(_ => httpHelper), new StubSftpTransferService(), clock);
            var dataMap = CreateJobDataMap(downloadDir, errorDir, null);
            dataMap[SettingsConstants.PauseJobOnException] = true;
            var context = (JobExecutionContextImpl)TestSchedulerBuilder.CreateContext(job, dataMap);
            clock.OnDelay = _ => { if (clock.Delays.Count == 3) context.Cancel(); };

            await job.Execute(context);

            Assert.AreEqual(3, clock.Delays.Count, "Job should stop waiting right after interruption.");
            Assert.AreEqual(0, Directory.GetFiles(downloadDir, "*.zip").Length);
        }

        private const string PackageUrl = "https://blob.contoso.com/export/package.zip";

        private static (string downloadDir, string errorDir) PrepareFolders()
        {
            var testDataRoot = FixtureManager.MaterializeTestData("ExportJobTests");
            return (Path.Combine(testDataRoot, "FileSystem", "Export", "Staging"),
                    Path.Combine(testDataRoot, "FileSystem", "Export", "Delivered"));
        }

        private static HttpResponseMessage JsonValueResponse(string value)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"value\":\"{value}\"}}", Encoding.UTF8, "application/json")
            };
        }

        private static StubHttpClientHelper CreateHttpHelper(Func<HttpResponseMessage> statusResponse, Func<HttpResponseMessage> packageUrlResponse, Action<string> onExport = null)
        {
            return new StubHttpClientHelper
            {
                ExportToPackageHandler = (definition, packageName, execId, company, re) =>
                {
                    onExport?.Invoke(execId);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                },
                GetExecutionSummaryStatusHandler = id => Task.FromResult(statusResponse()),
                GetExportedPackageUrlHandler = id => Task.FromResult(packageUrlResponse()),
                GetRequestAsyncHandler = (uri, addAuth) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("export payload"))
                })
            };
        }

        private static JobDataMap CreateJobDataMap(string downloadDir, string errorDir, string remoteOutboundFolder)
        {
            return new JobDataMap
            {
                { SettingsConstants.AosUri, "https://test.operations.dynamics.com" },
                { SettingsConstants.AzureAuthEndpoint, "https://login.microsoftonline.com" },
                { SettingsConstants.AadTenant, "contoso.onmicrosoft.com" },
                { SettingsConstants.UseServiceAuthentication, true },
                { SettingsConstants.AadClientId, Guid.NewGuid().ToString() },
                { SettingsConstants.AadClientSecret, EncryptDecrypt.Encrypt("secret") },
                { SettingsConstants.DownloadSuccessDir, downloadDir },
                { SettingsConstants.DownloadErrorsDir, errorDir },
                { SettingsConstants.DataProject, "CustExport" },
                { SettingsConstants.Company, "USMF" },
                { SettingsConstants.DelayBetweenStatusCheck, 0 },
                { SettingsConstants.DelayBetweenFiles, 0 },
                { SettingsConstants.RetryCount, 1 },
                { SettingsConstants.RetryDelay, 1 },
                { SettingsConstants.UseSftpOutbound, remoteOutboundFolder != null },
                { SettingsConstants.SftpOutboundHost, SftpTestEnvironment.Host },
                { SettingsConstants.SftpOutboundPort, SftpTestEnvironment.Port },
                { SettingsConstants.SftpOutboundUsername, SftpTestEnvironment.Username },
                { SettingsConstants.SftpOutboundPassword, EncryptDecrypt.Encrypt(SftpTestEnvironment.Password) },
                { SettingsConstants.SftpOutboundRemoteFolder, remoteOutboundFolder },
                { SettingsConstants.SftpOutboundFileMask, "*.zip" },
                { SettingsConstants.AddTimestamp, false },
                { SettingsConstants.DeletePackage, false },
                { SettingsConstants.PauseJobOnException, false }
            };
        }
    }
}
#pragma warning restore MSTEST0037
