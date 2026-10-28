using RIS.Hosting;
using RIS.Persistence;

namespace RIS.Server.Services;

public enum AccessChange { Done, UnknownRole, UnknownAccount, AlreadyMember, FromSettings, NotFound }

/// <summary>Result of adding or removing a role member; <see cref="Account"/> is the resolved account when there is one.</summary>
public sealed record AccessResult(AccessChange Change, WindowsAccount? Account = null)
{
    public bool Succeeded => Change == AccessChange.Done;
}

/// <summary>
/// Role members of the "Access" page and the API: the account is checked in Windows/AD before it is stored, members of the
/// settings file are read-only (they cannot be removed here, so administrators cannot lock themselves out).
/// </summary>
public sealed class AccessService(RoleMap roles, ConfigurationRepository repository)
{
    public Task<IReadOnlyList<RoleMember>> MembersAsync(CancellationToken cancellationToken = default) => roles.MembersAsync(cancellationToken);

    public async Task<AccessResult> AddAsync(string role, string member, string? user, CancellationToken cancellationToken = default)
    {
        if (RoleMap.Normalize(role) is not { } normalized) return new AccessResult(AccessChange.UnknownRole);
        if (WindowsAccounts.Resolve(member) is not { } account) return new AccessResult(AccessChange.UnknownAccount);

        var existing = (await roles.MembersAsync(cancellationToken)).FirstOrDefault(m =>
            m.Role == normalized && string.Equals(m.Sid, account.Sid, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return new AccessResult(existing.Source == RoleSource.Settings ? AccessChange.FromSettings : AccessChange.AlreadyMember, account);
        }

        var added = await repository.AddRoleMemberAsync(normalized, account.Name, account.Sid, user, cancellationToken);
        return new AccessResult(added ? AccessChange.Done : AccessChange.AlreadyMember, account);
    }

    public async Task<AccessResult> RemoveAsync(string role, string member, string? user, CancellationToken cancellationToken = default)
    {
        if (RoleMap.Normalize(role) is not { } normalized) return new AccessResult(AccessChange.UnknownRole);
        var sid = WindowsAccounts.Resolve(member)?.Sid;
        var current = (await roles.MembersAsync(cancellationToken)).Where(m => m.Role == normalized &&
            (string.Equals(m.Member, member, StringComparison.OrdinalIgnoreCase) || (sid is not null && string.Equals(m.Sid, sid, StringComparison.OrdinalIgnoreCase)))).ToList();
        if (current.Count == 0) return new AccessResult(AccessChange.NotFound);
        if (current.All(m => m.Source == RoleSource.Settings)) return new AccessResult(AccessChange.FromSettings);

        var stored = current.First(m => m.Source == RoleSource.Application);
        return await repository.RemoveRoleMemberAsync(normalized, stored.Sid ?? stored.Member, user, cancellationToken)
            ? new AccessResult(AccessChange.Done)
            : new AccessResult(AccessChange.NotFound);
    }
}
