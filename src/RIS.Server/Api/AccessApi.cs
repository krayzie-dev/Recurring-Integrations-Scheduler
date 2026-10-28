using System.Security.Claims;
using RIS.Hosting;
using RIS.Server.Services;

namespace RIS.Server.Api;

public sealed record AccessMemberRequest(string Member);

/// <summary>
/// Role members (Admin): GET lists the members of the settings file (read-only) and of the application, POST adds a Windows
/// group or user to a role (checked in Windows/AD), DELETE removes one added in the application. Effective without a restart.
/// </summary>
public static class AccessApi
{
    public static void MapAccessApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/access").RequireAuthorization(RisRoles.CanAdminister).WithTags("Access");

        api.MapGet("", async (AccessService access, CancellationToken ct) => Results.Ok(await access.MembersAsync(ct)));
        api.MapPost("/{role}", async (string role, AccessMemberRequest request, AccessService access, ClaimsPrincipal user, CancellationToken ct) =>
            Result(await access.AddAsync(role, request.Member ?? "", user.Identity?.Name, ct), role, request.Member));
        api.MapDelete("/{role}/{*member}", async (string role, string member, AccessService access, ClaimsPrincipal user, CancellationToken ct) =>
            Result(await access.RemoveAsync(role, member, user.Identity?.Name, ct), role, member));
    }

    private static IResult Result(AccessResult result, string role, string? member) => result.Change switch
    {
        AccessChange.Done => result.Account is { } account ? Results.Ok(account) : Results.NoContent(),
        AccessChange.UnknownRole => Problem($"Unknown role '{role}' (Admin, Operator or Viewer).", StatusCodes.Status400BadRequest),
        AccessChange.UnknownAccount => Problem($"Windows account or group '{member}' was not found.", StatusCodes.Status400BadRequest),
        AccessChange.AlreadyMember => Problem($"'{result.Account?.Name ?? member}' already has the role {role}.", StatusCodes.Status409Conflict),
        AccessChange.FromSettings => Problem($"'{result.Account?.Name ?? member}' has the role {role} in the settings file (appsettings.json); change it there.", StatusCodes.Status409Conflict),
        _ => Problem($"'{member}' does not have the role {role}.", StatusCodes.Status404NotFound)
    };

    private static IResult Problem(string detail, int status) => Results.Problem(detail, statusCode: status);
}
