using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RIS.Core.Configuration;

namespace RIS.Persistence;

/// <summary>
/// Application settings (UI/CLI, audited) and the activity of jobs (last run that did real work). Unlike
/// <see cref="ConfigurationRepository"/> it does not reload the configuration or touch the scheduler.
/// </summary>
public sealed class SettingsRepository(IDbContextFactory<RisDbContext> contexts, TimeProvider time)
{
    /// <summary>Since when RIS records job activity (first start of a version that does): earlier there is no data.</summary>
    public const string ActivityTrackingKey = "JobActivity.TrackingSinceUtc";

    public event Action? SettingsChanged;

    public async Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : new()
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entity = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        return entity is null ? new T() : JsonSerializer.Deserialize<T>(entity.Value, RisJson.Options) ?? new T();
    }

    /// <summary>Saves a setting (validated) with an audit entry "Setting / key / Updated".</summary>
    public async Task SaveAsync<T>(string key, T value, string? user, CancellationToken cancellationToken = default)
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(value!, new ValidationContext(value!), errors, validateAllProperties: true))
        {
            throw new ConfigurationValidationException(errors.Select(e => e.ErrorMessage ?? "Invalid value.").ToList());
        }

        var json = JsonSerializer.Serialize(value, RisJson.Options);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entity = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        var before = entity?.Value;
        if (before == json) return;
        if (entity is null) db.Settings.Add(entity = new SettingEntity { Key = key, Value = json });
        entity.Value = json;
        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        entity.UpdatedBy = user;
        db.Audit.Add(new AuditEntry
        {
            TimestampUtc = entity.UpdatedUtc, User = user, EntityType = "Setting", EntityKey = key,
            Action = before is null ? "Created" : "Updated", Before = before, After = json
        });
        await db.SaveChangesAsync(cancellationToken);
        SettingsChanged?.Invoke();
    }

    /// <summary>Runtime state kept between restarts (e.g. the last idle guard result): no validation, no audit, no event.</summary>
    public async Task SaveRuntimeAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entity = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (entity is null) db.Settings.Add(entity = new SettingEntity { Key = key, Value = "" });
        entity.Value = JsonSerializer.Serialize(value, RisJson.Options);
        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        entity.UpdatedBy = "RIS";
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>When activity tracking started; set on the first call (service start of the first version that records it).</summary>
    public async Task<DateTimeOffset> EnsureActivityTrackingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entity = await db.Settings.FirstOrDefaultAsync(s => s.Key == ActivityTrackingKey, cancellationToken);
        if (entity is not null) return JsonSerializer.Deserialize<DateTimeOffset>(entity.Value, RisJson.Options);
        var now = time.GetUtcNow();
        db.Settings.Add(new SettingEntity { Key = ActivityTrackingKey, Value = JsonSerializer.Serialize(now, RisJson.Options), UpdatedUtc = now.UtcDateTime, UpdatedBy = "RIS" });
        await db.SaveChangesAsync(cancellationToken);
        return now;
    }

    /// <summary>Records a run that did real work (upsert, no audit: this is runtime data, not configuration).</summary>
    public async Task RecordWorkAsync(string group, string name, DateTimeOffset finishedUtc, string? summary, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var entity = await db.JobActivity.FirstOrDefaultAsync(a => a.Group == group && a.Name == name, cancellationToken);
        if (entity is null) db.JobActivity.Add(entity = new JobActivityEntity { Group = group, Name = name });
        entity.LastWorkUtc = finishedUtc.UtcDateTime;
        entity.LastWorkSummary = summary is { Length: > 500 } ? summary[..500] : summary;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Last run with real work per job key "Group.Name".</summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetLastWorkAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return (await db.JobActivity.AsNoTracking().ToListAsync(cancellationToken))
            .ToDictionary(a => $"{a.Group}.{a.Name}", a => new DateTimeOffset(DateTime.SpecifyKind(a.LastWorkUtc, DateTimeKind.Utc)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Last change of each job definition (saved, moved, paused or resumed) per key "Group.Name".</summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetJobChangesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return (await db.Jobs.AsNoTracking().Select(j => new { j.Group, j.Name, j.UpdatedUtc }).ToListAsync(cancellationToken))
            .ToDictionary(j => $"{j.Group}.{j.Name}", j => new DateTimeOffset(DateTime.SpecifyKind(j.UpdatedUtc, DateTimeKind.Utc)), StringComparer.OrdinalIgnoreCase);
    }
}
