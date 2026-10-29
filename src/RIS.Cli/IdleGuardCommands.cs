using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using RIS.Core.Configuration;
using RIS.Persistence;

namespace RIS.Cli;

/// <summary>
/// "ris idle-guard": switches the idle job guard on or off and changes its settings (stored in ris.db, audited). The running
/// service reads them within a minute. The preview of what would be paused is on the Maintenance page.
/// </summary>
internal static class IdleGuardCommands
{
    public static Command Build()
    {
        var command = new Command("idle-guard", "Daily pause of jobs that only fail or do nothing (off by default)");
        command.Subcommands.Add(StatusCommand());
        command.Subcommands.Add(OnCommand());
        command.Subcommands.Add(OffCommand());
        return command;
    }

    private static Command StatusCommand()
    {
        var data = DbCommands.DataOption();
        var command = new Command("status", "Show the idle guard settings") { data };
        command.SetAction(async (parse, ct) =>
        {
            await using var services = await DbCommands.OpenAsync(parse.GetValue(data), ct);
            Print(await services.GetRequiredService<SettingsRepository>().GetAsync<IdleGuardSettings>(IdleGuardSettings.Key, ct));
            return 0;
        });
        return command;
    }

    private static Command OnCommand()
    {
        var data = DbCommands.DataOption();
        var only = new Option<string>("--only") { Description = "failing | idle (default: both checks)" };
        var days = new Option<int?>("--days") { Description = "Period in days (default 7)" };
        var minRuns = new Option<int?>("--min-runs") { Description = "Minimum planned runs in the period (default 20)" };
        var at = new Option<string>("--at") { Description = "Daily check time HH:mm, server time (default 06:00)" };
        var command = new Command("on", "Switch the idle guard on") { data, only, days, minRuns, at };
        command.SetAction(async (parse, ct) =>
        {
            var which = parse.GetValue(only)?.Trim().ToLowerInvariant();
            if (which is not (null or "" or "failing" or "idle"))
            {
                Console.Error.WriteLine("ERROR   --only must be 'failing' or 'idle'.");
                return 2;
            }
            TimeOnly? checkAt = null;
            if (parse.GetValue(at) is { Length: > 0 } text)
            {
                if (!TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    Console.Error.WriteLine("ERROR   --at must be HH:mm, e.g. 06:00.");
                    return 2;
                }
                checkAt = parsed;
            }

            await using var services = await DbCommands.OpenAsync(parse.GetValue(data), ct);
            var repository = services.GetRequiredService<SettingsRepository>();
            var settings = await repository.GetAsync<IdleGuardSettings>(IdleGuardSettings.Key, ct);
            settings.PauseFailing = which is null or "" or "failing";
            settings.PauseIdle = which is null or "" or "idle";
            settings.WindowDays = parse.GetValue(days) ?? settings.WindowDays;
            settings.MinPlannedRuns = parse.GetValue(minRuns) ?? settings.MinPlannedRuns;
            settings.CheckAt = checkAt ?? settings.CheckAt;
            return await SaveAsync(repository, settings, ct);
        });
        return command;
    }

    private static Command OffCommand()
    {
        var data = DbCommands.DataOption();
        var command = new Command("off", "Switch the idle guard off") { data };
        command.SetAction(async (parse, ct) =>
        {
            await using var services = await DbCommands.OpenAsync(parse.GetValue(data), ct);
            var repository = services.GetRequiredService<SettingsRepository>();
            var settings = await repository.GetAsync<IdleGuardSettings>(IdleGuardSettings.Key, ct);
            settings.PauseFailing = false;
            settings.PauseIdle = false;
            return await SaveAsync(repository, settings, ct);
        });
        return command;
    }

    private static async Task<int> SaveAsync(SettingsRepository repository, IdleGuardSettings settings, CancellationToken ct)
    {
        try
        {
            await repository.SaveAsync(IdleGuardSettings.Key, settings, $"{Environment.UserDomainName}\\{Environment.UserName} (ris cli)", ct);
        }
        catch (ConfigurationValidationException ex)
        {
            Console.Error.WriteLine("ERROR   " + string.Join("; ", ex.Errors));
            return 2;
        }
        Print(settings);
        Console.WriteLine("Saved. The service applies it within a minute; see what would be paused on the Maintenance page.");
        return 0;
    }

    private static void Print(IdleGuardSettings s)
    {
        Console.WriteLine($"Idle guard:     {(s.Enabled ? "on" : "off")}");
        Console.WriteLine($"Only failing:   {(s.PauseFailing ? "pause" : "-")}");
        Console.WriteLine($"Doing nothing:  {(s.PauseIdle ? "pause" : "-")}");
        Console.WriteLine($"Period:         {s.WindowDays} days");
        Console.WriteLine($"Min. planned:   {s.MinPlannedRuns} runs in the period");
        Console.WriteLine($"Check at:       {s.CheckAt:HH\\:mm} (server time)");
    }
}
