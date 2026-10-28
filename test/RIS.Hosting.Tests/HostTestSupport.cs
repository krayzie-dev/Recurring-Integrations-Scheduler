using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Extensibility;
using RIS.Core.Configuration;
using RIS.Core.Options;
using RIS.D365;
using RIS.Persistence;

namespace RIS.Hosting.Tests;

/// <summary>D365 stand-in: export finishes immediately (or never, when <see cref="NeverFinish"/>).</summary>
public sealed class FakeD365 : ID365ClientFactory, ID365Client
{
    public volatile bool NeverFinish;

    public ID365Client Create(D365ConnectionOptions connection, D365ActionPaths? actionPaths = null) => this;
    public Task<string> ExportToPackageAsync(string d, string p, string e, string l, bool r, CancellationToken ct) => Task.FromResult(e);
    public Task<string> GetExecutionSummaryStatusAsync(string executionId, CancellationToken ct) => Task.FromResult(NeverFinish ? "NotRun" : "Succeeded");
    public Task<string?> GetExportedPackageUrlAsync(string executionId, CancellationToken ct) => Task.FromResult<string?>("https://blob.test/p.zip");
    public Task<string?> GetExecutionSummaryPageUrlAsync(string executionId, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<BlobWriteUrl> GetAzureWriteUrlAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task UploadToBlobAsync(Uri blobUrl, Stream content, CancellationToken ct) => throw new NotSupportedException();
    public Task<string> ImportFromPackageAsync(string a, string b, string c, bool d, bool e, string f, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> GenerateImportTargetErrorKeysFileAsync(string e, string n, CancellationToken ct) => Task.FromResult(false);
    public Task<string?> GetImportTargetErrorKeysFileUrlAsync(string e, string n, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string> GetExecutionErrorsAsync(string e, CancellationToken ct) => Task.FromResult("");
    public Task<IReadOnlyList<DataProjectEntity>> GetDataProjectEntitiesAsync(string g, CancellationToken ct) => Task.FromResult<IReadOnlyList<DataProjectEntity>>([]);
    public Task<string> EnqueueAsync(Guid a, Stream c, string? co, string? en, string x, CancellationToken ct) => throw new NotSupportedException();
    public Task<DequeuedMessage?> DequeueAsync(Guid a, CancellationToken ct) => Task.FromResult<DequeuedMessage?>(null);
    public Task AckAsync(Guid a, DequeuedMessage m, CancellationToken ct) => Task.CompletedTask;
    public Task<DataJobStatusDetail> GetJobStatusAsync(Guid a, string m, CancellationToken ct) => throw new NotSupportedException();

    public Task<Stream> OpenDownloadAsync(Uri url, CancellationToken ct)
    {
        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("data.csv").Open()))
        {
            writer.Write("x");
        }
        memory.Position = 0;
        return Task.FromResult<Stream>(memory);
    }
}

/// <summary>RIS server host on a temporary data directory; the same directory can be started again to simulate a service restart.</summary>
public sealed class RisTestHost : IAsyncDisposable
{
    public RisTestHost(string dataDirectory, FakeD365 d365)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ris:Console"] = "false",
            ["Ris:ConfigurationPollInterval"] = "00:00:01"
        });
        builder.Services.AddRisServer(builder.Configuration, configurePersistence: p => p.DataDirectory = dataDirectory);
        builder.Services.AddSingleton<ID365ClientFactory>(d365);
        Host = builder.Build();
    }

    public IHost Host { get; }
    public ConfigurationRepository Repository => Host.Services.GetRequiredService<ConfigurationRepository>();
    public ScheduleSynchronizer Synchronizer => Host.Services.GetRequiredService<ScheduleSynchronizer>();

    public Task<IScheduler> SchedulerAsync() => Host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler().AsTask();

    public async Task<IReadOnlyList<ExecutionHistoryEntry>> HistoryAsync(string jobName)
    {
        var scheduler = await SchedulerAsync();
        var page = await Host.Services.GetRequiredService<IExecutionHistoryStore>()
            .QueryExecutions(new ExecutionHistoryQuery { SchedulerName = scheduler.SchedulerName, Job = new JobKey(jobName, "Tests") }, default);
        return page.Items;
    }

    public async ValueTask DisposeAsync()
    {
        try { await Host.StopAsync(TimeSpan.FromSeconds(30)); } catch (ObjectDisposedException) { }
        Host.Dispose();
        SqliteConnection.ClearAllPools();
    }

    public static D365ConnectionOptions Connection => new()
    {
        Name = "TEST",
        AosUri = new Uri("https://d365.test"),
        Tenant = "t",
        ClientId = Guid.NewGuid().ToString(),
        ClientSecret = "secret"
    };

    public static JobDefinition Export(string name, string downloadDir, ScheduleOptions schedule) => new()
    {
        Name = name,
        Group = "Tests",
        Schedule = schedule,
        Options = new ExportJobOptions { Connection = "TEST", DataProject = name, Company = "USMF", DownloadSuccessDir = downloadDir }
    };
}
