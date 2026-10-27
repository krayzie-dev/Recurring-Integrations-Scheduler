namespace RIS.Core.Configuration;

/// <summary>
/// Placeholder used instead of secrets in exported configuration and in converted 4.x schedules.
/// Importing a placeholder never overwrites a stored secret.
/// </summary>
public static class SecretPlaceholder
{
    public const string Value = "<SET-SECRET>";

    public static bool IsPlaceholderOrEmpty(string? value) => string.IsNullOrEmpty(value) || value == Value;
}
