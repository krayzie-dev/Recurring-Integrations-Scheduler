using System.Text.Json;
using System.Text.Json.Serialization;

namespace RIS.Core.Configuration;

/// <summary>
/// JSON settings for RIS configuration files (jobs.json).
/// </summary>
public static class RisJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // "type" of job options may come anywhere in the object (hand-written or generated JSON).
            AllowOutOfOrderMetadataProperties = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static RisConfiguration LoadConfiguration(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<RisConfiguration>(stream, Options)
               ?? throw new InvalidDataException($"Configuration file '{path}' is empty.");
    }

    public static void SaveConfiguration(RisConfiguration configuration, string path)
    {
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, configuration, Options);
    }
}
