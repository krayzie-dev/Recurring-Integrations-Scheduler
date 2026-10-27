using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RIS.Core.Options;
using RIS.Core.Statuses;

namespace RIS.D365;

/// <summary>Blob write location returned by GetAzureWriteUrl.</summary>
public sealed record BlobWriteUrl(string BlobId, Uri BlobUrl);

/// <summary>Message returned by Recurring Integrations dequeue.</summary>
public sealed record DequeuedMessage(string CorrelationId, string PopReceipt, Uri DownloadLocation);

/// <summary>Recurring Integrations job status (jobstatus API).</summary>
public sealed class DataJobStatusDetail
{
    public DataJobStatusInfo? DataJobStatus { get; set; }
    public string? ExecutionLog { get; set; }
    public JsonElement? ExecutionDetail { get; set; }
}

public sealed class DataJobStatusInfo
{
    public DataJobState DataJobState { get; set; }
    public DateTime? DataJobStartedDateTime { get; set; }
    public DateTime? DataJobCompletedDateTime { get; set; }
}

/// <summary>
/// D365 Data Management Package API (execution based import/export).
/// </summary>
public interface IPackageApiClient
{
    Task<string> ExportToPackageAsync(string definitionGroupId, string packageName, string executionId, string legalEntityId, bool reExecute, CancellationToken cancellationToken);

    /// <summary>Raw status text (see <see cref="DmfExecutionStatusExtensions.Parse"/>).</summary>
    Task<string> GetExecutionSummaryStatusAsync(string executionId, CancellationToken cancellationToken);

    /// <summary>Package URL or empty when not ready yet.</summary>
    Task<string?> GetExportedPackageUrlAsync(string executionId, CancellationToken cancellationToken);

    Task<string?> GetExecutionSummaryPageUrlAsync(string executionId, CancellationToken cancellationToken);

    Task<BlobWriteUrl> GetAzureWriteUrlAsync(CancellationToken cancellationToken);

    Task UploadToBlobAsync(Uri blobUrl, Stream content, CancellationToken cancellationToken);

    Task<string> ImportFromPackageAsync(string packageUrl, string definitionGroupId, string executionId, bool execute, bool overwrite, string legalEntityId, CancellationToken cancellationToken);

    Task<bool> GenerateImportTargetErrorKeysFileAsync(string executionId, string entityName, CancellationToken cancellationToken);

    Task<string?> GetImportTargetErrorKeysFileUrlAsync(string executionId, string entityName, CancellationToken cancellationToken);

    /// <summary>Raw response of GetExecutionErrors (written to a file for the user).</summary>
    Task<string> GetExecutionErrorsAsync(string executionId, CancellationToken cancellationToken);

    /// <summary>Downloads content from a URL returned by D365 (SAS URL, no Authorization header).</summary>
    Task<Stream> OpenDownloadAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>
/// D365 Recurring Integrations API (connector enqueue/dequeue).
/// </summary>
public interface IRecurringIntegrationsClient
{
    /// <summary>Uploads a file and returns D365 message id.</summary>
    Task<string> EnqueueAsync(Guid activityId, Stream content, string? company, string? entityName, string externalIdentifier, CancellationToken cancellationToken);

    /// <summary>Next exported message, or null when the queue is empty.</summary>
    Task<DequeuedMessage?> DequeueAsync(Guid activityId, CancellationToken cancellationToken);

    Task AckAsync(Guid activityId, DequeuedMessage message, CancellationToken cancellationToken);

    Task<DataJobStatusDetail> GetJobStatusAsync(Guid activityId, string messageId, CancellationToken cancellationToken);

    Task<Stream> OpenDownloadAsync(Uri url, CancellationToken cancellationToken);
}

public interface ID365Client : IPackageApiClient, IRecurringIntegrationsClient
{
    new Task<Stream> OpenDownloadAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>
/// Creates clients bound to a D365 connection.
/// </summary>
public interface ID365ClientFactory
{
    ID365Client Create(D365ConnectionOptions connection, D365ActionPaths? actionPaths = null);
}

public sealed class D365ClientFactory(IHttpClientFactory httpClientFactory) : ID365ClientFactory
{
    public const string HttpClientName = "RIS.D365";

