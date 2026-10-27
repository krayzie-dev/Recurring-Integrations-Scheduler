using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RIS.Persistence;

namespace RIS.Server;

/// <summary>Health settings (appsettings "Ris:Health").</summary>
public sealed class HealthOptions
{
    /// <summary>/health without authentication (monitoring agents). The report contains no configuration details.</summary>
    public bool AllowAnonymous { get; set; } = true;

    /// <summary>Jobs that must succeed regularly: key "Group.Name", value maximum age of the last success (e.g. "1.02:00:00").</summary>
    public Dictionary<string, TimeSpan> RequiredJobs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Both databases can be opened and the data directory is writable.</summary>
public sealed class DataHealthCheck(PersistenceOptions persistence) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var connectionString in new[] { persistence.RisConnectionString, persistence.QuartzConnectionString })
            {
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(cancellationToken);
            }

            var probe = Path.Combine(persistence.DataDirectory, ".health");
            await File.WriteAllTextAsync(probe, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            File.Delete(probe);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Unhealthy($"Data directory or database is not available: {ex.Message}");
        }
    }
}
