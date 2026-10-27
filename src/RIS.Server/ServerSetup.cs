using System.Reflection;
using RIS.Core.Configuration;
using RIS.Core.Errors;
using RIS.Persistence;

namespace RIS.Server;

/// <summary>HTTPS endpoint for remote access (appsettings "Ris:Https"); off when <see cref="Port"/> is 0.</summary>
public sealed class HttpsOptions
{
    public int Port { get; set; }

    /// <summary>Certificate with private key in LocalMachine\My; the service account needs read access to the key.</summary>
    public string? CertificateThumbprint { get; set; }
}

public static class ServerSetup
{
    /// <summary>Product version, e.g. "5.0.0" or "5.0.0-ci.42" (build metadata after '+' removed).</summary>
    public static string Version { get; } =
        typeof(ServerSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ServerSetup).Assembly.GetName().Version?.ToString() ?? "?";

    /// <summary>
    /// Adds the machine settings file &lt;data directory&gt;\appsettings.json (written by the installer: ports, HTTPS, roles).
    /// It lives outside the program folder, so upgrades keep it. Environment variables and command line still win.
    /// </summary>
    public static string AddMachineSettings(this WebApplicationBuilder builder, string[] args)
    {
        var dataDirectory = builder.Configuration["Ris:Data:DataDirectory"];
        if (string.IsNullOrWhiteSpace(dataDirectory)) dataDirectory = new PersistenceOptions().DataDirectory;
        var path = Path.Combine(dataDirectory, "appsettings.json");
        builder.Configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);
        return path;
    }

    /// <summary>
    /// With "Ris:Https:Port" set: HTTPS on all interfaces with the certificate from the store, plus HTTP on localhost
    /// (port of the first "Urls" entry, default 5080) for local tools and monitoring.
    /// </summary>
    public static void ConfigureHttps(this WebApplicationBuilder builder)
    {
        var https = builder.Configuration.GetSection("Ris:Https").Get<HttpsOptions>() ?? new HttpsOptions();
        if (https.Port <= 0) return;
        if (string.IsNullOrWhiteSpace(https.CertificateThumbprint))
        {
            throw new RisException(ErrorCodes.ConfigurationInvalid, "Ris:Https:Port is set but Ris:Https:CertificateThumbprint is empty.");
        }

        var certificate = Certificates.FindByThumbprint(https.CertificateThumbprint);
        var httpPort = LocalHttpPort(builder.Configuration["Urls"]);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenLocalhost(httpPort);
            kestrel.ListenAnyIP(https.Port, listen => listen.UseHttps(certificate));
        });
    }

    internal static int LocalHttpPort(string? urls)
    {
        var first = urls?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return first is not null && Uri.TryCreate(first.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri) && uri.Port > 0
            ? uri.Port
            : 5080;
    }
}
