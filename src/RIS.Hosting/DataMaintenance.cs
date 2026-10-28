using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RIS.Persistence;

namespace RIS.Hosting;

/// <summary>
/// How long RIS keeps data (Ris:Retention). Run history is trimmed by Quartz itself (execution history options, applied
/// when written and read); the rest by <see cref="DataMaintenanceService"/> once a day.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Ris:Retention";

    /// <summary>Runs in the history (succeeded, skipped, cancelled).</summary>
    public int RunHistoryDays { get; set; } = 30;

    /// <summary>Failed runs are kept longer: they are what people look for.</summary>
    public int FailedRunHistoryDays { get; set; } = 90;

    /// <summary>Newest runs kept per job (failures are not counted); 0 = no limit per job.</summary>
    public int MaxRunsPerJob { get; set; } = 1000;

    /// <summary>Runs kept in total.</summary>
    public int MaxRuns { get; set; } = 200_000;

    /// <summary>Captured log of one run (shown in the run details).</summary>
    public int RunLogKilobytes { get; set; } = 64;

    /// <summary>Configuration changes (audit). The newest entry is always kept.</summary>
    public int AuditDays { get; set; } = 365;

    /// <summary>backup\before-upgrade-* folders written by the setup.</summary>
    public int UpgradeBackupsToKeep { get; set; } = 5;

    /// <summary>appsettings.json.*.bak written by the setup on reconfiguration.</summary>
    public int SettingsBackupsToKeep { get; set; } = 5;

    /// <summary>Daily cleanup time (server time zone).</summary>
    public TimeOnly CleanupAt { get; set; } = new(3, 30);
}

public sealed record MaintenanceResult(DateTimeOffset StartedAt, TimeSpan Duration, int AuditEntriesDeleted, int LogFilesDeleted,
    int LogFoldersDeleted, int BackupsDeleted, bool Vacuumed, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// Daily cleanup of data RIS keeps outside the run history: audit entries, log files (also folders of deleted jobs, which
/// Serilog never rolls), old upgrade backups and settings backups; compacts ris.db after a large purge.
/// </summary>
public sealed class DataMaintenanceService(
    RetentionOptions retention, RisHostOptions host, PersistenceOptions persistence, ConfigurationRepository repository,
    TimeProvider time, ILogger<DataMaintenanceService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _running = new(1, 1);

    public MaintenanceResult? LastResult { get; private set; }

    public DateTimeOffset? NextRun { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First pass shortly after start (not during the busy startup), then every day at CleanupAt.
        NextRun = time.GetLocalNow().AddMinutes(10);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = NextRun.Value - time.GetLocalNow();
            if (delay > TimeSpan.Zero) await Task.Delay(delay, time, stoppingToken);
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            NextRun = NextDaily(time.GetLocalNow(), retention.CleanupAt);
        }
    }

    public static DateTimeOffset NextDaily(DateTimeOffset now, TimeOnly at)
    {
        var today = new DateTimeOffset(now.Date + at.ToTimeSpan(), now.Offset);
        return today > now ? today : today.AddDays(1);
    }

    /// <summary>Runs the cleanup now (also from the maintenance page); a second call waits for the running one.</summary>
    public async Task<MaintenanceResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            var started = time.GetLocalNow();
            var errors = new List<string>();
            var now = time.GetUtcNow().UtcDateTime;

            var audit = await Step("audit", errors, () => repository.PurgeAuditAsync(now.AddDays(-retention.AuditDays), cancellationToken));
            var (files, folders) = Step("logs", errors, () => CleanLogs(now.AddDays(-host.LogRetentionDays)));
            var backups = Step("backups", errors, () =>
                KeepNewest(Directory.Exists(persistence.BackupDirectory) ? Directory.GetDirectories(persistence.BackupDirectory, "before-upgrade-*") : [],
                    retention.UpgradeBackupsToKeep, d => Directory.Delete(d, recursive: true))
                + KeepNewest(Directory.GetFiles(persistence.DataDirectory, "appsettings.json.*.bak"), retention.SettingsBackupsToKeep, File.Delete));
            var vacuumed = audit >= 1000 && await Step("vacuum", errors, async () => { await repository.VacuumAsync(cancellationToken); return true; });

            var result = new MaintenanceResult(started, time.GetLocalNow() - started, audit, files, folders, backups, vacuumed, errors);
            LastResult = result;
            logger.Log(result.Succeeded ? LogLevel.Information : LogLevel.Warning,
                "Data cleanup: {Audit} audit entries, {Files} log files, {Folders} log folders, {Backups} backups deleted{Vacuum}{Errors}",
                audit, files, folders, backups, vacuumed ? "; ris.db compacted" : "", errors.Count == 0 ? "" : "; errors: " + string.Join("; ", errors));
            return result;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Log files older than the cutoff, and folders of jobs (logs\jobs\*) left empty, e.g. of deleted jobs.</summary>
    private (int Files, int Folders) CleanLogs(DateTime cutoffUtc)
    {
        if (!Directory.Exists(host.LogDirectory)) return (0, 0);
        var files = 0;
        foreach (var file in Directory.EnumerateFiles(host.LogDirectory, "*.clef", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) >= cutoffUtc) continue;
            try
            {
                File.Delete(file);
                files++;
            }
            catch (IOException)
            {
                // Still open by a sink: the next run deletes it.
            }
        }

        var folders = 0;
        var jobs = Path.Combine(host.LogDirectory, "jobs");
        if (Directory.Exists(jobs))
        {
            foreach (var folder in Directory.GetDirectories(jobs).Where(d => !Directory.EnumerateFileSystemEntries(d).Any()))
            {
                Directory.Delete(folder);
                folders++;
            }
        }
        return (files, folders);
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> paths (names end with a sortable time stamp).</summary>
    private static int KeepNewest(IEnumerable<string> paths, int keep, Action<string> delete)
    {
        var old = paths.OrderByDescending(p => p, StringComparer.Ordinal).Skip(Math.Max(0, keep)).ToList();
        old.ForEach(delete);
        return old.Count;
    }

    private T Step<T>(string name, List<string> errors, Func<T> step)
    {
        try
        {
            return step();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Data cleanup step {Step} failed", name);
            errors.Add($"{name}: {ex.Message}");
            return default!;
        }
    }

    private async Task<T> Step<T>(string name, List<string> errors, Func<Task<T>> step)
    {
        try
        {
            return await step();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(ex, "Data cleanup step {Step} failed", name);
            errors.Add($"{name}: {ex.Message}");
            return default!;
        }
    }
}
