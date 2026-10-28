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
        D365Message = ExtractMessage(responseBody);
    }

    /// <summary>The message D365 put into the OData error (deepest meaningful inner message), when there is one.</summary>
    public string? D365Message { get; }

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
        if (status is null) return $"D365 request {operation} to {target} failed: {inner?.GetBaseException().Message}";
        // The OData error of D365 carries the reason deep inside, followed by stack traces: show only the reason
        // (the whole body stays in ResponseBody, e.g. in the error report next to the file).
        return ExtractMessage(body) is { } message
            ? $"D365 request {operation} to {target} failed with HTTP {(int)status} {status}: {message}"
            : $"D365 request {operation} to {target} failed with HTTP {(int)status} {status}. Response: {LogMasking.Truncate(body)}";
    }

    /// <summary>
    /// {"error":{"message":"An error has occurred.","innererror":{"message":"Exception occurred while executing action X: reason",
    /// "internalexception":{"message":"Exception has been thrown by the target of an invocation.","internalexception":{"message":"reason"}}}}}
    /// gives "reason": the deepest message that is not one of the generic wrappers.
    /// </summary>
    internal static string? ExtractMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('{')) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var node) || node.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            string? best = null;
            while (true)
            {
                if (node.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String
                    && m.GetString() is { Length: > 0 } text && !IsGeneric(text))
                {
                    best = text;
                }
                if ((node.TryGetProperty("innererror", out var next) || node.TryGetProperty("internalexception", out next))
                    && next.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    node = next;
                }
                else
                {
                    return best;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool IsGeneric(string message) =>
        message == "An error has occurred." || message.StartsWith("Exception has been thrown by the target of an invocation", StringComparison.Ordinal);
}
