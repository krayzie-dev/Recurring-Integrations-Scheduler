using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RIS.Core.Configuration;

namespace RIS.Persistence;

/// <summary>Where RIS keeps its databases, keys and backups.</summary>
public sealed class PersistenceOptions
{
    /// <summary>Default: %ProgramData%\RIS5.</summary>
    public string DataDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RIS5");

    /// <summary>Thumbprint of a certificate protecting Data Protection keys (LocalMachine\My). Empty: DPAPI of this machine.</summary>
    public string? KeyCertificateThumbprint { get; set; }

    /// <summary>Number of database backups kept in the backup folder.</summary>
    public int BackupsToKeep { get; set; } = 10;

    public string RisDatabase => Path.Combine(DataDirectory, "ris.db");
    public string QuartzDatabase => Path.Combine(DataDirectory, "quartz.db");
    public string KeysDirectory => Path.Combine(DataDirectory, "keys");
    public string BackupDirectory => Path.Combine(DataDirectory, "backup");

    public string RisConnectionString => new SqliteConnectionStringBuilder { DataSource = RisDatabase, Pooling = true }.ToString();
    public string QuartzConnectionString => new SqliteConnectionStringBuilder { DataSource = QuartzDatabase, Pooling = true }.ToString();
}

/// <summary>
/// Current configuration from the database, cached and refreshed after every change.
/// </summary>
public sealed class DbConfigurationStore : IRisConfigurationStore
{
    private readonly ConfigurationRepository _repository;
    private volatile RisConfiguration _current = new();

    public DbConfigurationStore(ConfigurationRepository repository)
    {
        _repository = repository;
        _repository.Changed += () => Refresh();
    }

    public RisConfiguration Current => _current;

    /// <summary>Raised after the cached configuration was reloaded.</summary>
    public event Action? Reloaded;

    public void Refresh()
    {
        _current = _repository.LoadAsync().GetAwaiter().GetResult();
        Reloaded?.Invoke();
    }
}

public static class DatabaseMaintenance
{
    /// <summary>Copies a SQLite database to the backup folder (VACUUM INTO) and keeps the newest <paramref name="keep"/> copies.</summary>
    public static async Task<string?> BackupAsync(string databasePath, string backupDirectory, int keep, string reason, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath) || new FileInfo(databasePath).Length == 0)
        {
            return null;
        }

        Directory.CreateDirectory(backupDirectory);
        var name = Path.GetFileNameWithoutExtension(databasePath);
        var target = Path.Combine(backupDirectory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $target";
            command.Parameters.AddWithValue("$target", target);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var old in new DirectoryInfo(backupDirectory).GetFiles($"{name}-*.db").OrderByDescending(f => f.Name).Skip(keep))
        {
            old.Delete();
        }
        return target;
    }

    /// <summary>Applies pending EF migrations of ris.db, with a backup first.</summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<PersistenceOptions>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseMaintenance));
        Directory.CreateDirectory(options.DataDirectory);

        await using var db = await services.GetRequiredService<IDbContextFactory<RisDbContext>>().CreateDbContextAsync(cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var backup = await BackupAsync(options.RisDatabase, options.BackupDirectory, options.BackupsToKeep, "before-migration", cancellationToken);
        logger.LogInformation("Applying {Count} database migration(s) to {Database}: {Migrations}. Backup: {Backup}",
            pending.Count, options.RisDatabase, pending, backup ?? "none (new database)");
        await db.Database.MigrateAsync(cancellationToken);
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRisPersistence(this IServiceCollection services, PersistenceOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        services.AddSingleton(options);
        services.AddDbContextFactory<RisDbContext>(db => db.UseSqlite(options.RisConnectionString));

        var dataProtection = services.AddDataProtection()
            .SetApplicationName("RIS5")
            .PersistKeysToFileSystem(new DirectoryInfo(options.KeysDirectory));
        if (!string.IsNullOrWhiteSpace(options.KeyCertificateThumbprint))
        {
            dataProtection.ProtectKeysWithCertificate(Certificates.FindByThumbprint(options.KeyCertificateThumbprint));
        }
        else if (OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<ConfigurationRepository>();
        services.AddSingleton<DbConfigurationStore>();
        services.AddSingleton<IRisConfigurationStore>(sp => sp.GetRequiredService<DbConfigurationStore>());
        return services;
    }

}
