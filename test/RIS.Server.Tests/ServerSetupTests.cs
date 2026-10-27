using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using RIS.Core.Configuration;
using RIS.Core.Errors;

namespace RIS.Server.Tests;

[TestClass]
public sealed class ServerSetupTests
{
    [TestMethod]
    public async Task MachineSettings_InDataDirectory_OverrideDefaults()
    {
        await using var factory = new RisServerFactory();
        // Written by the installer next to the databases; here: /health requires login.
        await File.WriteAllTextAsync(Path.Combine(factory.DataDirectory, "appsettings.json"), """{ "Ris": { "Health": { "AllowAnonymous": false } } }""");

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await factory.Client(null).GetAsync("/health")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await factory.Client("Viewer").GetAsync("/health")).StatusCode);

        var me = await factory.Client("Viewer").GetFromJsonAsync<JsonElement>("/api/v1/me");
        StringAssert.StartsWith(me.GetProperty("version").GetString(), "5.");
    }

    [TestMethod]
    public void Https_WithoutCertificate_FailsWithClearMessage()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Ris:Https:Port"] = "5443" });
        var error = Assert.ThrowsExactly<RisException>(builder.ConfigureHttps);
        StringAssert.Contains(error.Message, "CertificateThumbprint");

        var missing = Assert.ThrowsExactly<RisException>(() => Certificates.FindByThumbprint("00 11 22"));
        StringAssert.Contains(missing.Message, "001122");
        Assert.AreEqual(ErrorCodes.ConfigurationInvalid, missing.ErrorCode);
    }

    [TestMethod]
    [DataRow(null, 5080)]
    [DataRow("http://localhost:5099", 5099)]
    [DataRow("http://*:6000;https://*:6001", 6000)]
    public void LocalHttpPort_FromUrls(string? urls, int expected) => Assert.AreEqual(expected, ServerSetup.LocalHttpPort(urls));
}
