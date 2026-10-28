using System.CommandLine;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RIS.D365;
using RIS.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using RIS.Core.Configuration;
using RIS.Migration;
using RIS.Persistence;

namespace RIS.Cli;

/// <summary>
/// "ris db": configuration database of the RIS 5 server (ris.db). Must run on the server, as an account that can read
/// the data directory and its Data Protection keys (Administrator or the service account).
/// A running service picks up the changes within ConfigurationPollInterval (30 s by default).
/// </summary>
internal static partial class DbCommands
{
    public static Command Build()
    {
        var command = new Command("db", "Manage the RIS 5 server configuration database");
        command.Subcommands.Add(MigrateCommand());
        command.Subcommands.Add(ImportCommand());
        command.Subcommands.Add(Import4xCommand());
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

    /// <summary>
    /// Upgrade from 4.x on the 4.x server (used by the installer): Schedule.xml is converted in memory, the 4.x secrets are decrypted
    /// (DPAPI of this machine) and stored encrypted by RIS 5 - they are never written to a file. The report folder gets jobs.json
    /// with &lt;SET-SECRET&gt; placeholders and the list of warnings.
    /// </summary>
    private static Command Import4xCommand()
    {
        var schedule = new Argument<FileInfo>("schedule") { Description = "RIS 4.x Schedule.xml" };
        var data = DataOption();
        var paused = new Option<bool>("--paused") { Description = "Import all jobs paused (switch them one by one from 4.x)" };
        var noSecrets = new Option<bool>("--no-secrets") { Description = "Do not decrypt the 4.x secrets (set them later)" };
        var report = new Option<DirectoryInfo?>("--report") { Description = "Folder for jobs.json (without secrets) of the converted configuration" };
        var secretsOnly = new Option<bool>("--secrets-only") { Description = "Only set the secrets of D365 instances and SFTP servers that already exist in RIS 5 (jobs are not touched)" };
        var noCheck = new Option<bool>("--no-secret-check") { Description = "Do not try the client secrets against Entra ID (offline)" };
        var clientSettings = new Option<FileInfo?>("--client-settings") { Description = "Settings of the 4.x client with the app registrations (default: portable.config next to Schedule.xml)" };
        var command = new Command("import-4x", "Convert RIS 4.x Schedule.xml and import it, with the 4.x secrets when run on the 4.x server")
            { schedule, data, paused, noSecrets, report, secretsOnly, noCheck, clientSettings };
        command.SetAction(async (parse, ct) =>
        {
            var source = parse.GetValue(schedule)!;
            var result = new ScheduleXmlConverter(new ConversionOptions { DecryptSecrets = !parse.GetValue(noSecrets) }).Convert(XDocument.Load(source.FullName));
            var configuration = result.Configuration;
            if (!parse.GetValue(noSecrets))
            {
                var settingsFile = parse.GetValue(clientSettings)?.FullName ?? Path.Combine(source.DirectoryName ?? ".", ClientSettingsReader.FileName);
                AddAppRegistrationSecrets(result, settingsFile);
                if (!parse.GetValue(noCheck)) await ChooseClientSecretsAsync(result, ct);
            }
            if (parse.GetValue(secretsOnly)) return await SetSecretsOnlyAsync(configuration, parse.GetValue(data), ct);
            if (parse.GetValue(paused))
            {
                foreach (var job in configuration.Jobs) job.Schedule.Paused = true;
            }

            Console.WriteLine($"Source: {source.FullName}");
            foreach (var warning in result.Warnings) Console.WriteLine("WARNING " + warning);
            foreach (var error in ConfigurationValidator.Validate(configuration)) Console.WriteLine("CHECK   " + error);

            var withoutSecrets = new RisConfiguration
            {
                Connections = configuration.Connections.Select(ConfigurationRepository.WithoutSecrets).ToList(),
                SftpServers = configuration.SftpServers.Select(ConfigurationRepository.WithoutSecrets).ToList(),
                Jobs = configuration.Jobs
            };
            if (parse.GetValue(report) is { } folder)
            {
                folder.Create();
                RisJson.SaveConfiguration(withoutSecrets, Path.Combine(folder.FullName, "jobs.json"));
            }

            await using var services = await OpenAsync(parse.GetValue(data), ct);
            return await ChangeAsync(async () =>
            {
                await services.GetRequiredService<ConfigurationRepository>().ImportAsync(configuration, CurrentUser(), ct);
                var missing = configuration.Connections.Where(c => SecretPlaceholder.IsPlaceholderOrEmpty(c.ClientSecret) && SecretPlaceholder.IsPlaceholderOrEmpty(c.UserPassword)
                                                                   && string.IsNullOrEmpty(c.CertificateThumbprint)).Select(c => "connection " + c.Name)
                    .Concat(configuration.SftpServers.Where(s => !s.UseKey && SecretPlaceholder.IsPlaceholderOrEmpty(s.Password)).Select(s => "SFTP server " + s.Name))
                    .ToList();
                var migrated = configuration.Connections.Count + configuration.SftpServers.Count - missing.Count;
                Console.WriteLine($"Imported {configuration.Jobs.Count} job(s), {configuration.Connections.Count} connection(s), {configuration.SftpServers.Count} SFTP server(s)" +
                                  (parse.GetValue(paused) ? ", all jobs paused" : "") + $". Secrets taken over from 4.x: {migrated}.");
                if (missing.Count > 0)
                {
                    Console.WriteLine("Secrets to set in RIS 5 (Connections / SFTP servers, or ris db set-secret): " + string.Join(", ", missing));
                }
            });
        });
        return command;
    }

    /// <summary>
    /// 4.x jobs of one connection may carry different client secrets (renewed only in some jobs): each one is tried against
    /// Entra ID (client credentials token) and the first one accepted is used. Secrets are never printed.
    /// </summary>
    /// <summary>
    /// The 4.x client validates instances with its app registrations (portable.config): their secret is the one users keep up to
    /// date, while every job carries its own, possibly outdated copy. Registrations with the client id of a connection come first.
    /// </summary>
    private static void AddAppRegistrationSecrets(ConversionResult result, string settingsFile)
    {
        if (!File.Exists(settingsFile) || !OperatingSystem.IsWindows()) return;
        IReadOnlyList<AppRegistration> registrations;
        try
        {
            registrations = ClientSettingsReader.ReadAppRegistrations(settingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            Console.WriteLine($"WARNING 4.x client settings {settingsFile} could not be read: {ex.Message}");
            return;
        }
        Console.WriteLine($"4.x client settings {settingsFile}: {registrations.Count} app registration(s): " +
                          string.Join(", ", registrations.Select(r => $"{r.Name} ({r.ClientId})")));

        foreach (var connection in result.Configuration.Connections.Where(c => c.AuthMode == D365AuthMode.ClientSecret))
        {
            foreach (var registration in registrations.Where(r => r.IsService && r.EncryptedSecret is not null
                                                                  && string.Equals(r.ClientId, connection.ClientId, StringComparison.OrdinalIgnoreCase)).Reverse())
            {
                string value;
                try { value = ScheduleXmlConverter.DecryptDpapi(registration.EncryptedSecret!); }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException) { continue; }

                if (!result.ClientSecretCandidates.TryGetValue(connection.Name, out var candidates))
                {
                    result.ClientSecretCandidates[connection.Name] = candidates = [];
                }
                var candidate = candidates.FirstOrDefault(c => c.Value == value) ?? new SecretCandidate(value);
                candidates.Remove(candidate);
                candidate.Jobs.Insert(0, $"app registration '{registration.Name}' of the 4.x client");
                candidates.Insert(0, candidate);
                connection.ClientSecret = candidate.Value;
            }
        }
    }

    private static async Task ChooseClientSecretsAsync(ConversionResult result, CancellationToken cancellationToken)
    {
        var tokens = new MsalTokenProvider(NullLogger<MsalTokenProvider>.Instance);
        foreach (var (name, candidates) in result.ClientSecretCandidates)
        {
            if (result.Configuration.FindConnection(name) is not { } connection) continue;
            string? lastError = null;
            var tried = 0;
            SecretCandidate? accepted = null;
            foreach (var candidate in candidates)
            {
                tried++;
                var probe = new D365ConnectionOptions
                {
                    Name = connection.Name, AosUri = connection.AosUri, Tenant = connection.Tenant, Authority = connection.Authority,
                    ClientId = connection.ClientId, AuthMode = D365AuthMode.ClientSecret, ClientSecret = candidate.Value
                };
                try
                {
                    await tokens.GetAuthorizationHeaderAsync(probe, cancellationToken);
                    accepted = candidate;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = AadError().Match(ex.ToString()) is { Success: true } m ? m.Value : ex.GetBaseException().Message;
                    // Not an answer about the secret (network, proxy): the other candidates would fail the same way.
                    if (!lastError.StartsWith("AADSTS", StringComparison.Ordinal)) break;
                }
            }

            if (accepted is not null)
            {
                connection.ClientSecret = accepted.Value;
                Console.WriteLine($"Connection {name}: client secret accepted by Entra ID (source: {accepted.Jobs[0]}" +
                                  (accepted.Jobs.Count > 1 ? $" and {accepted.Jobs.Count - 1} more" : "") + $"; {tried} of {candidates.Count} tried).");
            }
            else
            {
                Console.WriteLine($"WARNING Connection {name}: no client secret of the 4.x jobs or client was accepted ({tried} of {candidates.Count} tried, last: {lastError}). " +
                                  "The most used one is stored; when it is expired, set a new secret on the D365 instances page.");
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"AADSTS\d+: [^.\r\n]+")]
    private static partial System.Text.RegularExpressions.Regex AadError();

    /// <summary>"--secrets-only": the converted secrets go to the D365 instances and SFTP servers of the same name, nothing else changes.</summary>
    private static async Task<int> SetSecretsOnlyAsync(RisConfiguration converted, DirectoryInfo? data, CancellationToken cancellationToken)
    {
        await using var services = await OpenAsync(data, cancellationToken);
        var repository = services.GetRequiredService<ConfigurationRepository>();
        var stored = await repository.LoadAsync(cancellationToken);
        var set = 0;
        foreach (var connection in converted.Connections.Where(c => !SecretPlaceholder.IsPlaceholderOrEmpty(c.ClientSecret)))
        {
            if (stored.FindConnection(connection.Name) is null)
            {
                Console.WriteLine($"Connection {connection.Name}: not in RIS 5, skipped.");
                continue;
            }
            await repository.SetSecretAsync(SecretOwner.Connection, connection.Name, connection.ClientSecret!, CurrentUser(), cancellationToken);
            set++;
        }
        foreach (var server in converted.SftpServers.Where(s => !s.UseKey && !SecretPlaceholder.IsPlaceholderOrEmpty(s.Password)))
        {
            if (stored.FindSftpServer(server.Name) is null)
            {
                Console.WriteLine($"SFTP server {server.Name}: not in RIS 5, skipped.");
                continue;
            }
            await repository.SetSecretAsync(SecretOwner.SftpServer, server.Name, server.Password!, CurrentUser(), cancellationToken);
            set++;
        }
        Console.WriteLine($"Secrets set: {set}. Jobs were not changed.");
        return 0;
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

    public static string CurrentUser() => $@"{Environment.UserDomainName}\{Environment.UserName} (ris cli)";

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
