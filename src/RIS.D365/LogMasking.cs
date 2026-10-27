using System.Text.RegularExpressions;

namespace RIS.D365;

/// <summary>
/// Removes secrets from values written to logs (SAS signatures, tokens).
/// </summary>
public static partial class LogMasking
{
    public const int MaxBodyLength = 8000;

    [GeneratedRegex(@"(?i)(sig|signature|code|access_token|client_secret)=[^&\s""]+")]
    private static partial Regex SecretQueryParameter();

    public static string MaskUrl(Uri? uri) => uri is null ? string.Empty : MaskText(uri.ToString());

    public static string MaskText(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : SecretQueryParameter().Replace(text, m => m.Value[..(m.Value.IndexOf('=') + 1)] + "***");

    public static string Truncate(string? text, int maxLength = MaxBodyLength)
    {
        var masked = MaskText(text);
        return masked.Length <= maxLength ? masked : masked[..maxLength] + $"... [truncated, total length {masked.Length}]";
    }
}
