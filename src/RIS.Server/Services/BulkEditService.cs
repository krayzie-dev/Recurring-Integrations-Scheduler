using System.Reflection;
using System.Text.Json;
using RIS.Core.Configuration;
using RIS.Core.Options;
using RIS.Persistence;

namespace RIS.Server.Services;

/// <summary>
/// Changes applied to every selected job. Only the values that are set are applied, in this order:
/// common fields, schedule (cron / interval), shift, staggering, replacement in folder paths.
/// </summary>
public sealed record BulkEdit
{
    public string? Connection { get; init; }
    public string? Group { get; init; }
    public string? TimeZone { get; init; }
    /// <summary>Removes the time zone (server time zone is used).</summary>
    public bool ClearTimeZone { get; init; }
    public MissedRunPolicy? MissedRuns { get; init; }
    public bool? Paused { get; init; }
    public bool? PauseOnError { get; init; }
    public TimeSpan? PollingInitialDelay { get; init; }
    public TimeSpan? PollingMaxDelay { get; init; }
    public TimeSpan? PollingTimeout { get; init; }
    public int? RetryCount { get; init; }
    public TimeSpan? RetryDelay { get; init; }

    /// <summary>Same cron for all jobs.</summary>
    public string? Cron { get; init; }
    /// <summary>Same interval (with <see cref="StartAt"/>) for all jobs.</summary>
    public TimeSpan? Interval { get; init; }
    public DateTimeOffset? StartAt { get; init; }

    /// <summary>Moves run times by minutes (negative = earlier).</summary>
    public int? ShiftMinutes { get; init; }

    /// <summary>Staggers starts: first job at this time, each next one <see cref="StaggerStepMinutes"/> later.</summary>
    public TimeOnly? StaggerStart { get; init; }
    public int? StaggerStepMinutes { get; init; }

    public string? PathFind { get; init; }
    public string? PathReplace { get; init; }
    public bool PathIgnoreCase { get; init; } = true;
}

public sealed record FieldChange(string Job, string Field, string? Before, string? After);

public sealed record BulkEditPreview(
    IReadOnlyList<FieldChange> Changes, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors, IReadOnlyList<JobChange> Jobs)
{
    public bool CanApply => Errors.Count == 0 && Changes.Count > 0;
}

