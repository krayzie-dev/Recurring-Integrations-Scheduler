namespace RIS.Core.Configuration;

/// <summary>
/// Source of the current RIS configuration (connections, SFTP servers, jobs): JSON file (CLI) or database (service).
/// </summary>
public interface IRisConfigurationStore
{
    RisConfiguration Current { get; }
}

public sealed class StaticConfigurationStore(RisConfiguration configuration) : IRisConfigurationStore
{
    public RisConfiguration Current { get; } = configuration;
}
