using System.CommandLine;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Extensibility;
using RIS.Core.Configuration;
using RIS.Hosting;
using RIS.Jobs.Quartz;
using RIS.Migration;

namespace RIS.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await BuildCommand().Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
        {
            // Configuration/input file problems: short message instead of a stack trace.
            Console.Error.WriteLine($"ERROR   {ex.Message}");
            return 3;
        }
    }

    public static RootCommand BuildCommand()
    {
        var root = new RootCommand("Recurring Integrations Scheduler 5 - command line tools");
        root.Subcommands.Add(ValidateCommand());
        root.Subcommands.Add(ConvertCommand());
        root.Subcommands.Add(ListCommand());
        root.Subcommands.Add(RunCommand());
        return root;
    }

    private static Argument<FileInfo> ConfigArgument() => new("config") { Description = "RIS configuration file (jobs.json)" };

    private static Command ValidateCommand()
    {
        var config = ConfigArgument();
        var command = new Command("validate", "Validate configuration file and check that folders exist") { config };
        command.SetAction(parse =>
        {
            var file = parse.GetValue(config)!;
            var configuration = RisJson.LoadConfiguration(file.FullName);
            var errors = ConfigurationValidator.Validate(configuration).ToList();
            var warnings = MissingFolders(configuration).ToList();

            foreach (var error in errors) Console.Error.WriteLine("ERROR   " + error);
            foreach (var warning in warnings) Console.WriteLine("WARNING " + warning);
            Console.WriteLine($"{configuration.Jobs.Count} job(s), {configuration.Connections.Count} connection(s), {configuration.SftpServers.Count} SFTP server(s): " +
                              (errors.Count == 0 ? "configuration is valid" : $"{errors.Count} error(s)"));
            return errors.Count == 0 ? 0 : 1;
        });
        return command;
    }

    private static Command ConvertCommand()
    {
        var schedule = new Argument<FileInfo>("schedule") { Description = "RIS 4.x Schedule.xml" };
        var output = new Option<FileInfo>("--output", "-o") { Description = "Target jobs.json", Required = true };
        var decrypt = new Option<bool>("--decrypt-secrets") { Description = "Decrypt 4.x secrets (only on the 4.x server where they were encrypted)" };
        var command = new Command("convert-schedule", "Convert RIS 4.x Schedule.xml to RIS 5 configuration") { schedule, output, decrypt };
        command.SetAction(parse =>
        {
            var converter = new ScheduleXmlConverter(new ConversionOptions { DecryptSecrets = parse.GetValue(decrypt) });
            var result = converter.Convert(XDocument.Load(parse.GetValue(schedule)!.FullName));
            var target = parse.GetValue(output)!;
            RisJson.SaveConfiguration(result.Configuration, target.FullName);

            foreach (var warning in result.Warnings) Console.WriteLine("WARNING " + warning);
            var errors = ConfigurationValidator.Validate(result.Configuration);
            foreach (var error in errors) Console.WriteLine("CHECK   " + error);
            Console.WriteLine($"Converted {result.Configuration.Jobs.Count} job(s), {result.Configuration.Connections.Count} connection(s), " +
                              $"{result.Configuration.SftpServers.Count} SFTP server(s) to {target.FullName}. {result.Warnings.Count} warning(s), {errors.Count} validation issue(s).");
            return 0;
        });
        return command;
    }

    private static Command ListCommand()
    {
        var config = ConfigArgument();
        var command = new Command("list", "List configured jobs") { config };
        command.SetAction(parse =>
        {
            var configuration = RisJson.LoadConfiguration(parse.GetValue(config)!.FullName);
            foreach (var job in configuration.Jobs.OrderBy(j => j.Group).ThenBy(j => j.Name))
            {
                var schedule = job.Schedule.Cron ?? $"every {job.Schedule.Interval}";
                var state = job.Schedule.Paused || job.Options?.IndefinitePause == true ? " [paused]" : string.Empty;
                Console.WriteLine($"{job.Key,-70} {job.Type,-18} {schedule}{state}");
            }
            return 0;
        });
        return command;
    }

    private static Command RunCommand()
    {
        var config = ConfigArgument();
        var job = new Option<string>("--job", "-j") { Description = "Job name or Group.Name", Required = true };
        var dryRun = new Option<bool>("--dry-run") { Description = "Only validate and show what would run" };
        var logs = new Option<DirectoryInfo?>("--logs") { Description = "Log folder (default: logs next to configuration)" };
        var command = new Command("run", "Run one job once through the RIS 5 scheduler and print its result") { config, job, dryRun, logs };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var file = parse.GetValue(config)!;
            var configuration = RisJson.LoadConfiguration(file.FullName);
            var definition = configuration.FindJob(parse.GetValue(job)!);
            if (definition is null)
            {
                Console.Error.WriteLine($"Job '{parse.GetValue(job)}' is not defined in {file.FullName}.");
                return 2;
            }

            var errors = ConfigurationValidator.ValidateObject(definition, definition.Key);
            foreach (var error in errors) Console.Error.WriteLine("ERROR   " + error);
            if (errors.Count > 0) return 1;

            if (parse.GetValue(dryRun))
            {
                Console.WriteLine($"Would run {definition.Key} ({definition.Type}) using connection '{definition.Options!.Connection}'.");
                return 0;
            }

            return await RunJobAsync(file.FullName, definition, parse.GetValue(logs)?.FullName, cancellationToken);
        });
        return command;
    }

    private static async Task<int> RunJobAsync(string configFile, JobDefinition definition, string? logDirectory, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders(); // Serilog writes to console; must run before AddRis (Quartz log capture provider)
        builder.Services.AddRis(builder.Configuration, options =>
        {
            options.ConfigurationFile = configFile;
            options.LogDirectory = logDirectory ?? Path.Combine(Path.GetDirectoryName(configFile)!, "logs");
        });
        using var host = builder.Build();

        var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(cancellationToken);
        await scheduler.Start(cancellationToken);
        try
        {
            var once = new JobDefinition
            {
                Name = definition.Name,
                Group = definition.Group,
                Description = definition.Description,
                Options = definition.Options,
                Schedule = new ScheduleOptions { Interval = TimeSpan.FromDays(3650) }
            };
            once.Options!.IndefinitePause = false; // explicit manual run
            await RisJobScheduling.ScheduleAsync(scheduler, once, cancellationToken);

            var history = host.Services.GetRequiredService<IExecutionHistoryStore>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await history.QueryExecutions(new ExecutionHistoryQuery { SchedulerName = scheduler.SchedulerName, Job = new JobKey(definition.Name, definition.Group) }, cancellationToken);
                var entry = page.Items.FirstOrDefault();
                if (entry is not null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Result:   {entry.EffectiveResult}");
                    Console.WriteLine($"Summary:  {entry.Summary ?? entry.ExceptionMessage}");
                    Console.WriteLine($"Metrics:  {entry.MetricsJson}");
                    Console.WriteLine($"Run id:   {entry.FireInstanceId}");
                    Console.WriteLine($"Duration: {entry.Duration}");
                    return entry.EffectiveResult == JobRunResult.Failed ? 1 : 0;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: true, CancellationToken.None);
            await Serilog.Log.CloseAndFlushAsync();
        }
    }

    private static IEnumerable<string> MissingFolders(RisConfiguration configuration)
    {
        foreach (var job in configuration.Jobs)
        {
            foreach (var property in job.Options!.GetType().GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name.EndsWith("Dir", StringComparison.Ordinal)))
            {
                if (property.GetValue(job.Options) is string folder && folder.Length > 0 && !Directory.Exists(folder))
                {
                    yield return $"{job.Key}: {property.Name} '{folder}' does not exist or is not accessible from this machine.";
                }
            }
        }
    }
}
