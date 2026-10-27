using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RIS.Core.Configuration;
using RIS.Core.Options;
using RIS.Core.Polling;
using RIS.Core.Statuses;
using RIS.D365;
using RIS.Transfer;

namespace RIS.Jobs.Tests;

public sealed class FakeClock : IPollingClock
{
    public DateTime UtcNow { get; set; } = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
    public List<TimeSpan> Delays { get; } = [];
    public Action<int>? OnDelay { get; set; }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        UtcNow += delay;
        OnDelay?.Invoke(Delays.Count);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>D365 client whose behaviour is set per test with delegates.</summary>
public sealed class FakeD365Client : ID365Client, ID365ClientFactory
{
    public List<string> Calls { get; } = [];

    public Func<string> ExportStatus { get; set; } = () => "Succeeded";
    public Func<string?> PackageUrl { get; set; } = () => "https://blob.test/export.zip?sig=secret";
    public Func<Uri, byte[]> DownloadContent { get; set; } = _ => TestFiles.Zip(("Customers.csv", "a;b"));
    public Func<string, string, string> ImportFromPackage { get; set; } = (file, executionId) => executionId;
    public Func<string, string> ExecutionStatus { get; set; } = _ => "Succeeded";
    public Func<string, DataJobState> JobState { get; set; } = _ => DataJobState.Processed;
    public Func<string, string> Enqueue { get; set; } = name => "MSG-" + name;
    public Queue<DequeuedMessage> Messages { get; } = new();
    public Func<DequeuedMessage, bool> AckSucceeds { get; set; } = _ => true;
    public Func<Exception?> AzureWriteUrlError { get; set; } = () => null;
    public List<(string FileName, byte[] Package)> UploadedPackages { get; } = [];
    public List<(string Name, string? Entity, string? Company)> Enqueued { get; } = [];

    public ID365Client Create(D365ConnectionOptions connection, D365ActionPaths? actionPaths = null) => this;

    public Task<string> ExportToPackageAsync(string definitionGroupId, string packageName, string executionId, string legalEntityId, bool reExecute, CancellationToken cancellationToken)
    {
        Calls.Add($"ExportToPackage {definitionGroupId} {legalEntityId}");
        return Task.FromResult(executionId);
    }

    public Task<string> GetExecutionSummaryStatusAsync(string executionId, CancellationToken cancellationToken)
    {
        Calls.Add("GetExecutionSummaryStatus " + executionId);
        return Task.FromResult(executionId.StartsWith("IMPORT", StringComparison.Ordinal) ? ExecutionStatus(executionId) : ExportStatus());
    }

    public Task<string?> GetExportedPackageUrlAsync(string executionId, CancellationToken cancellationToken)
    {
        Calls.Add("GetExportedPackageUrl");
        return Task.FromResult(PackageUrl());
    }

