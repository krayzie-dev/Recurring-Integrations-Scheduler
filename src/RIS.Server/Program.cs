using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.WindowsServices;
using Quartz;
using RIS.Hosting;
using RIS.Server;
using MudBlazor;
using MudBlazor.Services;
using RIS.Server.Api;
using RIS.Server.Ui;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // As a Windows service the working directory is System32; appsettings.json lives next to the executable.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null
});

builder.Services.AddWindowsService(service => service.ServiceName = "RIS5");
// Static files of packages (MudBlazor, blazor.web.js) also when started from the build output outside Development;
// a published app has no runtime manifest and this does nothing.
builder.WebHost.UseStaticWebAssets();
// Serilog (console, files, Event Log) replaces the default providers; must run before AddRisServer (Quartz log capture provider).
builder.Logging.ClearProviders();
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
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, RisAuthorizationResultHandler>();
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
// Web UI (Blazor, interactive server rendering) with MudBlazor; PL/EN texts in Resources/UiText*.resx.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddLocalization();
builder.Services.AddMudServices();
builder.Services.AddTransient<MudLocalizer, RisMudLocalizer>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<RisExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddScoped<RIS.Server.Services.JobQueryService>();
builder.Services.AddScoped<RIS.Server.Services.JobOperationsService>();
builder.Services.AddScoped<RIS.Server.Services.DashboardService>();
builder.Services.AddScoped<RIS.Server.Services.SchedulePlanService>();
builder.Services.AddScoped<RIS.Server.Services.HistoryTimelineService>();
builder.Services.AddScoped<RIS.Server.Services.ExportService>();
builder.Services.AddScoped<RIS.Server.Services.BulkEditService>();

var health = builder.Configuration.GetSection("Ris:Health").Get<HealthOptions>() ?? new HealthOptions();
builder.Services.AddSingleton(health);
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
app.UseRequestLocalization(RisCultures.Options());
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapCultureEndpoint();
app.MapPreferencesEndpoint();
app.MapRazorComponents<RIS.Server.Components.App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.MapOpenApi().RequireAuthorization(RisRoles.CanView);
var healthEndpoint = app.MapHealthChecks("/health");
if (!health.AllowAnonymous) healthEndpoint.RequireAuthorization(RisRoles.CanView);
app.MapJobsApi();
app.MapConfigApi();
app.MapExportApi();
app.MapGet("/api/v1/me", (System.Security.Claims.ClaimsPrincipal user) => Results.Ok(new
{
    name = user.Identity?.Name,
    roles = new[] { RisRoles.Viewer, RisRoles.Operator, RisRoles.Admin }.Where(user.IsInRole)
})).RequireAuthorization();

app.Run();

/// <summary>Entry point; public for WebApplicationFactory in tests.</summary>
public partial class Program;
