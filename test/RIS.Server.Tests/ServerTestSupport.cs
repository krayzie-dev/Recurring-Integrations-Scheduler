using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RIS.Core.Configuration;
using RIS.D365;
using RIS.Hosting.Tests;

namespace RIS.Server.Tests;

/// <summary>Authenticates every request carrying "X-Test-User"; roles come from "X-Test-Roles" (comma separated).</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user)) return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, user.ToString()));
        foreach (var role in Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>RIS server on a temporary data directory, test authentication and fake D365.</summary>
public sealed class RisServerFactory : WebApplicationFactory<Program>
{
    public RisServerFactory()
    {
        DataDirectory = Directory.CreateTempSubdirectory("ris-server-").FullName;
    }

    public string DataDirectory { get; }
    public FakeD365 D365 { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Ris:Data:DataDirectory", DataDirectory);
        builder.UseSetting("Ris:Console", "false");
        builder.UseSetting("Ris:Security:WindowsAuthentication", "false");
        builder.UseSetting("Ris:Health:RequiredJobs:Tests.Daily", "1.02:00:00");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ID365ClientFactory>(D365);
            services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = o.DefaultAuthenticateScheme = o.DefaultChallengeScheme = o.DefaultForbidScheme = TestAuthHandler.SchemeName;
            });
        });
    }

    public HttpClient Client(string? roles, string user = @"TEST\user")
    {
        var client = CreateClient();
        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-User", user);
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        }
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { }
    }
}

public static class HttpExtensions
{
    public static Task<HttpResponseMessage> PutRisAsync<T>(this HttpClient client, string url, T value) =>
        client.PutAsync(url, JsonContent.Create(value, options: RisJson.Options));
}
