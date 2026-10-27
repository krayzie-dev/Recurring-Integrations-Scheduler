using System.Security.Cryptography;
using System.Text;
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
            var before = entity is null ? null : Json(WithoutSecrets(ToOptions(entity)));
            if (entity is null)
            {
                entity = new ConnectionEntity { Name = connection.Name, AosUri = "", Tenant = "", Authority = "", ClientId = "" };
                db.Connections.Add(entity);
            }

            Apply(entity, connection);
            Replace(configuration.Connections, c => c.Name, ToOptions(entity));
            audit("Connection", connection.Name, before is null ? "Created" : "Updated", before, Json(WithoutSecrets(connection)));
        });

    public Task SaveSftpServerAsync(SftpServerOptions server, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            var entity = await db.SftpServers.FirstOrDefaultAsync(s => s.Name == server.Name, cancellationToken);
            var before = entity is null ? null : Json(WithoutSecrets(ToOptions(entity)));
            if (entity is null)
            {
                entity = new SftpServerEntity { Name = server.Name, Host = "", UserName = "" };
                db.SftpServers.Add(entity);
            }

            Apply(entity, server);
            Replace(configuration.SftpServers, s => s.Name, ToOptions(entity));
            audit("SftpServer", server.Name, before is null ? "Created" : "Updated", before, Json(WithoutSecrets(server)));
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

    public Task DeleteConnectionAsync(string name, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            var entity = await db.Connections.FirstOrDefaultAsync(c => c.Name == name, cancellationToken) ?? throw NotFound("Connection", name);
            db.Connections.Remove(entity);
            configuration.Connections.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            audit("Connection", name, "Deleted", Json(WithoutSecrets(ToOptions(entity))), null);
        });

    public Task DeleteSftpServerAsync(string name, string? user, CancellationToken cancellationToken = default) =>
        ChangeAsync(user, cancellationToken, async (db, configuration, audit) =>
        {
            var entity = await db.SftpServers.FirstOrDefaultAsync(s => s.Name == name, cancellationToken) ?? throw NotFound("SFTP server", name);
            db.SftpServers.Remove(entity);
            configuration.SftpServers.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            audit("SftpServer", name, "Deleted", Json(WithoutSecrets(ToOptions(entity))), null);
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
        entity.CertificateThumbprint = options.CertificateThumbprint;
        entity.UserName = options.UserName;
        if (!SecretPlaceholder.IsPlaceholderOrEmpty(options.ClientSecret)) entity.ProtectedClientSecret = secrets.Protect(options.ClientSecret);
        if (!SecretPlaceholder.IsPlaceholderOrEmpty(options.UserPassword)) entity.ProtectedUserPassword = secrets.Protect(options.UserPassword);
    }

    private void Apply(SftpServerEntity entity, SftpServerOptions options)
    {
        entity.Name = options.Name;
        entity.Host = options.Host;
        entity.Port = options.Port;
        entity.UserName = options.UserName;
        entity.UseKey = options.UseKey;
        entity.KeyPath = options.KeyPath;
        entity.ConnectTimeoutSeconds = (int)options.ConnectTimeout.TotalSeconds;
        entity.OperationTimeoutSeconds = (int)options.OperationTimeout.TotalSeconds;
        if (!SecretPlaceholder.IsPlaceholderOrEmpty(options.Password)) entity.ProtectedPassword = secrets.Protect(options.Password);
        if (!SecretPlaceholder.IsPlaceholderOrEmpty(options.KeyPassphrase)) entity.ProtectedKeyPassphrase = secrets.Protect(options.KeyPassphrase);
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

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, RisJson.Options);

    private static string JobJson(JobDefinition job) => JsonSerializer.Serialize(new { job.Schedule, job.Options, job.Description, job.PairedJob }, RisJson.Options);

    private static void Replace<T>(List<T> list, Func<T, string> key, T item)
    {
        var index = list.FindIndex(x => string.Equals(key(x), key(item), StringComparison.OrdinalIgnoreCase));
        if (index >= 0) list[index] = item;
        else list.Add(item);
    }

    private static ConfigurationNotFoundException NotFound(string what, string name) => new(what, name);
}