    public ID365Client Create(D365ConnectionOptions connection, D365ActionPaths? actionPaths = null) =>
        new D365Client(httpClientFactory.CreateClient(HttpClientName), connection, actionPaths ?? new D365ActionPaths());
}

internal sealed class D365Client(HttpClient http, D365ConnectionOptions connection, D365ActionPaths paths) : ID365Client
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<string> ExportToPackageAsync(string definitionGroupId, string packageName, string executionId, string legalEntityId, bool reExecute, CancellationToken cancellationToken) =>
        await PostForValueAsync("ExportToPackage", paths.ExportToPackage,
            new { definitionGroupId, packageName, executionId, reExecute, legalEntityId }, noRetry: true, cancellationToken) ?? executionId;

    public async Task<string> GetExecutionSummaryStatusAsync(string executionId, CancellationToken cancellationToken) =>
        await PostForValueAsync("GetExecutionSummaryStatus", paths.GetExecutionSummaryStatus, new { executionId }, noRetry: false, cancellationToken) ?? string.Empty;

    public Task<string?> GetExportedPackageUrlAsync(string executionId, CancellationToken cancellationToken) =>
        PostForValueAsync("GetExportedPackageUrl", paths.GetExportedPackageUrl, new { executionId }, noRetry: false, cancellationToken);

    public Task<string?> GetExecutionSummaryPageUrlAsync(string executionId, CancellationToken cancellationToken) =>
        PostForValueAsync("GetExecutionSummaryPageUrl", paths.GetExecutionSummaryPageUrl, new { executionId }, noRetry: false, cancellationToken);

    public async Task<BlobWriteUrl> GetAzureWriteUrlAsync(CancellationToken cancellationToken)
    {
        var value = await PostForValueAsync("GetAzureWriteUrl", paths.GetAzureWriteUrl, new { uniqueFileName = Guid.NewGuid().ToString() }, noRetry: false, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(value ?? "{}");
            var blobUrl = document.RootElement.GetProperty("BlobUrl").GetString();
            var blobId = document.RootElement.TryGetProperty("BlobId", out var id) ? id.GetString() : null;
            return new BlobWriteUrl(blobId ?? string.Empty, new Uri(blobUrl!));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or UriFormatException or ArgumentNullException)
        {
            throw Unexpected("GetAzureWriteUrl", $"Unexpected GetAzureWriteUrl value: {LogMasking.Truncate(value)}", ex);
        }
    }

    public async Task UploadToBlobAsync(Uri blobUrl, Stream content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, blobUrl) { Content = new StreamContent(content) };
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        // Stream content can be sent only once.
        using var response = await SendAsync("UploadToBlob", request, authorize: false, noRetry: true, cancellationToken);
    }

    public async Task<string> ImportFromPackageAsync(string packageUrl, string definitionGroupId, string executionId, bool execute, bool overwrite, string legalEntityId, CancellationToken cancellationToken) =>
        await PostForValueAsync("ImportFromPackage", paths.ImportFromPackage,
            new { packageUrl, definitionGroupId, executionId, execute, overwrite, legalEntityId }, noRetry: true, cancellationToken) ?? executionId;

    public async Task<bool> GenerateImportTargetErrorKeysFileAsync(string executionId, string entityName, CancellationToken cancellationToken)
    {
        var value = await PostForValueAsync("GenerateImportTargetErrorKeysFile", paths.GenerateImportTargetErrorKeysFile, new { executionId, entityName }, noRetry: false, cancellationToken);
        return bool.TryParse(value, out var result) && result;
    }

    public Task<string?> GetImportTargetErrorKeysFileUrlAsync(string executionId, string entityName, CancellationToken cancellationToken) =>
        PostForValueAsync("GetImportTargetErrorKeysFileUrl", paths.GetImportTargetErrorKeysFileUrl, new { executionId, entityName }, noRetry: false, cancellationToken);