/// <summary>Bulk edit of job definitions: preview (differences, warnings, validation of the whole result) and save in one transaction.</summary>
public sealed class BulkEditService(IRisConfigurationStore store, ConfigurationRepository repository)
{
    public BulkEditPreview Preview(IReadOnlyList<JobRef> jobs, BulkEdit edit)
    {
        var configuration = store.Current;
        var changes = new List<FieldChange>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var result = new List<JobChange>();

        var index = 0;
        foreach (var reference in jobs)
        {
            if (configuration.FindJob(reference.Key) is not { } original)
            {
                errors.Add($"{reference.Key}: job is not configured.");
                continue;
            }

            var before = Snapshot(original);
            var job = Clone(original);
            Apply(job, edit, index++, warnings);
            var after = Snapshot(job);
            var jobChanges = Diff(original.Key, before, after);
            if (jobChanges.Count == 0) continue;
            changes.AddRange(jobChanges);
            result.Add(new JobChange(original.Group, original.Name, job));
        }

        if (result.Count > 0)
        {
            var proposed = new RisConfiguration
            {
                Connections = configuration.Connections,
                SftpServers = configuration.SftpServers,
                Jobs = configuration.Jobs
                    .Where(j => !result.Any(r => string.Equals($"{r.OriginalGroup}.{r.OriginalName}", j.Key, StringComparison.OrdinalIgnoreCase)))
                    .Concat(result.Select(r => r.Job)).ToList()
            };
            var keys = result.Select(r => r.Job.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Only errors of the edited jobs (or global ones, e.g. duplicate keys) block the edit.
            errors.AddRange(ConfigurationValidator.Validate(proposed)
                .Where(e => keys.Any(k => e.Contains(k, StringComparison.OrdinalIgnoreCase)) || !configuration.Jobs.Any(j => e.Contains(j.Key, StringComparison.OrdinalIgnoreCase))));
        }
        return new BulkEditPreview(changes, warnings, errors, result);
    }

    public async Task<BulkEditPreview> ApplyAsync(IReadOnlyList<JobRef> jobs, BulkEdit edit, string? user, CancellationToken ct = default)
    {
        var preview = Preview(jobs, edit);
        if (preview.CanApply) await repository.SaveJobsAsync(preview.Jobs, user, ct);
        return preview;
    }

    private static void Apply(JobDefinition job, BulkEdit edit, int index, List<string> warnings)
    {
        var options = job.Options!;
        if (!string.IsNullOrWhiteSpace(edit.Connection)) options.Connection = edit.Connection.Trim();
        if (!string.IsNullOrWhiteSpace(edit.Group)) job.Group = edit.Group.Trim();
        if (edit.ClearTimeZone) job.Schedule.TimeZone = null;
        else if (!string.IsNullOrWhiteSpace(edit.TimeZone)) job.Schedule.TimeZone = edit.TimeZone.Trim();
        if (edit.MissedRuns is { } missed) job.Schedule.MissedRuns = missed;
        if (edit.Paused is { } paused) job.Schedule.Paused = paused;
        if (edit.PauseOnError is { } pauseOnError) options.PauseOnError = pauseOnError;
        if (edit.RetryCount is { } count) options.Retry.Count = count;
        if (edit.RetryDelay is { } delay) options.Retry.Delay = delay;

        if (edit.PollingInitialDelay is not null || edit.PollingMaxDelay is not null || edit.PollingTimeout is not null)
        {
            if (options.GetType().GetProperty("Polling")?.GetValue(options) is PollingOptions polling)
            {
                if (edit.PollingInitialDelay is { } initial) polling.InitialDelay = initial;
                if (edit.PollingMaxDelay is { } max) polling.MaxDelay = max;
                if (edit.PollingTimeout is { } timeout) polling.Timeout = timeout;
            }
            else
            {
                warnings.Add($"{job.Key}: {job.Type} has no status polling; polling settings skipped.");
            }
        }

        if (!string.IsNullOrWhiteSpace(edit.Cron))
        {
            job.Schedule.Cron = edit.Cron.Trim();
            job.Schedule.Interval = null;
            job.Schedule.StartAt = null;
        }
        else if (edit.Interval is { } interval)
        {
            job.Schedule.Cron = null;
            job.Schedule.Interval = interval;
            job.Schedule.StartAt = edit.StartAt ?? job.Schedule.StartAt ?? new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
        }

        if (edit.ShiftMinutes is { } shift and not 0) Shift(job, shift, warnings);
        if (edit.StaggerStart is { } start && edit.StaggerStepMinutes is { } step) Stagger(job, start.AddMinutes(step * index), warnings);

        if (!string.IsNullOrEmpty(edit.PathFind))
        {
            ReplacePaths(options, edit.PathFind, edit.PathReplace ?? string.Empty,
                edit.PathIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Cron: minute and hour fields must be plain numbers (hour may be "*", then only minutes move within the hour).
    /// Crossing midnight with a specific day of week/month is refused. Interval: StartAt moves.
    /// </summary>
    internal static void Shift(JobDefinition job, int minutes, List<string> warnings)
    {
        if (job.Schedule.Interval is not null)
        {
            if (job.Schedule.StartAt is { } start) job.Schedule.StartAt = start.AddMinutes(minutes);
            return;
        }

        var fields = job.Schedule.Cron?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields is not { Length: >= 6 } || !int.TryParse(fields[1], out var minute))
        {
            warnings.Add($"{job.Key}: cron '{job.Schedule.Cron}' cannot be shifted automatically; change it manually.");
            return;
        }

        if (fields[2] == "*")
        {
            fields[1] = Modulo(minute + minutes, 60).ToString();
        }
        else if (int.TryParse(fields[2], out var hour))
        {
            var total = hour * 60 + minute + minutes;
            var crossesDay = total < 0 || total >= 24 * 60;
            if (crossesDay && !(fields[3] is "*" or "?" && fields[5] is "*" or "?"))
            {
                warnings.Add($"{job.Key}: shift crosses midnight and the cron has specific days; change it manually.");
                return;
            }
            total = Modulo(total, 24 * 60);
            fields[1] = (total % 60).ToString();
            fields[2] = (total / 60).ToString();
        }
        else
        {
            warnings.Add($"{job.Key}: cron hour field '{fields[2]}' cannot be shifted automatically; change it manually.");
            return;
        }
        job.Schedule.Cron = string.Join(' ', fields);
    }

    internal static void Stagger(JobDefinition job, TimeOnly time, List<string> warnings)
    {
        if (job.Schedule.Interval is not null)
        {
            var start = job.Schedule.StartAt ?? DateTimeOffset.Now;
            job.Schedule.StartAt = new DateTimeOffset(start.Date + time.ToTimeSpan(), start.Offset);
            return;
        }

        var fields = job.Schedule.Cron?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields is not { Length: >= 6 } || !int.TryParse(fields[1], out _) || !int.TryParse(fields[2], out _))
        {
            warnings.Add($"{job.Key}: cron '{job.Schedule.Cron}' does not run at one time of day; not staggered.");
            return;
        }
        fields[0] = "0";
        fields[1] = time.Minute.ToString();
        fields[2] = time.Hour.ToString();
        job.Schedule.Cron = string.Join(' ', fields);
    }

    /// <summary>Folder-like string properties of the options and their sections.</summary>
    internal static void ReplacePaths(object target, string find, string replace, StringComparison comparison)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            if (property.PropertyType == typeof(string) && IsPath(property.Name) && property.GetValue(target) is string value
                && value.Contains(find, comparison))
            {
                property.SetValue(target, value.Replace(find, replace, comparison));
            }
            else if (property.PropertyType.IsClass && property.PropertyType != typeof(string)
                     && property.PropertyType.Namespace?.StartsWith("RIS.Core", StringComparison.Ordinal) == true
                     && property.GetValue(target) is { } section)
            {
                ReplacePaths(section, find, replace, comparison);
            }
        }
    }

