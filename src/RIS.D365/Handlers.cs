using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using RIS.Core.Options;

namespace RIS.D365;

/// <summary>
/// Per-request options used by RIS HTTP handlers.
/// </summary>
public static class D365RequestOptions
{
    /// <summary>Connection used to authorize the request. Not set = no Authorization header (blob storage SAS URLs).</summary>
    public static readonly HttpRequestOptionsKey<D365ConnectionOptions> Connection = new("RIS.Connection");

    /// <summary>Operation name for logs, e.g. "ExportToPackage".</summary>
    public static readonly HttpRequestOptionsKey<string> Operation = new("RIS.Operation");

    /// <summary>Request must not be retried automatically (would duplicate work in D365, e.g. ExportToPackage).</summary>
    public static readonly HttpRequestOptionsKey<bool> NoRetry = new("RIS.NoRetry");
}

/// <summary>
/// Adds Entra ID token for requests sent to D365.
/// </summary>
public sealed class D365AuthenticationHandler(ID365TokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(D365RequestOptions.Connection, out var connection))
        {
            var header = await tokens.GetAuthorizationHeaderAsync(connection, cancellationToken);
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(header);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Logs every attempt: method, masked URL, status, duration, D365 correlation headers and (on failure) response body.
/// </summary>
public sealed class D365LoggingHandler(ILogger<D365LoggingHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.TryGetValue(D365RequestOptions.Operation, out var operation);
        var url = LogMasking.MaskUrl(request.RequestUri);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                logger.LogDebug("HTTP {Operation} {Method} {Url} -> {StatusCode} in {ElapsedMs} ms",
                    operation, request.Method, url, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
            }
            else
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("HTTP {Operation} {Method} {Url} -> {StatusCode} {Reason} in {ElapsedMs} ms. Correlation: {@Correlation}. Response: {ResponseBody}",
                    operation, request.Method, url, (int)response.StatusCode, response.ReasonPhrase, stopwatch.ElapsedMilliseconds,
                    CorrelationHeaders.From(response), LogMasking.Truncate(body));
            }
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("HTTP {Operation} {Method} {Url} failed after {ElapsedMs} ms: {Error}",
                operation, request.Method, url, stopwatch.ElapsedMilliseconds, ex.GetBaseException().Message);
            throw;
        }
    }
}

internal static class CorrelationHeaders
{
    public static IReadOnlyDictionary<string, string> From(HttpResponseMessage? response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response is null)
        {
            return result;
        }

        foreach (var header in response.Headers)
        {
            if (header.Key.StartsWith("ms-dyn-", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("request-id", StringComparison.OrdinalIgnoreCase))
            {
                result[header.Key] = string.Join(",", header.Value);
            }
        }
        return result;
    }
}
