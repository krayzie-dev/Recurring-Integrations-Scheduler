using RIS.Core.Configuration;
using RIS.Core.Errors;
using RIS.Core.Options;

namespace RIS.Core.Tests;

/// <summary>App registrations: default of the D365 instance, override per job, inline sign-in of older configurations.</summary>
[TestClass]
public sealed class AppRegistrationTests
{
    private static AppRegistrationOptions Registration(string name, string secret, string clientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") =>
        new() { Name = name, Tenant = "contoso.onmicrosoft.com", ClientId = clientId, ClientSecret = secret };

    private static ExportJobOptions Export(string connection, string? registration = null) =>
        new() { Connection = connection, AppRegistration = registration, DataProject = "P", Company = "C", DownloadSuccessDir = @"C:\out" };

    private static RisConfiguration Configuration() => new()
    {
        AppRegistrations = [Registration("RIS-PROD", "prod-secret"), Registration("RIS-OTHER", "other-secret", "11111111-2222-3333-4444-555555555555")],
        Connections = [new D365ConnectionOptions { Name = "PROD", AosUri = new Uri("https://contoso.operations.dynamics.com"), AppRegistration = "RIS-PROD" }],
        Jobs = [new JobDefinition { Name = "A", Group = "G", Schedule = new ScheduleOptions { Cron = "0 0 2 * * ?" }, Options = Export("PROD") }]
    };

    [TestMethod]
    public void ResolveConnection_UsesTheDefaultOfTheInstance_OrTheJobOverride()
    {
        var configuration = Configuration();

        var byDefault = configuration.ResolveConnection("prod");
        Assert.AreEqual(new Uri("https://contoso.operations.dynamics.com"), byDefault.AosUri);
        Assert.AreEqual("prod-secret", byDefault.ClientSecret);
        Assert.AreEqual("RIS-PROD", byDefault.AppRegistration);

        var overridden = configuration.ResolveConnection("PROD", "ris-other");
        Assert.AreEqual("other-secret", overridden.ClientSecret);
        Assert.AreEqual("11111111-2222-3333-4444-555555555555", overridden.ClientId);
        Assert.AreEqual("PROD", overridden.Name);
        Assert.IsNull(configuration.Connections[0].ClientSecret, "The stored instance is not changed.");

        Assert.AreEqual(ErrorCodes.ConnectionNotFound, Assert.ThrowsExactly<RisException>(() => configuration.ResolveConnection("NOPE")).ErrorCode);
        Assert.AreEqual(ErrorCodes.ConnectionNotFound, Assert.ThrowsExactly<RisException>(() => configuration.ResolveConnection("PROD", "NOPE")).ErrorCode);
    }

    [TestMethod]
    public void RenewedSecret_IsSetOnce_ForEveryInstance()
    {
        var configuration = Configuration();
        configuration.Connections.Add(new D365ConnectionOptions { Name = "TEST", AosUri = new Uri("https://contoso-test.operations.dynamics.com"), AppRegistration = "RIS-PROD" });

        configuration.AppRegistrations[0].ClientSecret = "renewed";

        Assert.AreEqual("renewed", configuration.ResolveConnection("PROD").ClientSecret);
        Assert.AreEqual("renewed", configuration.ResolveConnection("TEST").ClientSecret);
    }

    [TestMethod]
    public void Validation_ChecksTheReferences()
    {
        var configuration = Configuration();
        Assert.AreEqual(0, ConfigurationValidator.Validate(configuration).Count, string.Join("; ", ConfigurationValidator.Validate(configuration)));

        configuration.Jobs[0].Options!.AppRegistration = "MISSING-JOB";
        configuration.Connections[0].AppRegistration = "MISSING-INSTANCE";
        var errors = ConfigurationValidator.Validate(configuration);
        Assert.IsTrue(errors.Any(e => e.Contains("Connections[PROD].AppRegistration") && e.Contains("MISSING-INSTANCE")), string.Join("; ", errors));
        Assert.IsTrue(errors.Any(e => e.Contains("Options.AppRegistration") && e.Contains("MISSING-JOB")), string.Join("; ", errors));

        configuration.Connections[0].AppRegistration = null;
        Assert.IsTrue(ConfigurationValidator.Validate(configuration).Any(e => e.Contains("AppRegistration is required")), "No app registration and no inline sign-in.");
    }

    [TestMethod]
    public void Normalize_MovesInlineSignIn_ToSharedAppRegistrations()
    {
        // Configuration from before app registrations / RIS 4.x import: every instance has its own copy of the sign-in.
        D365ConnectionOptions Inline(string name, string secret) => new()
        {
            Name = name, AosUri = new Uri($"https://{name.ToLowerInvariant()}.operations.dynamics.com"),
            Tenant = "contoso.onmicrosoft.com", ClientId = "c72d6454-e523-4f05-ac23-8d4d89e27fe5", ClientSecret = secret
        };
        var configuration = new RisConfiguration { Connections = [Inline("UAT", "s1"), Inline("TEST", "s1"), Inline("DEV", "old")] };

        Assert.AreEqual(3, configuration.Normalize());

        CollectionAssert.AreEqual(new[] { "APP-C72D6454", "APP-C72D6454-2" }, configuration.AppRegistrations.Select(a => a.Name).ToList(),
            "Same sign-in shared; a different secret of the same app gets its own registration.");
        Assert.AreEqual("APP-C72D6454", configuration.FindConnection("TEST")!.AppRegistration);
        Assert.AreEqual("APP-C72D6454-2", configuration.FindConnection("DEV")!.AppRegistration);
        Assert.IsTrue(configuration.Connections.All(c => !c.HasInlineSignIn && c.ClientSecret is null && c.ClientId.Length == 0));
        Assert.AreEqual("s1", configuration.ResolveConnection("UAT").ClientSecret);
        Assert.AreEqual(0, ConfigurationValidator.Validate(configuration).Count(e => e.StartsWith("Connections", StringComparison.Ordinal)));
        Assert.AreEqual(0, configuration.Normalize(), "Idempotent.");
    }

    [TestMethod]
    public void InlineSignIn_StillResolves()
    {
        var configuration = new RisConfiguration
        {
            Connections = [new D365ConnectionOptions { Name = "OLD", AosUri = new Uri("https://old.operations.dynamics.com"), Tenant = "t", ClientId = Guid.NewGuid().ToString(), ClientSecret = "x" }]
        };
        Assert.AreEqual("x", configuration.ResolveConnection("OLD").ClientSecret);
    }
}
