using Quartz;
using Quartz.Extensibility;
using RIS.Hosting;
using RIS.Persistence;

namespace RIS.Server.Services;

/// <summary>One kind of stored data on the maintenance page.</summary>
public sealed record DataStore(string Key, long? Items, DateTimeOffset? OldestUtc, long Bytes, string Location, string Retention);

/// <summary>What RIS stores, how much and for how long; runs the cleanup on demand.</summary>
public sealed class MaintenanceService(
    ISchedulerFactory schedulers, IExecutionHistoryStore history, ConfigurationRepository repository, PersistenceOptions persistence,
    RisHostOptions host, RetentionOptions retention, DataMaintenanceService cleanup)
{
    public RetentionOptions Retention => retention;

    public MaintenanceResult? LastCleanup => cleanup.LastResult;

    public DateTimeOffset? NextCleanup => cleanup.NextRun;

    public Task<MaintenanceResult> CleanupNowAsync(CancellationToken ct = default) => cleanup.RunAsync(ct);

    public async Task<IReadOnlyList<DataStore>> StoresAsync(CancellationToken ct = default)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        // Total via IncludeTotalCount (second query); the oldest run is the last one of the newest-first list.
        var newest = await history.QueryExecutions(new ExecutionHistoryQuery { SchedulerName = scheduler.SchedulerName, Take = 1, IncludeTotalCount = true }, ct);
        var runs = newest.TotalCount ?? newest.Items.Count;
        DateTimeOffset? oldestRun = null;
        if (runs > 0)
        {
            var last = await history.QueryExecutions(new ExecutionHistoryQuery { SchedulerName = scheduler.SchedulerName, Skip = (int)Math.Min(runs - 1, int.MaxValue), Take = 1 }, ct);
            oldestRun = last.Items.FirstOrDefault()?.FiredAtUtc;
        }
        var (auditCount, auditOldest) = await repository.GetAuditStatsAsync(ct);

        var logs = Files(host.LogDirectory, "*.clef");
        var backups = Files(persistence.BackupDirectory, "*");
        return
        [
            new("RunHistory", runs, oldestRun, Size(persistence.QuartzDatabase), persistence.QuartzDatabase,
                $"{retention.RunHistoryDays} / {retention.FailedRunHistoryDays} d; max {retention.MaxRunsPerJob} / {retention.MaxRuns}"),
            new("Audit", auditCount, auditOldest.HasValue ? new DateTimeOffset(auditOldest.Value, TimeSpan.Zero) : null, Size(persistence.RisDatabase),
                persistence.RisDatabase, $"{retention.AuditDays} d"),
            new("Logs", logs.Count, logs.Count == 0 ? null : logs.Min(f => f.LastWriteTimeUtc), logs.Sum(f => f.Length), host.LogDirectory,
                $"{host.LogRetentionDays} d"),
            new("Backups", backups.Count, backups.Count == 0 ? null : backups.Min(f => f.LastWriteTimeUtc), backups.Sum(f => f.Length),
                persistence.BackupDirectory, $"{persistence.BackupsToKeep} + {retention.UpgradeBackupsToKeep}")
        ];
    }

    private static long Size(string database) =>
        new[] { database, database + "-wal", database + "-shm" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);

    private static List<FileInfo> Files(string folder, string pattern) =>
        Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles(pattern, SearchOption.AllDirectories).ToList() : [];
}