    public Task<string?> GetExecutionSummaryPageUrlAsync(string executionId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("https://d365.test/summary/" + executionId);

    public Task<BlobWriteUrl> GetAzureWriteUrlAsync(CancellationToken cancellationToken)
    {
        if (AzureWriteUrlError() is { } error) throw error;
        return Task.FromResult(new BlobWriteUrl("{blob}", new Uri("https://blob.test/upload/" + Guid.NewGuid())));
    }

    private string? _lastUploadedName;

    public Task UploadToBlobAsync(Uri blobUrl, Stream content, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        content.CopyTo(memory);
        _lastUploadedName = blobUrl.ToString();
        UploadedPackages.Add((blobUrl.ToString(), memory.ToArray()));
        return Task.CompletedTask;
    }

    public Task<string> ImportFromPackageAsync(string packageUrl, string definitionGroupId, string executionId, bool execute, bool overwrite, string legalEntityId, CancellationToken cancellationToken)
    {
        Calls.Add($"ImportFromPackage {legalEntityId}");
        return Task.FromResult(ImportFromPackage(packageUrl, executionId));
    }

    public Task<bool> GenerateImportTargetErrorKeysFileAsync(string executionId, string entityName, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<string?> GetImportTargetErrorKeysFileUrlAsync(string executionId, string entityName, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<string> GetExecutionErrorsAsync(string executionId, CancellationToken cancellationToken) =>
        Task.FromResult("{\"value\":\"Row 1: invalid account\"}");

    public Task<Stream> OpenDownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        Calls.Add("Download " + url.Host);
        return Task.FromResult<Stream>(new MemoryStream(DownloadContent(url)));
    }

    public Task<string> EnqueueAsync(Guid activityId, Stream content, string? company, string? entityName, string externalIdentifier, CancellationToken cancellationToken)
    {
        Enqueued.Add((externalIdentifier, entityName, company));
        return Task.FromResult(Enqueue(externalIdentifier));
    }

    public Task<DequeuedMessage?> DequeueAsync(Guid activityId, CancellationToken cancellationToken) =>
        Task.FromResult(Messages.Count > 0 ? Messages.Dequeue() : null);

    public Task AckAsync(Guid activityId, DequeuedMessage message, CancellationToken cancellationToken)
    {
        Calls.Add("Ack " + message.CorrelationId);
        return AckSucceeds(message) ? Task.CompletedTask : throw HttpError(HttpStatusCode.BadRequest, "ack rejected");
    }

    public Task<DataJobStatusDetail> GetJobStatusAsync(Guid activityId, string messageId, CancellationToken cancellationToken) =>
        Task.FromResult(new DataJobStatusDetail { DataJobStatus = new DataJobStatusInfo { DataJobState = JobState(messageId) }, ExecutionLog = "log of " + messageId });

    public static D365ApiException HttpError(HttpStatusCode status, string body) =>
        new("Test", new Uri("https://d365.test/"), status, body, new Dictionary<string, string>());
}

public sealed class FakeSftp : ISftpTransfer
{
    public List<string> Uploaded { get; } = [];
    public List<string> FilesToDownload { get; } = [];
    public IFileSystem? FileSystem { get; set; }

    public Task<IReadOnlyList<string>> DownloadFilesAsync(SftpServerOptions server, SftpTransferOptions transfer, string localFolder, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        foreach (var name in FilesToDownload)
        {
            var path = Path.Combine(localFolder, name);
            FileSystem!.File.WriteAllText(path, "from sftp");
            result.Add(path);
        }
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    public Task UploadFileAsync(SftpServerOptions server, SftpTransferOptions transfer, string localFilePath, CancellationToken cancellationToken)
    {
        Uploaded.Add(localFilePath);
        return Task.CompletedTask;
    }

    public Task TestConnectionAsync(SftpServerOptions server, string remoteFolder, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record LogEntry(LogLevel Level, string Category, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Scope);

/// <summary>Collects log entries together with active scope values.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose() { }

    public IEnumerable<LogEntry> Errors => Entries.Where(e => e.Level >= LogLevel.Error);

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, object?>();
            provider._scopes.ForEachScope((value, dict) =>
            {
                if (value is IEnumerable<KeyValuePair<string, object>> pairs)
                {
                    foreach (var (k, v) in pairs) dict[k] = v;
                }
            }, scope);
            provider.Entries.Enqueue(new LogEntry(logLevel, category, formatter(state, exception), exception, scope));
        }
    }
}

public static class TestFiles
{
    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return memory.ToArray();
    }

    public const string TemplateManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <DataManagementPackageManifest xmlns:i="http://www.w3.org/2001/XMLSchema-instance" xmlns="http://schemas.microsoft.com/dynamics/2015/01/DataManagement">
          <DefinitionGroupName>Vendor invoices</DefinitionGroupName>
          <PackageEntityList>
            <DataManagementPackageEntityData>
              <EntityName>Vendor invoice header</EntityName>
              <InputFilePath>Vendor invoice header.csv</InputFilePath>
            </DataManagementPackageEntityData>
          </PackageEntityList>
        </DataManagementPackageManifest>
        """;

    public static string ReadEntry(byte[] zip, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}

/// <summary>Builds DI container with fakes for D365, SFTP, file system and clock.</summary>
public sealed class JobsTestHost : IDisposable
{
    public MockFileSystem FileSystem { get; } = new();
    public FakeD365Client D365 { get; } = new();
    public FakeSftp Sftp { get; } = new();
    public FakeClock Clock { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();
    public RisConfiguration Configuration { get; } = new()
    {
        Connections = [new D365ConnectionOptions { Name = "PROD", AosUri = new Uri("https://d365.test"), Tenant = "t", ClientId = Guid.NewGuid().ToString(), ClientSecret = "s" }],
        SftpServers = [new SftpServerOptions { Name = "SFTP", Host = "sftp.test", UserName = "u", Password = "p" }]
    };

    public ServiceProvider Services { get; }

    public JobsTestHost(Action<IServiceCollection>? configure = null)
    {
        Sftp.FileSystem = FileSystem;
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        // After logging: Quartz log capture registers its own logger provider.
        configure?.Invoke(services);
        services.AddSingleton<IFileSystem>(FileSystem);
        services.AddSingleton<IPollingClock>(Clock);
        services.AddSingleton<ID365ClientFactory>(D365);
        services.AddSingleton<ISftpTransfer>(Sftp);
        services.AddSingleton<IRisConfigurationStore>(new StaticConfigurationStore(Configuration));
        services.AddRisJobs();
        Services = services.BuildServiceProvider();
    }

    public Task<JobOutcome> RunAsync(JobOptionsBase options, CancellationToken cancellationToken = default)
    {
        options.Connection = "PROD";
        options.Retry = new RetryOptions { Count = 1, Delay = TimeSpan.Zero };
        return Services.GetRequiredService<JobPipeline>().RunAsync("Tests." + options.GetType().Name, options, "RUN-1", null, cancellationToken);
    }

    public void Dispose() => Services.Dispose();
}
