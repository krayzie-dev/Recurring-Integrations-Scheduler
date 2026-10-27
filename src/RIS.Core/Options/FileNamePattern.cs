using System.Globalization;
using System.Text.RegularExpressions;

namespace RIS.Core.Options;

/// <summary>
/// File name pattern of downloaded packages, e.g. "{timestamp}" (4.x) or "{timestamp}_{executionId}".
/// ".zip" is added automatically; characters not allowed in file names are replaced by "_".
/// </summary>
public static partial class FileNamePattern
{
    /// <summary>4.x name of exported packages.</summary>
    public const string ExportDefault = "{timestamp}";

    /// <summary>4.x name of downloaded messages.</summary>
    public const string DownloadDefault = "{timestamp}-{number}";

    /// <summary>Tokens available for Export packages.</summary>
    public static readonly IReadOnlyCollection<string> ExportTokens = ["timestamp", "date", "executionId", "dataProject", "company"];

    /// <summary>Tokens available for Download messages.</summary>
    public static readonly IReadOnlyCollection<string> DownloadTokens = ["timestamp", "date", "number", "correlationId", "activityId"];

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Token();

    /// <summary>Tokens used in the pattern that are not in <paramref name="allowed"/>.</summary>
    public static IReadOnlyList<string> UnknownTokens(string pattern, IReadOnlyCollection<string> allowed) =>
        Token().Matches(pattern).Select(m => m.Groups[1].Value)
            .Where(t => !allowed.Contains(t, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();

    public static string Format(string pattern, DateTime now, IReadOnlyDictionary<string, string?> values)
    {
        var name = Token().Replace(pattern, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "timestamp" => now.ToString("yyyy-MM-dd_HH-mm-ss-ffff", CultureInfo.InvariantCulture),
            "date" => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            var token => values.FirstOrDefault(v => string.Equals(v.Key, token, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty
        });

        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return (name.Length == 0 ? now.ToString("yyyy-MM-dd_HH-mm-ss-ffff", CultureInfo.InvariantCulture) : name) + ".zip";
    }
}
