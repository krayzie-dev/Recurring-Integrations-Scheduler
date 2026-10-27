using System.Text.Json.Serialization;
using RIS.Core.Statuses;

namespace RIS.Core.Contracts;

/// <summary>
/// Status of a local file in the processing chain. Values match 4.x status files.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MessageStatus>))]
public enum MessageStatus
{
    Input,
    InProcess,
    Enqueued,
    Failed,
    Succeeded
}

/// <summary>
/// Content of a status file (e.g. "orders.Status") written next to a submitted file.
/// Property names and values are compatible with RIS 4.x, so 4.x and 5.0 can share folders during migration.
/// </summary>
public sealed class DataMessage
{
    public string? Name { get; set; }

    public string? FullPath { get; set; }

    /// <summary>D365 execution id (Package API) or message id (Recurring Integrations).</summary>
    public string? MessageId { get; set; }

    public MessageStatus MessageStatus { get; set; }

    public DataJobState DataJobState { get; set; }

    public string? CorrelationId { get; set; }

    public string? PopReceipt { get; set; }

    public string? DownloadLocation { get; set; }

    /// <summary>Time of submission to D365. Missing in status files written by 4.x before 4.2.4.</summary>
    public DateTime? SubmittedUtc { get; set; }

    public DataMessage Clone() => (DataMessage)MemberwiseClone();
}
