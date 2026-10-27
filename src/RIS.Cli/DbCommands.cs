using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RIS.Core.Configuration;
using RIS.Persistence;

namespace RIS.Cli;

/// <summary>
/// "ris db": configuration database of the RIS 5 server (ris.db). Must run on the server, as an account that can read
/// the data directory and its Data Protection keys (Administrator or the service account).
/// A running service picks up the changes within ConfigurationPollInterval (30 s by default).
/// </summary>
internal static class DbCommands
{
    public static Command Build()
    {
        var command = new Command("db", "Manage the RIS 5 server configuration database");
        command.Subcommands.Add(MigrateCommand());
        command.Subcommands.Add(ImportCommand());
        command.Subcommands.Add(ExportCommand());
        command.Subcommands.Add(SetSecretCommand());
        return command;
    }

    public static Option<DirectoryInfo?> DataOption() => new("--data") { Description = @"Data directory of the server (default %ProgramData%\RIS5)" };

    private static Command MigrateCommand()
    {
        var data = DataOption();
        var command = new Command("migrate", "Create or update the database schema (with a backup)") { data };
        command.SetAction(async (parse, ct) =>
        {
            await using var services = await OpenAsync(parse.GetValue(data), ct);
            Console.WriteLine($"Database is up to date: {services.GetRequiredService<PersistenceOptions>().RisDatabase}");
            return 0;
        });
        return command;
    }

    private static Command ImportCommand()
    {
        var file = new Argument<FileInfo>("config") { Description = "jobs.json (e.g. output of convert-schedule)" };
        var data = DataOption();
        var paused = new Option<bool>("--paused") { Description = "Import all jobs paused (parallel run with 4.x: visible, but not started until resumed)" };
        var command = new Command("import", "Add or update connections, SFTP servers and jobs from a file. Placeholder secrets keep stored values.") { file, data, paused };
        command.SetAction(async (parse, ct) =>
        {
            var configuration = RisJson.LoadConfiguration(parse.GetValue(file)!.FullName);
            if (parse.GetValue(paused))
            {
                foreach (var job in configuration.Jobs) job.Schedule.Paused = true;
            }
            await using var services = await OpenAsync(parse.GetValue(data), ct);
            return await ChangeAsync(async () =>
            {
                await services.GetRequiredService<ConfigurationRepository>().ImportAsync(configuration, CurrentUser(), ct);
                Console.WriteLine($"Imported {configuration.Jobs.Count} job(s), {configuration.Connections.Count} connection(s), {configuration.SftpServers.Count} SFTP server(s)." + (parse.GetValue(paused) ? " Jobs are paused." : ""));
                var missing = configuration.Connections.Where(c => SecretPlaceholder.IsPlaceholderOrEmpty(c.ClientSecret) && SecretPlaceholder.IsPlaceholderOrEmpty(c.UserPassword))
                    .Select(c => c.Name).ToList();
                if (missing.Count > 0)
                {
                    Console.WriteLine("Connections without a secret in the file (stored value kept, if any): " + string.Join(", ", missing) +
                                      ". Set with: ris db set-secret connection <name>");
                }
            });
        });
        return command;
    }

    private static Command ExportCommand()
    {
        var output = new Option<FileInfo>("--output", "-o") { Description = "Target jobs.json", Required = true };
        var data = DataOption();
        var command = new Command("export", "Write the configuration to a file. Secrets are replaced by <SET-SECRET>.") { output, data };
        command.SetAction(async (parse, ct) =>
        {
            await using var services = await OpenAsync(parse.GetValue(data), ct);
            var configuration = await services.GetRequiredService<ConfigurationRepository>().ExportAsync(ct);
            var target = parse.GetValue(output)!;
            RisJson.SaveConfiguration(configuration, target.FullName);
            Console.WriteLine($"Exported {configuration.Jobs.Count} job(s), {configuration.Connections.Count} connection(s), {configuration.SftpServers.Count} SFTP server(s) to {target.FullName}.");
            return 0;
        });
        return command;
    }

    private static Command SetSecretCommand()
    {
        var owner = new Argument<string>("owner") { Description = "connection | sftp" };
        owner.AcceptOnlyFromAmong("connection", "sftp");
        var name = new Argument<string>("name") { Description = "Connection or SFTP server name" };
        var data = DataOption();
        var command = new Command("set-secret",
            "Set the client secret / password of a connection or the password / key passphrase of an SFTP server. The value is read from the console without echo, or from standard input.")
            { owner, name, data };
        command.SetAction(async (parse, ct) =>
        {
            var value = ReadSecret($"Secret for {parse.GetValue(owner)} '{parse.GetValue(name)}': ");
            if (SecretPlaceholder.IsPlaceholderOrEmpty(value))
            {
                Console.Error.WriteLine("ERROR   Empty secret - nothing changed.");
                return 1;
            }

            await using var services = await OpenAsync(parse.GetValue(data), ct);
            var secretOwner = parse.GetValue(owner) == "sftp" ? SecretOwner.SftpServer : SecretOwner.Connection;
            return await ChangeAsync(async () =>
            {
                await services.GetRequiredService<ConfigurationRepository>().SetSecretAsync(secretOwner, parse.GetValue(name)!, value!, CurrentUser(), ct);
                Console.WriteLine("Secret stored (encrypted).");
            });
        });
        return command;
    }

    /// <summary>Services for the database only (no scheduler); the schema is migrated first.</summary>
    public static async Task<ServiceProvider> OpenAsync(DirectoryInfo? dataDirectory, CancellationToken cancellationToken)
    {
        var persistence = new PersistenceOptions();
        if (dataDirectory is not null) persistence.DataDirectory = dataDirectory.FullName;

        var services = new ServiceCollection()
            .AddLogging(logging => logging.AddSimpleConsole(console => console.SingleLine = true).SetMinimumLevel(LogLevel.Warning))
            .AddRisPersistence(persistence)
            .BuildServiceProvider();
        await DatabaseMaintenance.MigrateAsync(services, cancellationToken);
        return services;
    }

    private static async Task<int> ChangeAsync(Func<Task> change)
    {
        try
        {
            await change();
            return 0;
        }
        catch (ConfigurationValidationException ex)
        {
            foreach (var error in ex.Errors) Console.Error.WriteLine("ERROR   " + error);
            Console.Error.WriteLine("Nothing was changed.");
            return 1;
        }
        catch (ConfigurationNotFoundException ex)
        {
            Console.Error.WriteLine("ERROR   " + ex.Message);
            return 2;
        }
    }

    private static string CurrentUser() => $@"{Environment.UserDomainName}\{Environment.UserName} (ris cli)";

    private static string? ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine();

        Console.Write(prompt);
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
        Console.WriteLine();
        return value.ToString();
    }
}
