using Microsoft.Extensions.Logging;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.Core.Polling;
using RIS.D365;

namespace RIS.Jobs;

/// <summary>Result of polling D365 for a value.</summary>
public sealed record PollResult(string? Value, int Checks, TimeSpan Elapsed);

/// <summary>
/// Repeats a D365 request with growing delays until the value is final or the deadline passes.
/// Final value always wins over the deadline. Transient failures (timeouts, throttling, 5xx, network) are retried until the deadline.
/// </summary>
public sealed class ExecutionPoller(IPollingClock clock)
{
    public IPollingClock Clock { get; } = clock;

    /// <param name="what">Description for logs, e.g. "export status".</param>
    /// <param name="request">Request returning the current value.</param>
    /// <param name="isFinal">Whether the value ends polling.</param>
    /// <param name="startedUtc">Start of the operation (elapsed time is measured from here).</param>
    /// <param name="deadlineUtc">After this time polling fails with <paramref name="timeoutErrorCode"/>.</param>
    public async Task<PollResult> PollAsync(
        string what,
        Func<CancellationToken, Task<string?>> request,
        Func<string?, bool> isFinal,
        PollingOptions polling,
        DateTime startedUtc,
        DateTime deadlineUtc,
        string timeoutErrorCode,
        IReadOnlyDictionary<string, object?> context,
        ILogger logger,
        CancellationToken cancellationToken,
        Action<int, string?, TimeSpan>? onCheck = null)
    {
        var backoff = new StatusCheckBackoff(polling);
        var checks = 0;
        string? lastValue = null;
        string? lastError = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checks++;

            try
            {
                lastValue = await request(cancellationToken);
                lastError = null;
                if (isFinal(lastValue))
                {
                    return new PollResult(lastValue, checks, Clock.UtcNow - startedUtc);
                }
            }
            catch (D365ApiException ex) when (ex.IsTransient)
            {
                lastError = ex.Message;
            }

            var now = Clock.UtcNow;
            if (now >= deadlineUtc)
            {
                var elapsed = Round(now - startedUtc);
                throw new RisException(timeoutErrorCode,
                    $"Waiting for {what} did not finish within {Round(deadlineUtc - startedUtc)}. Last value: {lastValue ?? "n/a"}. Elapsed: {elapsed}. Checks: {checks}. Last error: {lastError ?? "none"}",
                    new Dictionary<string, object?>(context) { ["LastValue"] = lastValue, ["Checks"] = checks, ["Elapsed"] = elapsed });
            }

            var delay = backoff.Next();
            if (now + delay > deadlineUtc)
            {
                delay = deadlineUtc - now; // last check exactly at the deadline
            }

            if (lastError is not null)
            {
                logger.LogWarning("[{ErrorCode}] Temporary error while waiting for {What}: {Error}. Next check in {Delay}",
                    ErrorCodes.HttpTransient, what, lastError, Round(delay));
            }
            else
            {
                logger.LogDebug("Waiting for {What}: check {Check}, value {Value}, next check in {Delay}", what, checks, lastValue, Round(delay));
            }

            onCheck?.Invoke(checks, lastValue, now - startedUtc);
            await Clock.Delay(delay, cancellationToken);
        }
    }

    public static TimeSpan Round(TimeSpan value) => TimeSpan.FromSeconds(Math.Round(value.TotalSeconds));
}
