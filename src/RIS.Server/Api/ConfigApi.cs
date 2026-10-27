using System.Security.Claims;
using RIS.Core.Configuration;
using RIS.Core.Options;
using RIS.Persistence;
using RIS.Server.Services;

namespace RIS.Server.Api;

public sealed record SecretRequest(string Value);

public sealed record D365TestRequest(string Connection);

public sealed record FolderTestRequest(string? Path);

public sealed record SftpTestRequest(string Server, string? RemoteFolder);

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>
/// Configuration API (Admin): connections, SFTP servers, job definitions, secrets, validation, import/export, audit.
/// Secrets are write-only: responses contain "&lt;SET-SECRET&gt;" placeholders. Every change is validated and audited by
/// <see cref="ConfigurationRepository"/>; the scheduler picks it up through <see cref="DbConfigurationStore"/>.
/// </summary>
public static class ConfigApi
{
    public static void MapConfigApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization(RisRoles.CanAdminister).WithTags("Configuration");

        api.MapGet("/connections", async (ConfigurationRepository repository, CancellationToken ct) =>
            Results.Ok((await repository.LoadAsync(ct)).Connections.Select(ConfigurationRepository.WithoutSecrets)));
        api.MapGet("/connections/{name}", async (string name, ConfigurationRepository repository, CancellationToken ct) =>
            (await repository.LoadAsync(ct)).FindConnection(name) is { } c ? Results.Ok(ConfigurationRepository.WithoutSecrets(c)) : NotFound("Connection", name));
        api.MapPut("/connections/{name}", async (string name, D365ConnectionOptions connection, ConfigurationRepository repository, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (Mismatch(name, connection.Name) is { } problem) return problem;
            await repository.SaveConnectionAsync(connection, User(user), ct);
            return Results.Ok(ConfigurationRepository.WithoutSecrets((await repository.LoadAsync(ct)).FindConnection(name)!));
        });
        api.MapDelete("/connections/{name}", async (string name, ConfigurationRepository repository, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await repository.DeleteConnectionAsync(name, User(user), ct);
            return Results.NoContent();
        });
        api.MapPut("/connections/{name}/secret", async (string name, SecretRequest request, ConfigurationRepository repository, ClaimsPrincipal user, CancellationToken ct) =>
            await SetSecretAsync(SecretOwner.Connection, name, request, repository, user, ct));

        api.MapGet("/sftp-servers", async (ConfigurationRepository repository, CancellationToken ct) =>
            Results.Ok((await repository.LoadAsync(ct)).SftpServers.Select(ConfigurationRepository.WithoutSecrets)));
        api.MapGet("/sftp-servers/{name}", async (string name, ConfigurationRepository repository, CancellationToken ct) =>
            (await repository.LoadAsync(ct)).FindSftpServer(name) is { } s ? Results.Ok(ConfigurationRepository.WithoutSecrets(s)) : NotFound("SFTP server", name));
        api.MapPut("/sftp-servers/{name}", async (string name, SftpServerOptions server, ConfigurationRepository repository, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (Mismatch(name, server.Name) is { } problem) return problem;
            await repository.SaveSftpServerAsync(server, User(user), ct);
            return Results.Ok(ConfigurationRepository.WithoutSecrets((await repository.LoadAsync(ct)).FindSftpServer(name)!));
        });
        api.MapDelete("/sftp-servers/{name}", async (string name, ConfigurationRepository repository, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await repository.DeleteSftpServerAsync(name, User(user), ct);
            return Results.NoContent();
        });
        api.MapPut("/sftp-servers/{name}/secret", async (string name, SecretRequest request, ConfigurationRepository repository, ClaimsPrincipal user, CancellationToken ct) =>
            await SetSecretAsync(SecretOwner.SftpServer, name, request, repository, user, ct));

        // Job definitions share the route of the operational job resource; reading is in JobsApi (Viewer).
        api.MapPut("/jobs/{group}/{name}", async (string group, string name, JobDefinition job, ConfigurationRepository repository, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if ((Mismatch(name, job.Name) ?? Mismatch(group, job.Group)) is { } problem) return problem;
            await repository.SaveJobAsync(job, User(user), ct);
            return Results.Ok((await repository.LoadAsync(ct)).Jobs.First(j => string.Equals(j.Key, job.Key, StringComparison.OrdinalIgnoreCase)));
        });
        api.MapDelete("/jobs/{group}/{name}", async (string group, string name, ConfigurationRepository repository, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await repository.DeleteJobAsync(group, name, User(user), ct);
            return Results.NoContent();
        });

        api.MapPost("/config/validate", (RisConfiguration configuration) =>
        {
            var errors = ConfigurationValidator.Validate(configuration);
            return Results.Ok(new ValidationResult(errors.Count == 0, errors));
        });
        api.MapGet("/config/export", async (ConfigurationRepository repository, CancellationToken ct) => Results.Ok(await repository.ExportAsync(ct)));
        api.MapPost("/config/import", async (RisConfiguration configuration, ConfigurationRepository repository, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await repository.ImportAsync(configuration, User(user), ct);
            return Results.NoContent();
        });
        api.MapGet("/audit", async (int? take, ConfigurationRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.GetAuditAsync(Math.Clamp(take ?? 200, 1, 5000), ct)));

        // Checks run by the service account (what jobs will use).
        api.MapPost("/test/d365", async (D365TestRequest request, ConnectionTestService tests, CancellationToken ct) =>
            Results.Ok(await tests.D365Async(request.Connection, ct)));
        api.MapPost("/test/folder", async (FolderTestRequest request, ConnectionTestService tests, CancellationToken ct) =>
            Results.Ok(await tests.FolderAsync(request.Path, ct)));
        api.MapPost("/test/sftp", async (SftpTestRequest request, ConnectionTestService tests, CancellationToken ct) =>
            Results.Ok(await tests.SftpAsync(request.Server, request.RemoteFolder, ct)));
    }

    private static async Task<IResult> SetSecretAsync(SecretOwner owner, string name, SecretRequest request, ConfigurationRepository repository,
        ClaimsPrincipal user, CancellationToken ct)
    {
        if (SecretPlaceholder.IsPlaceholderOrEmpty(request.Value))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["value"] = ["Secret value is required."] });
        }
        await repository.SetSecretAsync(owner, name, request.Value, User(user), ct);
        return Results.NoContent();
    }

    private static IResult? Mismatch(string route, string body) =>
        string.Equals(route, body, StringComparison.OrdinalIgnoreCase)
            ? null
            : Results.Problem($"Name in the URL ('{route}') differs from the name in the body ('{body}'). Renaming is delete + create.",
                statusCode: StatusCodes.Status400BadRequest);

    private static string? User(ClaimsPrincipal user) => user.Identity?.Name;

    private static IResult NotFound(string what, string name) =>
        Results.Problem($"{what} '{name}' does not exist.", statusCode: StatusCodes.Status404NotFound);
}
