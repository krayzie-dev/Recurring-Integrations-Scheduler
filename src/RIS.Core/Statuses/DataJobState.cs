using System.Text.Json;
using System.Text.Json.Serialization;

namespace RIS.Core.Statuses;

/// <summary>
/// State of a Recurring Integrations message (jobstatus API). Values match 4.x and D365.
/// </summary>
[JsonConverter(typeof(DataJobStateJsonConverter))]
public enum DataJobState
{
    Enqueued = 0,
    Dequeued = 1,
    Downloaded = 2,
    Acked = 3,
    InProcess = 4,
    Processed = 5,
    ProcessedWithErrors = 6,
    PostProcessError = 7,
    PreProcessError = 8,
    PreProcessing = 9,

    /// <summary>Value not known to RIS. 4.x failed the whole run on such values.</summary>
    Unrecognized = 99
}

public static class DataJobStateExtensions
{
    public static bool IsSuccess(this DataJobState state) => state == DataJobState.Processed;

    public static bool IsFailure(this DataJobState state) =>
        state is DataJobState.ProcessedWithErrors or DataJobState.PostProcessError or DataJobState.PreProcessError;

    public static bool IsInProgress(this DataJobState state) => !state.IsSuccess() && !state.IsFailure();

    public static DataJobState Parse(string? value)
    {
        var text = value?.Trim();
        // D365 uses both "PostProcessError" and "PostProcessingError" spellings in different APIs.
        text = text switch
        {
            "PostProcessingError" => nameof(DataJobState.PostProcessError),
            "PreProcessingError" => nameof(DataJobState.PreProcessError),
            "Processing" => nameof(DataJobState.InProcess),
            _ => text
        };

        if (int.TryParse(text, out var number))
        {
            return Enum.IsDefined(typeof(DataJobState), number) ? (DataJobState)number : DataJobState.Unrecognized;
        }

        return Enum.TryParse<DataJobState>(text, ignoreCase: true, out var state) ? state : DataJobState.Unrecognized;
    }
}

/// <summary>Reads state as text or number, never throws on unknown values.</summary>
public sealed class DataJobStateJsonConverter : JsonConverter<DataJobState>
{
    public override DataJobState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => DataJobStateExtensions.Parse(reader.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            JsonTokenType.String => DataJobStateExtensions.Parse(reader.GetString()),
            _ => DataJobState.Unrecognized
        };

    public override void Write(Utf8JsonWriter writer, DataJobState value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
