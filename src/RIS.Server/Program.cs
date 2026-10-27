using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.WindowsServices;
using Quartz;
using RIS.Hosting;
using RIS.Server;
using RIS.Server.Api;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // As a Windows service the working directory is System32; appsettings.json lives next to the executable.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null
});

builder.Services.AddWindowsService(service => service.ServiceName = "RIS5");
builder.Services.AddRisServer(builder.Configuration, configureHost: host =>
{
    if (WindowsServiceHelpers.IsWindowsService())
    {
        host.Console = false;
        host.EventLog = true;
    }
});

var security = builder.Configuration.GetSection("Ris:Security").Get<SecurityOptions>() ?? new SecurityOptions();
builder.Services.AddSingleton(security);
builder.Services.AddSingleton<IClaimsTransformation, WindowsRoleClaimsTransformation>();
var authentication = builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme);
if (security.WindowsAuthentication) authentication.AddNegotiate();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(RisRoles.CanView, p => p.RequireRole(RisRoles.Viewer, RisRoles.Operator, RisRoles.Admin))
    .AddPolicy(RisRoles.CanOperate, p => p.RequireRole(RisRoles.Operator, RisRoles.Admin))
    .AddPolicy(RisRoles.CanAdminister, p => p.RequireRole(RisRoles.Admin));

builder.Services.ConfigureHttpJsonOptions(json =>
{
    // Same JSON as jobs.json (RisJson): camelCase, enums as strings, nulls omitted.
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<RisExceptionHandler>();
builder.Services.AddOpenApi();

var health = builder.Configuration.GetSection("Ris:Health").Get<HealthOptions>() ?? new HealthOptions();
builder.Services.AddHealthChecks()
    .AddQuartz(quartz =>
    {
        foreach (var (key, within) in health.RequiredJobs)
        {
            var separator = key.IndexOf('.');
            var job = separator > 0 ? new JobKey(key[(separator + 1)..], key[..separator]) : new JobKey(key, "Default");
            quartz.RequireSuccessWithin(job, within, HealthStatus.Unhealthy);
        }
    })
    .AddCheck<DataHealthCheck>("data");

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi().RequireAuthorization(RisRoles.CanView);
var healthEndpoint = app.MapHealthChecks("/health");
if (!health.AllowAnonymous) healthEndpoint.RequireAuthorization(RisRoles.CanView);
app.MapJobsApi();
app.MapConfigApi();
app.MapGet("/api/v1/me", (System.Security.Claims.ClaimsPrincipal user) => Results.Ok(new
{
    name = user.Identity?.Name,
    roles = new[] { RisRoles.Viewer, RisRoles.Operator, RisRoles.Admin }.Where(user.IsInRole)
})).RequireAuthorization();

app.Run();

/// <summary>Entry point; public for WebApplicationFactory in tests.</summary>
public partial class Program;
