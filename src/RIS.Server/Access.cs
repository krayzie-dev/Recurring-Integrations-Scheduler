using System.Security.Claims;
using RIS.Hosting;
using RIS.Persistence;

namespace RIS.Server;

/// <summary>Where a role member comes from: the settings file (installer, read-only in the UI) or the "Access" page / "ris access".</summary>
public enum RoleSource { Settings, Application }

/// <summary>A Windows group or user with a RIS role. <see cref="Sid"/> is null when the account could not be found.</summary>
public sealed record RoleMember(string Role, string Member, string? Sid, RoleSource Source, bool IsGroup, string? AddedBy = null, DateTime? AddedUtc = null);

/// <summary>
/// Members of the RIS roles: from the settings file (Ris:Security:Roles, resolved once) and from the database (page "Access",
/// "ris access"). The database part is reloaded after a change in this process and at most every 15 seconds otherwise (changes
/// made by the CLI), so a new member gets the role at the next page load or API call, without a service restart.
/// </summary>
public sealed class RoleMap
{
    public static readonly string[] Roles = AccessRoles.All;
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(15);

    private readonly IReadOnlyList<RoleMember> _settings;
    private readonly ConfigurationRepository _repository;
    private readonly TimeProvider _time;
    private readonly ILogger<RoleMap> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile Snapshot? _snapshot;

    private sealed record Snapshot(IReadOnlyList<RoleMember> Members, DateTimeOffset LoadedAt);

    public RoleMap(SecurityOptions options, ConfigurationRepository repository, TimeProvider time, ILogger<RoleMap> logger)
    {
        _repository = repository;
        _time = time;
        _logger = logger;
        var settings = new List<RoleMember>();
        foreach (var (role, members) in options.Roles)
        {
            foreach (var member in members)
            {
                var account = WindowsAccounts.Resolve(member);
                if (account is null) logger.LogWarning("Windows account or group {Member} configured for role {Role} was not found", member, role);
                // Shown by name also when the file has a SID (S-1-5-32-544 -> BUILTIN\Administrators in the language of Windows).
                settings.Add(new RoleMember(Canonical(role), account?.Name ?? member, account?.Sid, RoleSource.Settings, account?.IsGroup ?? false));
            }
        }
        _settings = settings;
        repository.RolesChanged += () => _snapshot = null;
    }

    /// <summary>All members: settings file first, then the ones added in the application.</summary>
    public async Task<IReadOnlyList<RoleMember>> MembersAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _snapshot;
        if (snapshot is not null && _time.GetUtcNow() - snapshot.LoadedAt < ReloadInterval) return snapshot.Members;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            snapshot = _snapshot;
            if (snapshot is not null && _time.GetUtcNow() - snapshot.LoadedAt < ReloadInterval) return snapshot.Members;
            IReadOnlyList<RoleMemberEntity> stored;
            try
            {
                stored = await _repository.GetRoleMembersAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The settings file still works (e.g. database being migrated): never lock the administrators out.
                _logger.LogWarning(ex, "Role members could not be read from the database; only the roles of the settings file apply");
                return snapshot?.Members ?? _settings;
            }
            var members = _settings.Concat(stored.Select(m => new RoleMember(Canonical(m.Role), m.Member, m.Sid, RoleSource.Application,
                WindowsAccounts.Resolve(m.Sid)?.IsGroup ?? true, m.AddedBy, m.AddedUtc))).ToList();
            _snapshot = new Snapshot(members, _time.GetUtcNow());
            return members;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Members that give the user their roles (user SID, group SIDs from the Windows token, or the user name).</summary>
    public async Task<IReadOnlyList<RoleMember>> MatchesAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var groupSids = principal.FindAll(ClaimTypes.GroupSid).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var userSid = principal.FindFirst(ClaimTypes.PrimarySid)?.Value;
        var name = principal.Identity?.Name ?? string.Empty;
        return (await MembersAsync(cancellationToken)).Where(m =>
                (m.Sid is not null && (groupSids.Contains(m.Sid) || string.Equals(m.Sid, userSid, StringComparison.OrdinalIgnoreCase)))
                || string.Equals(m.Member, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Role name as RIS uses it (case of the settings file does not matter), or null for an unknown role.</summary>
    public static string? Normalize(string role) => AccessRoles.Normalize(role);

    private static string Canonical(string role) => Normalize(role) ?? role;
}
