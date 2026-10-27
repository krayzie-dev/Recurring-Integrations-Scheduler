using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RIS.Core.Options;
using RIS.D365;
using RIS.Hosting.Tests;
using RIS.Server.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace RIS.Server.Tests;

[TestClass]
public sealed class ConnectionTestTests
{
    private sealed class FakeTokens : ID365TokenProvider
    {
        public Task<string> GetAuthorizationHeaderAsync(D365ConnectionOptions connection, CancellationToken cancellationToken) => Task.FromResult("Bearer test-token");
    }

    private static ServiceProvider TestServices()
    {
        var services = new ServiceCollection().AddLogging().AddLocalization();
        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() =>
            new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator }));
        return services.BuildServiceProvider();
    }

    [TestMethod]
    public async Task Folder_ExistingMissingRelative_AndAdminOnly()
    {
        await using var factory = new RisServerFactory();
        var admin = factory.Client("Admin");
        admin.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");

        async Task<JsonElement> Test(string? path) =>
            await (await admin.PostAsJsonAsync("/api/v1/test/folder", new { path })).Content.ReadFromJsonAsync<JsonElement>();

        var ok = await Test(factory.DataDirectory);
        Assert.IsTrue(ok.GetProperty("succeeded").GetBoolean(), ok.ToString());
        StringAssert.Contains(ok.GetProperty("message").GetString(), Environment.UserName, "The message names the account the service runs as.");
        Assert.AreEqual(0, Directory.GetFiles(factory.DataDirectory, ".ris-access-test-*").Length, "Probe file removed.");

        var missing = await Test(Path.Combine(factory.DataDirectory, "does-not-exist"));
        Assert.IsFalse(missing.GetProperty("succeeded").GetBoolean());
        StringAssert.Contains(missing.GetProperty("message").GetString(), "does not exist");

        Assert.IsFalse((await Test("relative\\folder")).GetProperty("succeeded").GetBoolean());
        Assert.AreEqual(HttpStatusCode.Forbidden, (await factory.Client("Operator").PostAsJsonAsync("/api/v1/test/folder", new { path = "C:\\" })).StatusCode);
    }

    [TestMethod]
    public async Task D365_SignsInAndReadsDataManagement()
    {
        using var d365 = WireMockServer.Start(new WireMock.Settings.WireMockServerSettings { UseSSL = true });
        d365.Given(Request.Create().WithPath("/data/DataManagementDefinitionGroups").WithHeader("Authorization", "Bearer test-token").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { value = Array.Empty<object>() }));

        // Directly against the service (the AOS must be https; WireMock uses its own certificate).
        var connection = RisTestHost.Connection;
        connection.AosUri = new Uri(d365.Url! + "/");
        using var services = TestServices();
        var tests = new ConnectionTestService(
            new RIS.Core.Configuration.StaticConfigurationStore(new RIS.Core.Configuration.RisConfiguration { Connections = [connection] }),
            new FakeTokens(), services.GetRequiredService<IHttpClientFactory>(), null!,
            services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<RIS.Server.Resources.UiText>>());

        var ok = await tests.D365Async("TEST");
        Assert.IsTrue(ok.Succeeded, ok.Message);

        d365.Reset();
        d365.Given(Request.Create().WithPath("/data/DataManagementDefinitionGroups").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody("Forbidden: missing Data management role"));
        var denied = await tests.D365Async("TEST");
        Assert.IsFalse(denied.Succeeded);
        StringAssert.Contains(denied.Message, "403");
        StringAssert.Contains(denied.Message, "missing Data management role");

        StringAssert.Contains((await tests.D365Async("NOPE")).Message, "RIS-CFG-002");
    }

    private sealed class RecordingTokens : ID365TokenProvider
    {
        public D365ConnectionOptions? Last { get; private set; }

        public Task<string> GetAuthorizationHeaderAsync(D365ConnectionOptions connection, CancellationToken cancellationToken)
        {
            Last = connection;
            return Task.FromResult("Bearer test-token");
        }
    }

    [TestMethod]
    public async Task D365_UnsavedForm_UsesStoredSecretWhenEmpty_AndRemembersNamedTests()
    {
        using var d365 = WireMockServer.Start(new WireMock.Settings.WireMockServerSettings { UseSSL = true });
        d365.Given(Request.Create().WithPath("/data/DataManagementDefinitionGroups").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { value = Array.Empty<object>() }));
        var stored = RisTestHost.Connection;
        stored.AosUri = new Uri(d365.Url! + "/");
        stored.ClientSecret = "stored-secret";
        var tokens = new RecordingTokens();
        var results = new ConnectionTestResults();
        using var services = TestServices();
        var tests = new ConnectionTestService(
            new RIS.Core.Configuration.StaticConfigurationStore(new RIS.Core.Configuration.RisConfiguration { Connections = [stored] }),
            tokens, services.GetRequiredService<IHttpClientFactory>(), null!,
            services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<RIS.Server.Resources.UiText>>(), results);

        // Edited form (other client id), secret left empty: the stored secret is used, nothing is saved.
        var form = ConnectionTestService.WithStoredSecrets(stored, null);
        form.ClientSecret = null;
        form.ClientId = Guid.NewGuid().ToString();
        Assert.IsTrue((await tests.D365Async(form, stored.Name)).Succeeded);
        Assert.AreEqual("stored-secret", tokens.Last!.ClientSecret);
        Assert.AreEqual(form.ClientId, tokens.Last.ClientId);

        form.ClientSecret = "typed-secret";
        await tests.D365Async(form, stored.Name);
        Assert.AreEqual("typed-secret", tokens.Last!.ClientSecret, "A typed secret wins over the stored one.");

        // New connection without a secret: reported before signing in.
        form.ClientSecret = null;
        var missing = await tests.D365Async(form, null);
        Assert.IsFalse(missing.Succeeded);
        StringAssert.Contains(missing.Message, "ClientSecret is required");

        Assert.IsNull(results.Get(TestTarget.D365, stored.Name), "Form tests are not remembered.");
        await tests.D365Async(stored.Name);
        Assert.IsTrue(results.Get(TestTarget.D365, stored.Name.ToLowerInvariant())!.Result.Succeeded);
    }
}
