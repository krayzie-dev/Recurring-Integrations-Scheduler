using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using RIS.Core.Options;

namespace RIS.Core.Configuration;

/// <summary>
/// Validates whole RIS configuration (DataAnnotations + IValidatableObject, recursively) and cross references.
/// </summary>
public static class ConfigurationValidator
{
    public static IReadOnlyList<string> Validate(RisConfiguration configuration)
    {
        var errors = new List<string>();

        var connectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in configuration.Connections)
        {
            ValidateObject(connection, $"Connections[{connection.Name}]", errors);
            if (!connectionNames.Add(connection.Name))
            {
                errors.Add($"Connections[{connection.Name}]: duplicate connection name.");
            }
        }

        var jobKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in configuration.Jobs)
        {
            var path = $"Jobs[{job.Key}]";
            ValidateObject(job, path, errors);
            if (!jobKeys.Add(job.Key))
            {
                errors.Add($"{path}: duplicate job name in group.");
            }

            if (job.Options is { } options && !connectionNames.Contains(options.Connection))
            {
                errors.Add($"{path}.Options.Connection: connection '{options.Connection}' is not defined.");
            }
        }

        foreach (var job in configuration.Jobs.Where(j => j.PairedJob is not null))
        {
            if (!configuration.Jobs.Any(j => string.Equals(j.Name, job.PairedJob, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Jobs[{job.Key}].PairedJob: job '{job.PairedJob}' is not defined.");
            }
        }

        return errors;
    }

    /// <summary>Validates one object graph (used also for a single job before it runs).</summary>
    public static IReadOnlyList<string> ValidateObject(object instance, string path)
    {
        var errors = new List<string>();
        ValidateObject(instance, path, errors);
        return errors;
    }

    private static void ValidateObject(object instance, string path, List<string> errors)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        foreach (var result in results)
        {
            var members = string.Join(",", result.MemberNames);
            errors.Add($"{path}{(members.Length > 0 ? "." + members : string.Empty)}: {result.ErrorMessage}");
        }

        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !IsNested(property.PropertyType))
            {
                continue;
            }

            var value = property.GetValue(instance);
            switch (value)
            {
                case null:
                    continue;
                case string:
                    continue;
                case IEnumerable items:
                    var index = 0;
                    foreach (var item in items)
                    {
                        if (item is not null && IsNested(item.GetType()))
                        {
                            ValidateObject(item, $"{path}.{property.Name}[{index}]", errors);
                        }
                        index++;
                    }
                    break;
                default:
                    ValidateObject(value, $"{path}.{property.Name}", errors);
                    break;
            }
        }
    }

    private static bool IsNested(Type type) =>
        type.Namespace?.StartsWith("RIS.", StringComparison.Ordinal) == true && !type.IsEnum
        || (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string));
}
