using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RIS.Hosting;
using RIS.Persistence;

namespace RIS.Cli;

/// <summary>
/// "ris access": role members added in the application (page "Access"), stored in ris.db. For scripts and to regain access
/// (run on the server as administrator). Members of the settings file (appsettings.json, installer) are only listed.
/// The running service applies the change within 15 seconds, without a restart.
/// </summary>
internal static class AccessCommands
{
    public static Command Build()
    {
        var command = new Command("access", "Manage who has the RIS roles Admin, Operator and Viewer");
        command.Subcommands.Add(ListCommand());
        command.Subcommands.Add(ChangeCommand("add", "Give a role to a Windows group or user (checked in Windows/AD)"));
        command.Subcommands.Add(ChangeCommand("remove", "Take a role given with 'add' or on the Access page"));
        return command;
    }

    private static Command ListCommand()
    {
        var data = DbCommands.DataOption();
        var command = new Command("list", "Role members of the settings file and of the application") { data };
        command.SetAction(async (parse, ct) =>
        {
            await using var services = await DbCommands.OpenAsync(parse.GetValue(data), ct);
            var persistence = services.GetRequiredService<PersistenceOptions>();
            var settings = new ConfigurationBuilder().AddJsonFile(Path.Combine(persistence.DataDirectory, "appsettings.json"), optional: true).Build();
            foreach (var role in AccessRoles.All)
            {
                foreach (var member in settings.GetSection($"Ris:Security:Roles:{role}").Get<string[]>() ?? [])
                {
                    Console.WriteLine($"{role,-9} {member,-50} settings file");
                }
            }
            foreach (var member in await services.GetRequiredService<ConfigurationRepository>().GetRoleMembersAsync(ct))
            {
                Console.WriteLine($"{member.Role,-9} {member.Member,-50} application ({member.Sid}, {member.AddedBy}, {member.AddedUtc:u})");
            }
            return 0;
        });
        return command;
    }

    private static Command ChangeCommand(string name, string description)
    {
        var role = new Argument<string>("role") { Description = "Admin | Operator | Viewer" };
        var member = new Argument<string>("member") { Description = @"DOMAIN\group, DOMAIN\user or SID" };
        var data = DbCommands.DataOption();
        var command = new Command(name, description) { role, member, data };
        command.SetAction(async (parse, ct) =>
        {
            if (AccessRoles.Normalize(parse.GetValue(role)!) is not { } roleName)
            {
                Console.Error.WriteLine($"ERROR   Unknown role '{parse.GetValue(role)}' (Admin, Operator or Viewer).");
                return 1;
            }
            var entered = parse.GetValue(member)!;
            var account = WindowsAccounts.Resolve(entered);
            await using var services = await DbCommands.OpenAsync(parse.GetValue(data), ct);
            var repository = services.GetRequiredService<ConfigurationRepository>();
            if (name == "add")
            {
                if (account is null)
                {
                    Console.Error.WriteLine($"ERROR   Windows/AD has no account or group '{entered}'.");
                    return 1;
                }
                var added = await repository.AddRoleMemberAsync(roleName, account.Name, account.Sid, DbCommands.CurrentUser(), ct);
                Console.WriteLine(added ? $"{account.Name} ({account.Sid}, {(account.IsGroup ? "group" : "user")}) has the role {roleName}." : $"{account.Name} already has the role {roleName}.");
                return 0;
            }

            var removed = await repository.RemoveRoleMemberAsync(roleName, account?.Sid ?? entered, DbCommands.CurrentUser(), ct);
            Console.WriteLine(removed ? $"{account?.Name ?? entered} no longer has the role {roleName}." : $"{entered} does not have the role {roleName} in the application (roles of the settings file are changed in appsettings.json).");
            return removed ? 0 : 2;
        });
        return command;
    }
}
