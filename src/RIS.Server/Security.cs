using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Authentication;

namespace RIS.Server;

public static class RisRoles
{
    public const string Viewer = RIS.Hosting.AccessRoles.Viewer;
    public const string Operator = RIS.Hosting.AccessRoles.Operator;
    public const string Admin = RIS.Hosting.AccessRoles.Admin;

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
/// Adds RIS role claims to Windows users based on their group membership (group SIDs in the Windows token) or user name,
/// using the members of the settings file and of the "Access" page (<see cref="RoleMap"/>).
/// SIDs work on every Windows language (BUILTIN\Administrators is "BUILTIN\Administratorzy" on Polish Windows).
/// </summary>
public sealed class WindowsRoleClaimsTransformation(RoleMap roles) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true } || principal.HasClaim(c => c.Type == "ris.roles-mapped"))
        {
            return principal;
        }

        var mapped = new ClaimsIdentity();
        mapped.AddClaim(new Claim("ris.roles-mapped", "true"));
        foreach (var role in (await roles.MatchesAsync(principal)).Select(m => m.Role).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            mapped.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        principal.AddIdentity(mapped);
        return principal;
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
