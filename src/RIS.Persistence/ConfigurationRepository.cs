using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RIS.Core.Configuration;
using RIS.Core.Errors;
using RIS.Core.Options;

namespace RIS.Persistence;

/// <summary>Configuration change rejected by validation.</summary>
public sealed class ConfigurationValidationException(IReadOnlyList<string> errors)
    : RisException(ErrorCodes.ConfigurationInvalid, "Configuration is invalid: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Configuration item (connection, SFTP server, job) does not exist.</summary>
public sealed class ConfigurationNotFoundException(string what, string name)
    : RisException(ErrorCodes.ConfigurationInvalid, $"{what} '{name}' does not exist.", new Dictionary<string, object?> { ["Name"] = name });

/// <summary>New definition of an existing job identified by its current group and name.</summary>
public sealed record JobChange(string OriginalGroup, string OriginalName, JobDefinition Job);

public enum SecretOwner
{
    Connection,
    SftpServer
}

/// <summary>
/// Reads and changes RIS configuration in the database. Every change is validated together with the rest of the
/// configuration (e.g. a connection used by a job cannot be deleted) and written to the audit log without secrets.
/// </summary>
public sealed class ConfigurationRepository(IDbContextFactory<RisDbContext> contexts, ISecretProtector secrets, TimeProvider time)
{
    /// <summary>Raised after a successful change.</summary>
    public event Action? Changed;

    /// <summary>Raised after a successful change with the complete, validated configuration (with secrets) as it is now.</summary>
    public event Action<RisConfiguration>? Committed;

    public async Task<RisConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return new RisConfiguration
        {
            Connections = (await db.Connections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken)).Select(ToOptions).ToList(),
            SftpServers = (await db.SftpServers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(cancellationToken)).Select(ToOptions).ToList(),
            Jobs = (await db.Jobs.AsNoTracking().OrderBy(j => j.Group).ThenBy(j => j.Name).ToListAsync(cancellationToken)).Select(ToDefinition).ToList()
        };
    }

    /// <summary>Configuration with secrets replaced by <see cref="SecretPlaceholder.Value"/> (for export and API).</summary>
    public async Task<RisConfiguration> ExportAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await LoadAsync(cancellationToken);
        configuration.Connections = configuration.Connections.Select(WithoutSecrets).ToList();
        configuration.SftpServers = configuration.SftpServers.Select(WithoutSecrets).ToList();
        return configuration;
    }

    /// <summary>Hash of each job definition (schedule + options), used by the scheduler synchronizer.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetJobHashesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Jobs.AsNoTracking().ToDictionaryAsync(j => $"{j.Group}.{j.Name}", j => j.Hash, StringComparer.OrdinalIgnoreCase, cancellationToken);
    }

    public Task SaveConnectionAsync(D365ConnectionOptions connection, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            var entity = await db.Connections.FirstOrDefaultAsync(c => c.Name == connection.Name, cancellationToken);
            var before = entity is null ? null : AuditJson(entity);
            string?[] previousSecrets = [entity?.ProtectedClientSecret, entity?.ProtectedUserPassword];
            if (entity is null)
            {
                entity = new ConnectionEntity { Name = connection.Name, AosUri = "", Tenant = "", Authority = "", ClientId = "" };
                db.Connections.Add(entity);
            }

            Apply(entity, connection);
            Replace(configuration.Connections, c => c.Name, ToOptions(entity));
            audit("Connection", connection.Name, before is null ? "Created" : "Updated", before, AuditJson(entity, previousSecrets));
        });

    public Task SaveSftpServerAsync(SftpServerOptions server, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            var entity = await db.SftpServers.FirstOrDefaultAsync(s => s.Name == server.Name, cancellationToken);
            var before = entity is null ? null : AuditJson(entity);
            string?[] previousSecrets = [entity?.ProtectedPassword, entity?.ProtectedKeyPassphrase];
            if (entity is null)
            {
                entity = new SftpServerEntity { Name = server.Name, Host = "", UserName = "" };
                db.SftpServers.Add(entity);
            }

            Apply(entity, server);
            Replace(configuration.SftpServers, s => s.Name, ToOptions(entity));
            audit("SftpServer", server.Name, before is null ? "Created" : "Updated", before, AuditJson(entity, previousSecrets));
        });

    public Task SaveJobAsync(JobDefinition job, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            var entity = await db.Jobs.FirstOrDefaultAsync(j => j.Group == job.Group && j.Name == job.Name, cancellationToken);
            var before = entity is null ? null : JobJson(ToDefinition(entity));
            if (entity is null)
            {
                entity = new JobDefinitionEntity { Name = job.Name, Group = job.Group, ScheduleJson = "", OptionsJson = "", Hash = "" };
                db.Jobs.Add(entity);
            }

            Apply(entity, job);
            Replace(configuration.Jobs, j => j.Key, ToDefinition(entity));
            audit("Job", job.Key, before is null ? "Created" : "Updated", before, JobJson(job));
        });

    /// <summary>
    /// Saves changes of many jobs at once (bulk edit): one validation of the whole result, one transaction, one audit entry
    /// per job plus a summary entry, one <see cref="Changed"/> event. Nothing is saved when any job is invalid.
    /// A change of group or name moves the job (the old key is removed).
    /// </summary>
    public Task SaveJobsAsync(IReadOnlyList<JobChange> changes, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            foreach (var change in changes)
            {
                var entity = await db.Jobs.FirstOrDefaultAsync(j => j.Group == change.OriginalGroup && j.Name == change.OriginalName, cancellationToken)
                             ?? throw NotFound("Job", $"{change.OriginalGroup}.{change.OriginalName}");
                var before = JobJson(ToDefinition(entity));
                var originalKey = $"{change.OriginalGroup}.{change.OriginalName}";
                if (!string.Equals(originalKey, change.Job.Key, StringComparison.OrdinalIgnoreCase))
                {
                    configuration.Jobs.RemoveAll(j => string.Equals(j.Key, originalKey, StringComparison.OrdinalIgnoreCase));
                    audit("Job", originalKey, "Moved", before, change.Job.Key);
                }

                Apply(entity, change.Job);
                Replace(configuration.Jobs, j => j.Key, ToDefinition(entity));
                audit("Job", change.Job.Key, "Updated", before, JobJson(change.Job));
            }
            audit("Job", "*", "BulkUpdated", null, $"{changes.Count} job(s): {string.Join(", ", changes.Select(c => c.Job.Key))}");
        });

    public Task DeleteConnectionAsync(string name, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            var entity = await db.Connections.FirstOrDefaultAsync(c => c.Name == name, cancellationToken) ?? throw NotFound("Connection", name);
            db.Connections.Remove(entity);
            configuration.Connections.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            audit("Connection", name, "Deleted", AuditJson(entity), null);
        });

    public Task DeleteSftpServerAsync(string name, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            var entity = await db.SftpServers.FirstOrDefaultAsync(s => s.Name == name, cancellationToken) ?? throw NotFound("SFTP server", name);
            db.SftpServers.Remove(entity);
            configuration.SftpServers.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            audit("SftpServer", name, "Deleted", AuditJson(entity), null);
        });

    /// <summary>Deletes several jobs in one transaction (one validation, one reload, one scheduler synchronization).</summary>
    public Task DeleteJobsAsync(IReadOnlyCollection<(string Group, string Name)> jobs, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            foreach (var (group, name) in jobs)
            {
                var entity = await db.Jobs.FirstOrDefaultAsync(j => j.Group == group && j.Name == name, cancellationToken) ?? throw NotFound("Job", $"{group}.{name}");
                db.Jobs.Remove(entity);
                configuration.Jobs.RemoveAll(j => string.Equals(j.Key, $"{group}.{name}", StringComparison.OrdinalIgnoreCase));
                audit("Job", $"{group}.{name}", "Deleted", JobJson(ToDefinition(entity)), null);
            }
        });

    public Task DeleteJobAsync(string group, string name, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            var entity = await db.Jobs.FirstOrDefaultAsync(j => j.Group == group && j.Name == name, cancellationToken) ?? throw NotFound("Job", $"{group}.{name}");
            db.Jobs.Remove(entity);
            configuration.Jobs.RemoveAll(j => string.Equals(j.Key, $"{group}.{name}", StringComparison.OrdinalIgnoreCase));
            audit("Job", $"{group}.{name}", "Deleted", JobJson(ToDefinition(entity)), null);
        });

    /// <summary>
    /// Sets the secret of a connection (client secret or user password, by authentication mode)
    /// or of an SFTP server (password or key passphrase).
    /// </summary>
    public Task SetSecretAsync(SecretOwner owner, string name, string value, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            if (owner == SecretOwner.Connection)
            {
                var entity = await db.Connections.FirstOrDefaultAsync(c => c.Name == name, cancellationToken) ?? throw NotFound("Connection", name);
#pragma warning disable CS0618
                if (entity.AuthMode == D365AuthMode.UsernamePassword) entity.ProtectedUserPassword = secrets.Protect(value);
#pragma warning restore CS0618
                else entity.ProtectedClientSecret = secrets.Protect(value);
                Touch(entity, user);
                Replace(configuration.Connections, c => c.Name, ToOptions(entity));
            }
            else
            {
                var entity = await db.SftpServers.FirstOrDefaultAsync(s => s.Name == name, cancellationToken) ?? throw NotFound("SFTP server", name);
                if (entity.UseKey) entity.ProtectedKeyPassphrase = secrets.Protect(value);
                else entity.ProtectedPassword = secrets.Protect(value);
                Touch(entity, user);
                Replace(configuration.SftpServers, s => s.Name, ToOptions(entity));
            }
            audit(owner.ToString(), name, "SecretChanged", null, null);
        });

    /// <summary>Adds or updates everything from <paramref name="imported"/> (e.g. converted 4.x schedule). Placeholder secrets keep stored values.</summary>
    public Task ImportAsync(RisConfiguration imported, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            foreach (var connection in imported.Connections)
            {
                var entity = await db.Connections.FirstOrDefaultAsync(c => c.Name == connection.Name, cancellationToken);
                if (entity is null)
                {
                    entity = new ConnectionEntity { Name = connection.Name, AosUri = "", Tenant = "", Authority = "", ClientId = "" };
                    db.Connections.Add(entity);
                }
                Apply(entity, connection);
                Replace(configuration.Connections, c => c.Name, ToOptions(entity));
            }

            foreach (var server in imported.SftpServers)
            {
                var entity = await db.SftpServers.FirstOrDefaultAsync(s => s.Name == server.Name, cancellationToken);
                if (entity is null)
                {
                    entity = new SftpServerEntity { Name = server.Name, Host = "", UserName = "" };
                    db.SftpServers.Add(entity);
                }
                Apply(entity, server);
                Replace(configuration.SftpServers, s => s.Name, ToOptions(entity));
            }

            foreach (var job in imported.Jobs)
            {
                var entity = await db.Jobs.FirstOrDefaultAsync(j => j.Group == job.Group && j.Name == job.Name, cancellationToken);
                if (entity is null)
                {
                    entity = new JobDefinitionEntity { Name = job.Name, Group = job.Group, ScheduleJson = "", OptionsJson = "", Hash = "" };
                    db.Jobs.Add(entity);
                }
                Apply(entity, job);
                Replace(configuration.Jobs, j => j.Key, ToDefinition(entity));
            }

            audit("Configuration", "*", "Imported", null,
                $"{imported.Connections.Count} connection(s), {imported.SftpServers.Count} SFTP server(s), {imported.Jobs.Count} job(s)");
        });

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(int take = 200, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Audit.AsNoTracking().OrderByDescending(a => a.Id).Take(take).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// One page of the audit, newest first, filtered in the database (user, object, name, action, before/after JSON);
    /// <paramref name="total"/> counts all matching entries.
    /// </summary>
    public async Task<(IReadOnlyList<AuditEntry> Items, int Total)> QueryAuditAsync(int skip, int take, string? search, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var query = db.Audit.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(a => EF.Functions.Like(a.User ?? "", pattern, "\\") || EF.Functions.Like(a.EntityType, pattern, "\\")
                                     || EF.Functions.Like(a.EntityKey, pattern, "\\") || EF.Functions.Like(a.Action, pattern, "\\")
                                     || EF.Functions.Like(a.Before ?? "", pattern, "\\") || EF.Functions.Like(a.After ?? "", pattern, "\\"));
        }
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(a => a.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 50_000)).ToListAsync(cancellationToken);
        return (items, total);
    }

    /// <summary>Number of audit entries and the oldest one (maintenance page).</summary>
    public async Task<(int Count, DateTime? OldestUtc)> GetAuditStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var count = await db.Audit.CountAsync(cancellationToken);
        var oldest = count == 0 ? (DateTime?)null : await db.Audit.MinAsync(a => a.TimestampUtc, cancellationToken);
        return (count, oldest);
    }

    /// <summary>
    /// Deletes audit entries older than <paramref name="olderThanUtc"/>. The newest entry always stays: its id is the
    /// configuration version (<see cref="GetVersionAsync"/>) and must never go back.
    /// </summary>
    public async Task<int> PurgeAuditAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var newest = await db.Audit.MaxAsync(a => (long?)a.Id, cancellationToken) ?? 0;
        return await db.Audit.Where(a => a.TimestampUtc < olderThanUtc && a.Id < newest).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Returns the space of deleted rows to the file system (after a large purge).</summary>
    public async Task VacuumAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("VACUUM", cancellationToken);
    }

    /// <summary>Changes counter: id of the last audit entry (every change is audited). Lets a running service notice changes made by another process (ris db import).</summary>
    public async Task<long> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Audit.MaxAsync(a => (long?)a.Id, cancellationToken) ?? 0;
    }

    public static string ComputeHash(JobDefinition job) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(job.Schedule, RisJson.Options) + "\n" + JsonSerializer.Serialize(job.Options, RisJson.Options))));

    private delegate void AuditWriter(string entityType, string key, string action, string? before, string? after);

    private async Task ChangeAsync(string? user, CancellationToken cancellationToken, Func<RisDbContext, RisConfiguration, AuditWriter, Task> change)
    {
        var configuration = await LoadAsync(cancellationToken);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var audits = new List<AuditEntry>();
        await change(db, configuration, (type, key, action, before, after) => audits.Add(new AuditEntry
        {
            TimestampUtc = time.GetUtcNow().UtcDateTime,
            User = user,
            EntityType = type,
            EntityKey = key,
            Action = action,
            Before = before,
            After = after
        }));

        var errors = ConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }

        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            switch (entry.Entity)
            {
                case ConnectionEntity c: Touch(c, user); break;
                case SftpServerEntity s: Touch(s, user); break;
                case JobDefinitionEntity j: j.UpdatedUtc = time.GetUtcNow().UtcDateTime; j.UpdatedBy = user; break;
            }
        }

        db.Audit.AddRange(audits);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Committed?.Invoke(configuration);
        Changed?.Invoke();
    }

    private void Touch(ConnectionEntity entity, string? user) { entity.UpdatedUtc = time.GetUtcNow().UtcDateTime; entity.UpdatedBy = user; }

    private void Touch(SftpServerEntity entity, string? user) { entity.UpdatedUtc = time.GetUtcNow().UtcDateTime; entity.UpdatedBy = user; }

    private void Apply(ConnectionEntity entity, D365ConnectionOptions options)
    {
        entity.Name = options.Name;
        entity.AosUri = options.AosUri?.ToString() ?? string.Empty;
        entity.Tenant = options.Tenant;
        entity.Authority = options.Authority.ToString();
        entity.ClientId = options.ClientId;
        entity.AuthMode = options.AuthMode;
        // Only the credentials of the chosen mode are kept: switching mode removes the others (also stored secrets).
#pragma warning disable CS0618 // UsernamePassword is obsolete but still supported
        entity.CertificateThumbprint = options.AuthMode == D365AuthMode.Certificate ? options.CertificateThumbprint : null;
        entity.UserName = options.AuthMode == D365AuthMode.UsernamePassword ? options.UserName : null;
        if (options.AuthMode != D365AuthMode.ClientSecret) entity.ProtectedClientSecret = null;
        else if (!SecretPlaceholder.IsPlaceholderOrEmpty(options.ClientSecret)) entity.ProtectedClientSecret = secrets.Protect(options.ClientSecret);
        if (options.AuthMode != D365AuthMode.UsernamePassword) entity.ProtectedUserPassword = null;
        else if (!SecretPlaceholder.IsPlaceholderOrEmpty(options.UserPassword)) entity.ProtectedUserPassword = secrets.Protect(options.UserPassword);
#pragma warning restore CS0618
    }

    private void Apply(SftpServerEntity entity, SftpServerOptions options)
    {
        entity.Name = options.Name;
        entity.Host = options.Host;
        entity.Port = options.Port;
        entity.UserName = options.UserName;
        entity.UseKey = options.UseKey;
        entity.KeyPath = options.UseKey ? options.KeyPath : null;
        entity.ConnectTimeoutSeconds = (int)options.ConnectTimeout.TotalSeconds;
        entity.OperationTimeoutSeconds = (int)options.OperationTimeout.TotalSeconds;
        // Password or key, not both: switching removes the stored secret of the other method.
        if (options.UseKey) entity.ProtectedPassword = null;
        else if (!SecretPlaceholder.IsPlaceholderOrEmpty(options.Password)) entity.ProtectedPassword = secrets.Protect(options.Password);
        if (!options.UseKey) entity.ProtectedKeyPassphrase = null;
        else if (!SecretPlaceholder.IsPlaceholderOrEmpty(options.KeyPassphrase)) entity.ProtectedKeyPassphrase = secrets.Protect(options.KeyPassphrase);
    }

    private static void Apply(JobDefinitionEntity entity, JobDefinition job)
    {
        entity.Name = job.Name;
        entity.Group = job.Group;
        entity.Description = job.Description;
        entity.ScheduleJson = JsonSerializer.Serialize(job.Schedule, RisJson.Options);
        entity.OptionsJson = JsonSerializer.Serialize(job.Options, RisJson.Options);
        entity.PairedJob = job.PairedJob;
        entity.Hash = ComputeHash(job);
    }

    private D365ConnectionOptions ToOptions(ConnectionEntity entity) => new()
    {
        Name = entity.Name,
        AosUri = Uri.TryCreate(entity.AosUri, UriKind.Absolute, out var aos) ? aos : null,
        Tenant = entity.Tenant,
        Authority = new Uri(entity.Authority),
        ClientId = entity.ClientId,
        AuthMode = entity.AuthMode,
        CertificateThumbprint = entity.CertificateThumbprint,
        UserName = entity.UserName,
        // A required secret that was not set yet (e.g. after importing a converted 4.x schedule) is a placeholder:
        // the configuration is valid, the job fails with "secret not set" until ris db set-secret / the API sets it.
#pragma warning disable CS0618
        ClientSecret = secrets.Unprotect(entity.ProtectedClientSecret, $"connection {entity.Name} client secret")
                       ?? (entity.AuthMode == D365AuthMode.ClientSecret ? SecretPlaceholder.Value : null),
        UserPassword = secrets.Unprotect(entity.ProtectedUserPassword, $"connection {entity.Name} user password")
                       ?? (entity.AuthMode == D365AuthMode.UsernamePassword ? SecretPlaceholder.Value : null)
#pragma warning restore CS0618
    };

    private SftpServerOptions ToOptions(SftpServerEntity entity) => new()
    {
        Name = entity.Name,
        Host = entity.Host,
        Port = entity.Port,
        UserName = entity.UserName,
        UseKey = entity.UseKey,
        KeyPath = entity.KeyPath,
        ConnectTimeout = TimeSpan.FromSeconds(entity.ConnectTimeoutSeconds),
        OperationTimeout = TimeSpan.FromSeconds(entity.OperationTimeoutSeconds),
        Password = secrets.Unprotect(entity.ProtectedPassword, $"SFTP server {entity.Name} password") ?? (entity.UseKey ? null : SecretPlaceholder.Value),
        KeyPassphrase = secrets.Unprotect(entity.ProtectedKeyPassphrase, $"SFTP server {entity.Name} key passphrase")
    };

    private static JobDefinition ToDefinition(JobDefinitionEntity entity) => new()
    {
        Name = entity.Name,
        Group = entity.Group,
        Description = entity.Description,
        Schedule = JsonSerializer.Deserialize<ScheduleOptions>(entity.ScheduleJson, RisJson.Options)!,
        Options = JsonSerializer.Deserialize<JobOptionsBase>(entity.OptionsJson, RisJson.Options),
        PairedJob = entity.PairedJob
    };

    public static D365ConnectionOptions WithoutSecrets(D365ConnectionOptions c) => new()
    {
        Name = c.Name, AosUri = c.AosUri, Tenant = c.Tenant, Authority = c.Authority, ClientId = c.ClientId, AuthMode = c.AuthMode,
        CertificateThumbprint = c.CertificateThumbprint, UserName = c.UserName,
        ClientSecret = string.IsNullOrEmpty(c.ClientSecret) ? null : SecretPlaceholder.Value,
        UserPassword = string.IsNullOrEmpty(c.UserPassword) ? null : SecretPlaceholder.Value
    };

    public static SftpServerOptions WithoutSecrets(SftpServerOptions s) => new()
    {
        Name = s.Name, Host = s.Host, Port = s.Port, UserName = s.UserName, UseKey = s.UseKey, KeyPath = s.KeyPath,
        ConnectTimeout = s.ConnectTimeout, OperationTimeout = s.OperationTimeout,
        Password = string.IsNullOrEmpty(s.Password) ? null : SecretPlaceholder.Value,
        KeyPassphrase = string.IsNullOrEmpty(s.KeyPassphrase) ? null : SecretPlaceholder.Value
    };

    /// <summary>Audit JSON: readable (no < escapes, national characters as they are).</summary>
    private static readonly JsonSerializerOptions AuditJsonOptions = new(RisJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Secret in audit entries: never the value, only whether one is stored and whether this change set it.</summary>
    public const string AuditSecretStored = "***";
    public const string AuditSecretChanged = "*** (new)";

    private static string? AuditSecret(string? stored, string?[]? previous, int index) =>
        stored is null ? null : previous is not null && previous[index] != stored ? AuditSecretChanged : AuditSecretStored;

    /// <summary>Connection as stored; with <paramref name="previous"/> (encrypted secrets before the change) changed secrets are marked.</summary>
    private string AuditJson(ConnectionEntity entity, string?[]? previous = null)
    {
        var options = ToOptions(entity);
        options.ClientSecret = AuditSecret(entity.ProtectedClientSecret, previous, 0);
        options.UserPassword = AuditSecret(entity.ProtectedUserPassword, previous, 1);
        return JsonSerializer.Serialize(options, AuditJsonOptions);
    }

    /// <inheritdoc cref="AuditJson(ConnectionEntity, string?[])"/>
    private string AuditJson(SftpServerEntity entity, string?[]? previous = null)
    {
        var options = ToOptions(entity);
        options.Password = AuditSecret(entity.ProtectedPassword, previous, 0);
        options.KeyPassphrase = AuditSecret(entity.ProtectedKeyPassphrase, previous, 1);
        return JsonSerializer.Serialize(options, AuditJsonOptions);
    }

    private static string JobJson(JobDefinition job) => JsonSerializer.Serialize(new { job.Schedule, job.Options, job.Description, job.PairedJob }, AuditJsonOptions);

    private static void Replace<T>(List<T> list, Func<T, string> key, T item)
    {
        var index = list.FindIndex(x => string.Equals(key(x), key(item), StringComparison.OrdinalIgnoreCase));
        if (index >= 0) list[index] = item;
        else list.Add(item);
    }

    private static ConfigurationNotFoundException NotFound(string what, string name) => new(what, name);
}