    private static bool IsPath(string name) =>
        name.EndsWith("Dir", StringComparison.Ordinal) || name.EndsWith("Path", StringComparison.Ordinal)
        || name is "PackageTemplate" or "RemoteFolder";

    private static int Modulo(int value, int divisor) => (value % divisor + divisor) % divisor;

    private static JobDefinition Clone(JobDefinition job) =>
        JsonSerializer.Deserialize<JobDefinition>(JsonSerializer.Serialize(job, RisJson.Options), RisJson.Options)!;

    /// <summary>Flat "path → value" view of a definition, for the differences.</summary>
    private static Dictionary<string, string?> Snapshot(JobDefinition job)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Group"] = job.Group,
            ["Name"] = job.Name
        };
        Flatten(JsonSerializer.SerializeToElement(job.Schedule, RisJson.Options), "Schedule", result);
        Flatten(JsonSerializer.SerializeToElement(job.Options, RisJson.Options), "Options", result);
        return result;
    }

    private static void Flatten(JsonElement element, string path, Dictionary<string, string?> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject()) Flatten(property.Value, $"{path}.{property.Name}", result);
        }
        else
        {
            result[path] = element.ValueKind == JsonValueKind.Null ? null : element.ToString();
        }
    }

    private static List<FieldChange> Diff(string job, Dictionary<string, string?> before, Dictionary<string, string?> after) =>
        before.Keys.Union(after.Keys)
            .Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k))
            .Select(k => new FieldChange(job, k, before.GetValueOrDefault(k), after.GetValueOrDefault(k)))
            .ToList();
}
