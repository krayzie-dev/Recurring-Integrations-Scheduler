using System.Text.Encodings.Web;
using System.Text.Json;
using RIS.Core.Configuration;
using RIS.Persistence;

namespace RIS.Server.Services;

public enum AuditChangeKind { Same, Changed, Added, Removed }

/// <summary>One field of an audited object, e.g. "options.polling.timeout": "12:00:00" → "06:00:00".</summary>
public sealed record AuditChange(string Path, string? Before, string? After, AuditChangeKind Kind);

/// <summary>Compares the JSON before and after a change field by field (nested objects flattened to paths).</summary>
public static class AuditDiff
{
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Fields of both versions in the order of the newer one; null when the entry is not JSON (e.g. a moved job: new key).
    /// Older entries mark secrets with the export placeholder; they are shown like current entries ("***").
    /// </summary>
    public static IReadOnlyList<AuditChange>? Compare(string? before, string? after)
    {
        var old = Flatten(before);
        var current = Flatten(after);
        if (old is null && before is not null || current is null && after is not null) return null;
        old ??= [];
        current ??= [];

        var result = new List<AuditChange>();
        foreach (var (path, value) in current)
        {
            if (!old.TryGetValue(path, out var previous)) result.Add(new AuditChange(path, null, value, AuditChangeKind.Added));
            else result.Add(new AuditChange(path, previous, value, previous == value ? AuditChangeKind.Same : AuditChangeKind.Changed));
        }
        result.AddRange(old.Where(o => !current.ContainsKey(o.Key)).Select(o => new AuditChange(o.Key, o.Value, null, AuditChangeKind.Removed)));
        return result;
    }

    /// <summary>JSON indented and readable (no < escapes), or the text as it is.</summary>
    public static string Pretty(string? json)
    {
        if (string.IsNullOrEmpty(json)) return "–";
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions(Readable) { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static Dictionary<string, string>? Flatten(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            Add(document.RootElement, "", fields);
            return fields;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Add(JsonElement element, string path, Dictionary<string, string> fields)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) Add(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", fields);
                break;
            case JsonValueKind.Null:
                break;
            case JsonValueKind.String:
                var text = element.GetString() ?? "";
                fields[path] = text == SecretPlaceholder.Value ? ConfigurationRepository.AuditSecretStored : text;
                break;
            case JsonValueKind.Array:
                fields[path] = JsonSerializer.Serialize(element, Readable);
                break;
            default:
                fields[path] = element.GetRawText();
                break;
        }
    }
}
