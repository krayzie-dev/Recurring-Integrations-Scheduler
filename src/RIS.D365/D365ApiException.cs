using System.Net;
using RIS.Core.Errors;

namespace RIS.D365;

/// <summary>
/// D365 (or blob storage) returned an unsuccessful response or could not be reached.
/// </summary>
public sealed class D365ApiException : RisException
{
    public D365ApiException(string operation, Uri? requestUri, HttpStatusCode? statusCode, string? responseBody,
        IReadOnlyDictionary<string, string> correlationHeaders, Exception? innerException = null)
        : base(IsTransientStatus(statusCode, innerException) ? ErrorCodes.HttpTransient : ErrorCodes.HttpRequestFailed,
            BuildMessage(operation, requestUri, statusCode, responseBody, innerException),
            new Dictionary<string, object?>
            {
                ["Operation"] = operation,
                ["Url"] = LogMasking.MaskUrl(requestUri),
                ["StatusCode"] = statusCode is null ? null : (int)statusCode,
                ["CorrelationHeaders"] = correlationHeaders
            },
            innerException)
    {
        Operation = operation;
        StatusCode = statusCode;
        ResponseBody = responseBody;
        CorrelationHeaders = correlationHeaders;
    }

    public string Operation { get; }

    public HttpStatusCode? StatusCode { get; }

    public string? ResponseBody { get; }

    public IReadOnlyDictionary<string, string> CorrelationHeaders { get; }

    /// <summary>Temporary problem (timeout, throttling, 5xx, network): worth trying again later.</summary>
    public bool IsTransient => ErrorCode == ErrorCodes.HttpTransient;

    public static bool IsTransientStatus(HttpStatusCode? statusCode, Exception? exception = null)
    {
        if (statusCode is null)
        {
            // No response at all: network error, DNS, timeout.
            return exception is HttpRequestException or TaskCanceledException or TimeoutException;
        }

        var code = (int)statusCode;
        return code is 408 or 429 or 599 || code >= 500;
    }

    private static string BuildMessage(string operation, Uri? uri, HttpStatusCode? status, string? body, Exception? inner)
    {
        var target = LogMasking.MaskUrl(uri);
        return status is null
            ? $"D365 request {operation} to {target} failed: {inner?.GetBaseException().Message}"
            : $"D365 request {operation} to {target} failed with HTTP {(int)status} {status}. Response: {LogMasking.Truncate(body)}";
    }
}
