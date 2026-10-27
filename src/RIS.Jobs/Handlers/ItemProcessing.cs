using Microsoft.Extensions.Logging;
using RIS.Core.Errors;

namespace RIS.Jobs.Handlers;

/// <summary>
/// Runs processing of one item (file, message) so that its failure does not stop the remaining items.
/// Configuration and authentication errors still stop the run - they would fail every item the same way.
/// </summary>
internal static class ItemProcessing
{
    private static readonly HashSet<string> RunFatalPrefixes = ["RIS-CFG-", "RIS-AUTH-"];

    public static async Task RunAsync(ILogger logger, ItemCounters counters, string itemDescription, Func<Task> process)
    {
        try
        {
            await process();
        }
        catch (RisException ex) when (ex.ErrorCode == ErrorCodes.FileLocked)
        {
            counters.Skipped++;
            logger.LogWarning("[{ErrorCode}] {Item} is locked by another process and is left for the next run: {Error}",
                ex.ErrorCode, itemDescription, ex.Message);
        }
        catch (RisException ex) when (!RunFatalPrefixes.Any(p => ex.ErrorCode.StartsWith(p, StringComparison.Ordinal)))
        {
            counters.Fail(ex.ErrorCode);
            logger.LogError(ex, "[{ErrorCode}] {Item} failed: {Error}", ex.ErrorCode, itemDescription, ex.Message);
        }
        catch (Exception ex) when (ex is not RisException and not OperationCanceledException)
        {
            counters.Fail(ErrorCodes.Unexpected);
            logger.LogError(ex, "[{ErrorCode}] {Item} failed with unexpected error: {Error}", ErrorCodes.Unexpected, itemDescription, ex.Message);
        }
    }
}
