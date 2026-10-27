using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RIS.Core.Errors;
using RIS.Core.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RIS.D365.Tests;

[TestClass]
public sealed class D365ClientTests : IDisposable
{
    private const string ActionPrefix = "/data/DataManagementDefinitionGroups/Microsoft.Dynamics.DataEntities.";
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly ServiceProvider _services;
    private readonly ID365Client _client;

    public D365ClientTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ID365TokenProvider, FakeTokenProvider>();
        services.AddRisD365(o => { o.RetryCount = 2; o.RetryDelay = TimeSpan.FromMilliseconds(1); });
        _services = services.BuildServiceProvider();

        var connection = new D365ConnectionOptions
        {
            Name = "TEST",
            AosUri = new Uri(_server.Url!),
            Tenant = "contoso.onmicrosoft.com",
            ClientId = Guid.NewGuid().ToString(),
            ClientSecret = "secret"
        };
        _client = _services.GetRequiredService<ID365ClientFactory>().Create(connection);
    }

    public void Dispose()
    {
        _server.Stop();
        _services.Dispose();
    }

    [TestMethod]
    public async Task ExportToPackage_SendsSamePayloadAs4x_WithToken()
    {
        _server.Given(Request.Create().WithPath(ActionPrefix + "ExportToPackage").UsingPost())
            .RespondWith(Response.Create().WithBodyAsJson(new { value = "EXEC-1" }));

        var result = await _client.ExportToPackageAsync("CustInvoiceJour", "EXEC-1", "EXEC-1", "USMF", false, CancellationToken.None);

        Assert.AreEqual("EXEC-1", result);
        var request = _server.LogEntries.Single().RequestMessage!;
        Assert.AreEqual("Bearer test-token", request.Headers!["Authorization"].Single());
        using var body = JsonDocument.Parse(request.Body!);
        Assert.AreEqual("CustInvoiceJour", body.RootElement.GetProperty("definitionGroupId").GetString());
        Assert.AreEqual("EXEC-1", body.RootElement.GetProperty("packageName").GetString());
        Assert.AreEqual("USMF", body.RootElement.GetProperty("legalEntityId").GetString());
        Assert.IsFalse(body.RootElement.GetProperty("reExecute").GetBoolean());
    }

    [TestMethod]
    public async Task StatusCheck_TransientError_IsRetried()
    {
        _server.Given(Request.Create().WithPath(ActionPrefix + "GetExecutionSummaryStatus").UsingPost())
            .InScenario("status").WillSetStateTo("second")
            .RespondWith(Response.Create().WithStatusCode(503).WithBody("maintenance"));
        _server.Given(Request.Create().WithPath(ActionPrefix + "GetExecutionSummaryStatus").UsingPost())
            .InScenario("status").WhenStateIs("second")
            .RespondWith(Response.Create().WithBodyAsJson(new { value = "Succeeded" }));

        var status = await _client.GetExecutionSummaryStatusAsync("EXEC-1", CancellationToken.None);

        Assert.AreEqual("Succeeded", status);
        Assert.AreEqual(2, _server.LogEntries.Count());
    }

    [TestMethod]
    public async Task ExportToPackage_TransientError_IsNotRetried_AndReportsBody()
    {
        _server.Given(Request.Create().WithPath(ActionPrefix + "ExportToPackage").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503).WithBody("{\"error\":\"service unavailable\"}")
                .WithHeader("ms-dyn-aid", "activity-123"));

        var ex = await Assert.ThrowsExactlyAsync<D365ApiException>(() =>
            _client.ExportToPackageAsync("P", "E", "E", "USMF", false, CancellationToken.None));

        Assert.AreEqual(1, _server.LogEntries.Count(), "ExportToPackage must not be retried (duplicate export).");
        Assert.IsTrue(ex.IsTransient);
        Assert.AreEqual(ErrorCodes.HttpTransient, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "service unavailable");
        Assert.AreEqual("activity-123", ex.CorrelationHeaders["ms-dyn-aid"]);
    }

    [TestMethod]
    public async Task Unauthorized_IsNotRetried_AndNotTransient()
    {
        _server.Given(Request.Create().WithPath(ActionPrefix + "GetExecutionSummaryStatus").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("token expired"));

        var ex = await Assert.ThrowsExactlyAsync<D365ApiException>(() => _client.GetExecutionSummaryStatusAsync("E", CancellationToken.None));

        Assert.AreEqual(1, _server.LogEntries.Count());
        Assert.IsFalse(ex.IsTransient);
        Assert.AreEqual(ErrorCodes.HttpRequestFailed, ex.ErrorCode);
    }

    [TestMethod]
    public async Task ExportedPackageUrl_EmptyValue_ReturnsEmpty()
    {
        _server.Given(Request.Create().WithPath(ActionPrefix + "GetExportedPackageUrl").UsingPost())
            .RespondWith(Response.Create().WithBody("{\"value\":\"\"}"));

        Assert.AreEqual(string.Empty, await _client.GetExportedPackageUrlAsync("E", CancellationToken.None));
    }

    [TestMethod]
    public async Task Download_DoesNotSendAuthorizationHeader()
    {
        _server.Given(Request.Create().WithPath("/blob/package.zip").UsingGet())
            .RespondWith(Response.Create().WithBody("zip-bytes"));

        await using var stream = await _client.OpenDownloadAsync(new Uri(_server.Url + "/blob/package.zip?sig=secret"), CancellationToken.None);
        using var reader = new StreamReader(stream);

        Assert.AreEqual("zip-bytes", await reader.ReadToEndAsync());
        Assert.IsFalse(_server.LogEntries.Single().RequestMessage!.Headers!.ContainsKey("Authorization"));
    }

    [TestMethod]
    public async Task AzureWriteUrl_ParsesBlobInfo()
    {
        _server.Given(Request.Create().WithPath(ActionPrefix + "GetAzureWriteUrl").UsingPost())
            .RespondWith(Response.Create().WithBodyAsJson(new { value = "{\"BlobId\":\"{abc}\",\"BlobUrl\":\"https://blob.core.windows.net/x?sig=1\"}" }));

        var blob = await _client.GetAzureWriteUrlAsync(CancellationToken.None);

        Assert.AreEqual("{abc}", blob.BlobId);
        Assert.AreEqual("blob.core.windows.net", blob.BlobUrl.Host);
    }

    [TestMethod]
    public async Task Enqueue_UsesQueryAndExternalIdentifier_ReturnsMessageIdWithoutQuotes()
    {
        var activity = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/connector/enqueue/{activity:D}").UsingPost())
            .RespondWith(Response.Create().WithBody("\"MSG-1\""));

        using var content = new MemoryStream(Encoding.UTF8.GetBytes("a;b"));
        var messageId = await _client.EnqueueAsync(activity, content, "USMF", "Customers V3", "customers.csv", CancellationToken.None);

        Assert.AreEqual("MSG-1", messageId);
        var request = _server.LogEntries.Single().RequestMessage!;
        Assert.AreEqual("USMF", request.Query!["company"].Single());
        Assert.AreEqual("Customers V3", request.Query!["entity"].Single());
        Assert.AreEqual("customers.csv", request.Headers!["x-ms-dyn-externalidentifier"].Single());
    }

    [TestMethod]
    public async Task Dequeue_EmptyQueue_ReturnsNull_AndMessageUsesHttps()
    {
        var activity = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/connector/dequeue/{activity:D}").UsingGet())
            .InScenario("dq").WillSetStateTo("empty")
            .RespondWith(Response.Create().WithBodyAsJson(new { CorrelationId = "C1", PopReceipt = "P1", DownloadLocation = "http://blob/x.zip" }));
        _server.Given(Request.Create().WithPath($"/api/connector/dequeue/{activity:D}").UsingGet())
            .InScenario("dq").WhenStateIs("empty")
            .RespondWith(Response.Create().WithStatusCode(204));

        var message = await _client.DequeueAsync(activity, CancellationToken.None);
        var empty = await _client.DequeueAsync(activity, CancellationToken.None);

        Assert.AreEqual("C1", message!.CorrelationId);
        Assert.AreEqual("https", message.DownloadLocation.Scheme);
        Assert.IsNull(empty);
    }

    [TestMethod]
    public async Task JobStatus_ParsesStateIncludingUnknownValues()
    {
        var activity = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/connector/jobstatus/{activity:D}").WithParam("jobId", "MSG-1").UsingGet())
            .RespondWith(Response.Create().WithBody("{\"DataJobStatus\":{\"DataJobState\":\"PreProcessing\"},\"ExecutionLog\":\"log\"}"));

        var status = await _client.GetJobStatusAsync(activity, "\"MSG-1\"", CancellationToken.None);

        Assert.AreEqual(Core.Statuses.DataJobState.PreProcessing, status.DataJobStatus!.DataJobState);
    }

    [TestMethod]
    public void Masking_RemovesSasSignatureAndSecrets()
    {
        var masked = LogMasking.MaskText("https://x.blob.core.windows.net/c/p.zip?sv=2020&sig=abc%2Bdef&se=1 client_secret=xyz");
        StringAssert.Contains(masked, "sig=***");
        StringAssert.Contains(masked, "client_secret=***");
        Assert.IsFalse(masked.Contains("abc", StringComparison.Ordinal));
        Assert.IsFalse(masked.Contains("xyz", StringComparison.Ordinal));
    }

    private sealed class FakeTokenProvider : ID365TokenProvider
    {
        public Task<string> GetAuthorizationHeaderAsync(D365ConnectionOptions connection, CancellationToken cancellationToken) =>
            Task.FromResult("Bearer test-token");
    }
}