    public async Task<string> GetExecutionErrorsAsync(string executionId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, paths.GetExecutionErrors) { Content = JsonContent.Create(new { executionId }) };
        using var response = await SendAsync("GetExecutionErrors", request, authorize: true, noRetry: false, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<Stream> OpenDownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await SendAsync("Download", request, authorize: false, noRetry: false, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken), response, request);
    }

    public async Task<string> EnqueueAsync(Guid activityId, Stream content, string? company, string? entityName, string externalIdentifier, CancellationToken cancellationToken)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(company)) query.Add("company=" + Uri.EscapeDataString(company));
        if (!string.IsNullOrEmpty(entityName)) query.Add("entity=" + Uri.EscapeDataString(entityName));
        var path = D365ActionPaths.EnqueuePath + activityId.ToString("D") + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StreamContent(content) };
        request.Headers.Add("x-ms-dyn-externalidentifier", externalIdentifier);
        using var response = await SendAsync("Enqueue", request, authorize: true, noRetry: true, cancellationToken);
        var messageId = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim().Trim('"');
        return string.IsNullOrEmpty(messageId)
            ? throw Unexpected("Enqueue", "Enqueue returned empty message id.")
            : messageId;
    }

    public async Task<DequeuedMessage?> DequeueAsync(Guid activityId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, D365ActionPaths.DequeuePath + activityId.ToString("D"));
        using var response = await SendAsync("Dequeue", request, authorize: true, noRetry: false, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = JsonSerializer.Deserialize<DequeueBody>(body, Json);
        if (message?.CorrelationId is null || message.DownloadLocation is null)
        {
            throw Unexpected("Dequeue", $"Unexpected dequeue response: {LogMasking.Truncate(body)}");
        }

        // 4.x forced https for the download location.
        var location = new UriBuilder(message.DownloadLocation) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        return new DequeuedMessage(message.CorrelationId, message.PopReceipt ?? string.Empty, location);
    }

    public async Task AckAsync(Guid activityId, DequeuedMessage message, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, D365ActionPaths.AckPath + activityId.ToString("D"))
        {
            Content = JsonContent.Create(new DequeueBody
            {
                CorrelationId = message.CorrelationId,
                PopReceipt = message.PopReceipt,
                DownloadLocation = message.DownloadLocation.ToString()
            }, options: new JsonSerializerOptions())
        };
        using var response = await SendAsync("Ack", request, authorize: true, noRetry: false, cancellationToken);
    }

    public async Task<DataJobStatusDetail> GetJobStatusAsync(Guid activityId, string messageId, CancellationToken cancellationToken)
    {
        var path = D365ActionPaths.JobStatusPath + activityId.ToString("D") + "?jobId=" + Uri.EscapeDataString(messageId.Trim('"'));
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendAsync("JobStatus", request, authorize: true, noRetry: false, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<DataJobStatusDetail>(body, Json)
               ?? throw Unexpected("JobStatus", $"Unexpected job status response: {LogMasking.Truncate(body)}");
    }

    private async Task<string?> PostForValueAsync(string operation, string path, object body, bool noRetry, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: new JsonSerializerOptions()) };
        using var response = await SendAsync(operation, request, authorize: true, noRetry, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("value", out var value))
            {
                throw Unexpected(operation, $"Response of {operation} has no 'value' property: {LogMasking.Truncate(payload)}");
            }

            return value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True or JsonValueKind.False => value.GetBoolean().ToString(CultureInfo.InvariantCulture),
                _ => value.GetRawText()
            };
        }
        catch (JsonException ex)
        {
            throw Unexpected(operation, $"Response of {operation} is not valid JSON: {LogMasking.Truncate(payload)}", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string operation, HttpRequestMessage request, bool authorize, bool noRetry,
        CancellationToken cancellationToken, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        if (!request.RequestUri!.IsAbsoluteUri)
        {
            request.RequestUri = new Uri(connection.AosUri!, request.RequestUri);
        }

        request.Options.Set(D365RequestOptions.Operation, operation);
        request.Options.Set(D365RequestOptions.NoRetry, noRetry);
        if (authorize)
        {
            request.Options.Set(D365RequestOptions.Connection, connection);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, completion, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new D365ApiException(operation, request.RequestUri, null, null, new Dictionary<string, string>(), ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new D365ApiException(operation, request.RequestUri, response.StatusCode, body, CorrelationHeaders.From(response));
        }
    }

    private D365ApiException Unexpected(string operation, string message, Exception? inner = null) =>
        new(operation, connection.AosUri, HttpStatusCode.OK, message, new Dictionary<string, string>(), inner);

    private sealed class DequeueBody
    {
        public string? CorrelationId { get; set; }
        public string? PopReceipt { get; set; }
        public string? DownloadLocation { get; set; }
    }

    /// <summary>Stream that disposes the HTTP response together with the content stream.</summary>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response, HttpRequestMessage request) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                request.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
