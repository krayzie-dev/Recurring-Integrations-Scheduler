using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Authentication;

namespace RIS.Server;

public static class RisRoles
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string Admin = "Admin";

    /// <summary>Authorization policy names; a higher role includes the lower ones.</summary>
    public const string CanView = "CanView";
    public const string CanOperate = "CanOperate";
    public const string CanAdminister = "CanAdminister";
}

/// <summary>Windows groups or users per RIS role (appsettings "Ris:Security:Roles").</summary>
public sealed class SecurityOptions
{
    /// <summary>Windows authentication (Negotiate: Kerberos/NTLM). Disabled only in tests, which use their own scheme.</summary>
    public bool WindowsAuthentication { get; set; } = true;

    public Dictionary<string, List<string>> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Adds RIS role claims to Windows users based on their group membership (group SIDs in the Windows token) or user name.
/// </summary>
public sealed class WindowsRoleClaimsTransformation : IClaimsTransformation
{
    private readonly Dictionary<string, (HashSet<string> Sids, HashSet<string> Names)> _roles = new(StringComparer.OrdinalIgnoreCase);

    public WindowsRoleClaimsTransformation(SecurityOptions options, ILogger<WindowsRoleClaimsTransformation> logger)
    {
        foreach (var (role, members) in options.Roles)
        {
            var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
            foreach (var member in members)
            {
                // SIDs work on every Windows language (BUILTIN\Administrators is "BUILTIN\Administratorzy" on Polish Windows).
                if (member.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                {
                    sids.Add(member);
                    continue;
                }
                if (!OperatingSystem.IsWindows()) continue;
                try
                {
                    sids.Add(new NTAccount(member).Translate(typeof(SecurityIdentifier)).Value);
                }
                catch (IdentityNotMappedException)
                {
                    logger.LogWarning("Windows account or group {Member} configured for role {Role} was not found", member, role);
                }
            }
            _roles[role] = (sids, names);
        }
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true } identity || principal.HasClaim(c => c.Type == "ris.roles-mapped"))
        {
            return Task.FromResult(principal);
        }

        var groupSids = principal.FindAll(ClaimTypes.GroupSid).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var userSid = principal.FindFirst(ClaimTypes.PrimarySid)?.Value;
        var mapped = new ClaimsIdentity();
        mapped.AddClaim(new Claim("ris.roles-mapped", "true"));
        foreach (var (role, (sids, names)) in _roles)
        {
            if (sids.Overlaps(groupSids) || (userSid is not null && sids.Contains(userSid)) || names.Contains(identity.Name ?? string.Empty))
            {
                mapped.AddClaim(new Claim(ClaimTypes.Role, role));
            }
        }

        principal.AddIdentity(mapped);
        return Task.FromResult(principal);
    }
}

/// <summary>Browser pages a user may not see redirect to a friendly "no access" page; the API keeps plain 403.</summary>
public sealed class RisAuthorizationResultHandler : Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler
{
    private readonly Microsoft.AspNetCore.Authorization.Policy.AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, Microsoft.AspNetCore.Authorization.AuthorizationPolicy policy,
        Microsoft.AspNetCore.Authorization.Policy.PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && HttpMethods.IsGet(context.Request.Method)
            && !context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/health"))
        {
            context.Response.Redirect("/no-access");
            return Task.CompletedTask;
        }
        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
