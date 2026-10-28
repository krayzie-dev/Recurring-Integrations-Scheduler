using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RIS.Core.Options;
using RIS.Core.Polling;
using RIS.D365;

namespace RIS.Jobs;

/// <summary>
/// Legal entities of each D365 environment, used to check the company of a multi-company import before anything is sent to D365.
/// The list is read once a day per environment; an unknown company reloads it (at most every 5 minutes), so a company created
/// during the day is accepted. When the list cannot be read, the check is skipped and D365 validates the company itself.
/// </summary>
public sealed class LegalEntityCache(IPollingClock clock, ILogger<LegalEntityCache> logger)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);
    public static readonly TimeSpan MinReloadInterval = TimeSpan.FromMinutes(5);

    private sealed record Entry(HashSet<string> Ids, DateTime LoadedUtc);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>True when the company exists, false when it does not, null when the list of companies is not available.</summary>
    public async Task<bool?> ExistsAsync(ID365Client client, D365ConnectionOptions connection, string legalEntity, CancellationToken cancellationToken)
    {
        var key = connection.AosUri?.GetLeftPart(UriPartial.Authority) ?? connection.Name;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var now = clock.UtcNow;
            _entries.TryGetValue(key, out var entry);
            var stale = entry is null || now - entry.LoadedUtc >= MaxAge;
            var unknown = entry is not null && !entry.Ids.Contains(legalEntity);
            var failedRecently = _failures.TryGetValue(key, out var failed) && now - failed < MinReloadInterval;
            if (!failedRecently && (stale || (unknown && now - entry!.LoadedUtc >= MinReloadInterval)))
            {
                try
                {
                    var ids = await client.GetLegalEntitiesAsync(cancellationToken);
                    entry = new Entry(new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase), now);
                    _entries[key] = entry;
                    _failures.TryRemove(key, out _);
                    logger.LogInformation("Loaded {Count} legal entities of {Environment}", ids.Count, key);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _failures[key] = now;
                    logger.LogWarning(ex, "Legal entities of {Environment} could not be read; company {LegalEntity} is not checked before import", key, legalEntity);
                }
            }

            return entry is null || now - entry.LoadedUtc >= MaxAge ? null : entry.Ids.Contains(legalEntity);
        }
        finally
        {
            _lock.Release();
        }
    }
}
