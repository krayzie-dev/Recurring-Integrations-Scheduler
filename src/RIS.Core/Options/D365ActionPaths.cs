namespace RIS.Core.Options;

/// <summary>
/// Relative paths of D365 Package API OData actions. Can be overridden per job (4.x "Custom OData actions").
/// </summary>
public sealed class D365ActionPaths
{
    private const string Prefix = "data/DataManagementDefinitionGroups/Microsoft.Dynamics.DataEntities.";

    public string GetAzureWriteUrl { get; set; } = Prefix + "GetAzureWriteUrl";
    public string GetExecutionSummaryStatus { get; set; } = Prefix + "GetExecutionSummaryStatus";
    public string GetExportedPackageUrl { get; set; } = Prefix + "GetExportedPackageUrl";
    public string GetExecutionSummaryPageUrl { get; set; } = Prefix + "GetExecutionSummaryPageUrl";
    public string ImportFromPackage { get; set; } = Prefix + "ImportFromPackage";
    public string DeleteExecutionHistoryJob { get; set; } = Prefix + "DeleteExecutionHistoryJob";
    public string ExportToPackage { get; set; } = Prefix + "ExportToPackage";
    public string ExportFromPackage { get; set; } = Prefix + "ExportFromPackage";
    public string GetMessageStatus { get; set; } = Prefix + "GetMessageStatus";
    public string GetImportTargetErrorKeysFileUrl { get; set; } = Prefix + "GetImportTargetErrorKeysFileUrl";
    public string GenerateImportTargetErrorKeysFile { get; set; } = Prefix + "GenerateImportTargetErrorKeysFile";
    public string GetExecutionErrors { get; set; } = Prefix + "GetExecutionErrors";

    /// <summary>Recurring Integrations connector API paths.</summary>
    public const string EnqueuePath = "api/connector/enqueue/";
    public const string DequeuePath = "api/connector/dequeue/";
    public const string AckPath = "api/connector/ack/";
    public const string JobStatusPath = "api/connector/jobstatus/";
}
