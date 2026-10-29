using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using RIS.Core.Options;

namespace RIS.Persistence;

/// <summary>
/// D365 instance. Its sign-in is the app registration <see cref="AppRegistration"/>; databases from before app registrations
/// have the sign-in in the instance itself (moved to app registrations at startup, then these columns are empty).
/// </summary>
public sealed class ConnectionEntity
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string AosUri { get; set; }
    public string? AppRegistration { get; set; }
    public required string Tenant { get; set; }
    public required string Authority { get; set; }
    public required string ClientId { get; set; }
    public D365AuthMode AuthMode { get; set; }
    public string? CertificateThumbprint { get; set; }
    public string? UserName { get; set; }
    public string? ProtectedClientSecret { get; set; }
    public string? ProtectedUserPassword { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Entra ID app registration with encrypted secrets (Data Protection).</summary>
public sealed class AppRegistrationEntity
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Tenant { get; set; }
    public required string Authority { get; set; }
    public required string ClientId { get; set; }
    public D365AuthMode AuthMode { get; set; }
    public string? CertificateThumbprint { get; set; }
    public string? UserName { get; set; }
    public string? ProtectedClientSecret { get; set; }
    public string? ProtectedUserPassword { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>SFTP server with encrypted credentials.</summary>
public sealed class SftpServerEntity
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; }
    public required string UserName { get; set; }
    public bool UseKey { get; set; }
    public string? KeyPath { get; set; }
    public string? ProtectedPassword { get; set; }
    public string? ProtectedKeyPassphrase { get; set; }
    public int ConnectTimeoutSeconds { get; set; }
    public int OperationTimeoutSeconds { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Job definition: schedule and options as JSON (RisJson format), hash used to detect changes for the scheduler.</summary>
public sealed class JobDefinitionEntity
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Group { get; set; }
    public string? Description { get; set; }
    public required string ScheduleJson { get; set; }
    public required string OptionsJson { get; set; }
    public string? PairedJob { get; set; }
    public required string Hash { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Configuration change. Never contains secrets.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string? User { get; set; }
    public required string EntityType { get; set; }
    public required string EntityKey { get; set; }
    public required string Action { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
}

/// <summary>
/// Windows group or user given a RIS role in the UI (page "Access") or with "ris access". Roles from the settings file
/// (Ris:Security:Roles, written by the installer) are not stored here and cannot be removed in the UI.
/// </summary>
public sealed class RoleMemberEntity
{
    public int Id { get; set; }
    public required string Role { get; set; }
    /// <summary>Account as entered and resolved, e.g. CONTOSO\RIS-Operators, or a SID.</summary>
    public required string Member { get; set; }
    public required string Sid { get; set; }
    public DateTime AddedUtc { get; set; }
    public string? AddedBy { get; set; }
}

/// <summary>Application setting changed in the UI or CLI (e.g. the idle guard), JSON value; changes are audited.</summary>
public sealed class SettingEntity
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Last run of a job that did real work (result Succeeded; Quartz counts "Skipped" as success too, and its history keeps only
/// the newest runs). Read by the idle guard. Written only on success, so a job that skips every 3 minutes writes nothing.
/// </summary>
public sealed class JobActivityEntity
{
    public int Id { get; set; }
    public required string Group { get; set; }
    public required string Name { get; set; }
    public DateTime LastWorkUtc { get; set; }
    public string? LastWorkSummary { get; set; }
}

public sealed class RisDbContext(DbContextOptions<RisDbContext> options) : DbContext(options)
{
    public DbSet<AppRegistrationEntity> AppRegistrations => Set<AppRegistrationEntity>();
    public DbSet<ConnectionEntity> Connections => Set<ConnectionEntity>();
    public DbSet<SftpServerEntity> SftpServers => Set<SftpServerEntity>();
    public DbSet<JobDefinitionEntity> Jobs => Set<JobDefinitionEntity>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<RoleMemberEntity> RoleMembers => Set<RoleMemberEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<JobActivityEntity> JobActivity => Set<JobActivityEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConnectionEntity>(e =>
        {
            e.ToTable("Connections");
            e.HasIndex(c => c.Name).IsUnique();
            e.Property(c => c.Name).UseCollation("NOCASE");
            e.Property(c => c.AuthMode).HasConversion<string>();
        });
        modelBuilder.Entity<AppRegistrationEntity>(e =>
        {
            e.ToTable("AppRegistrations");
            e.HasIndex(a => a.Name).IsUnique();
            e.Property(a => a.Name).UseCollation("NOCASE");
            e.Property(a => a.AuthMode).HasConversion<string>();
        });
        modelBuilder.Entity<SftpServerEntity>(e =>
        {
            e.ToTable("SftpServers");
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.Name).UseCollation("NOCASE");
        });
        modelBuilder.Entity<JobDefinitionEntity>(e =>
        {
            e.ToTable("Jobs");
            e.HasIndex(j => new { j.Group, j.Name }).IsUnique();
            e.Property(j => j.Name).UseCollation("NOCASE");
            e.Property(j => j.Group).UseCollation("NOCASE");
        });
        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("Audit");
            e.HasIndex(a => a.TimestampUtc);
        });
        modelBuilder.Entity<RoleMemberEntity>(e =>
        {
            e.ToTable("RoleMembers");
            e.HasIndex(m => new { m.Role, m.Sid }).IsUnique();
            e.Property(m => m.Role).UseCollation("NOCASE");
            e.Property(m => m.Sid).UseCollation("NOCASE");
            e.Property(m => m.Member).UseCollation("NOCASE");
        });
        modelBuilder.Entity<SettingEntity>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).UseCollation("NOCASE");
        });
        modelBuilder.Entity<JobActivityEntity>(e =>
        {
            e.ToTable("JobActivity");
            e.HasIndex(a => new { a.Group, a.Name }).IsUnique();
            e.Property(a => a.Name).UseCollation("NOCASE");
            e.Property(a => a.Group).UseCollation("NOCASE");
        });
    }
}

/// <summary>Used by "dotnet ef migrations add".</summary>
public sealed class DesignTimeRisDbContextFactory : IDesignTimeDbContextFactory<RisDbContext>
{
    public RisDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RisDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
