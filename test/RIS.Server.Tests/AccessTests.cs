using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RIS.Server.Tests;

/// <summary>Page "Access" / API /api/v1/access: roles given to Windows groups in the application apply without a restart.</summary>
[TestClass]
public sealed class AccessTests
{
    private const string Users = "S-1-5-32-545";          // BUILTIN\Users
    private const string Administrators = "S-1-5-32-544"; // BUILTIN\Administrators
    private RisServerFactory _factory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _factory = new RisServerFactory();
        _factory.Settings["Ris:Security:Roles:Admin:0"] = Administrators;
    }

    [TestCleanup]
    public async Task Cleanup() => await _factory.DisposeAsync();

    /// <summary>A user without role claims, member of the given Windows groups (SIDs as in a Windows token).</summary>
    private HttpClient Member(params string[] groupSids)
    {
        var client = _factory.Client("", @"CONTOSO\member");
        client.DefaultRequestHeaders.Add("X-Test-Groups", string.Join(",", groupSids));
        return client;
    }

    [TestMethod]
    public async Task GroupAddedInTheApplication_GetsTheRole_WithoutRestart_AndLosesItAfterRemoval()
    {
        var admin = _factory.Client("Admin");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Member(Users).GetAsync("/api/v1/jobs")).StatusCode, "No role yet.");

        var added = await admin.PostAsJsonAsync("/api/v1/access/viewer", new { member = Users });
        Assert.AreEqual(HttpStatusCode.OK, added.StatusCode, await added.Content.ReadAsStringAsync());
        Assert.AreEqual(Users, (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sid").GetString());

        Assert.AreEqual(HttpStatusCode.OK, (await Member(Users).GetAsync("/api/v1/jobs")).StatusCode, "Viewer through the group, no restart.");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Member(Users).PostAsync("/api/v1/jobs/Tests/X/run", null)).StatusCode, "Viewer cannot operate.");

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/v1/access");
        Assert.IsTrue(list.EnumerateArray().Any(m => m.GetProperty("role").GetString() == "Viewer" && m.GetProperty("sid").GetString() == Users
                                                     && IsSource(m, RoleSource.Application)));
        Assert.IsTrue(list.EnumerateArray().Any(m => m.GetProperty("role").GetString() == "Admin" && IsSource(m, RoleSource.Settings)));

        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/access/Viewer/{Users}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Member(Users).GetAsync("/api/v1/jobs")).StatusCode, "Role removed.");

        var audit = await admin.GetStringAsync("/api/v1/audit?search=Access");
        StringAssert.Contains(audit, "Added");
        StringAssert.Contains(audit, "Removed");
    }

    private static bool IsSource(JsonElement member, RoleSource source) => member.GetProperty("source") is var value
        && (value.ValueKind == JsonValueKind.Number ? value.GetInt32() == (int)source : value.GetString() == source.ToString());

    [TestMethod]
    public async Task InvalidChanges_AreRejected_AndSettingsFileMembersCannotBeRemoved()
    {
        var admin = _factory.Client("Admin");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/access/Viewer", new { member = @"NO-SUCH-DOMAIN\nobody-ris5" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/access/Superuser", new { member = Users })).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/access/Admin", new { member = Administrators })).StatusCode, "Already in the settings file.");
        Assert.AreEqual(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/access/Admin/{Administrators}")).StatusCode, "Settings file members are read-only.");

        Assert.AreEqual(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/access/Operator", new { member = Users })).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/access/Operator", new { member = Users })).StatusCode, "Duplicate.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/v1/access/Viewer/{Users}")).StatusCode);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await _factory.Client("Operator").GetAsync("/api/v1/access")).StatusCode, "Admin only.");
        Assert.AreEqual(HttpStatusCode.OK, (await Member(Administrators).GetAsync("/api/v1/access")).StatusCode, "Admin through the settings file (SID).");
    }

    [TestMethod]
    public async Task AccessPage_AdminOnly_ShowsMembersAndOwnRoles()
    {
        var admin = _factory.Client("Admin");
        (await admin.PostAsJsonAsync("/api/v1/access/Operator", new { member = Users })).EnsureSuccessStatusCode();

        var client = Member(Administrators, Users);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pl-PL");
        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/access"));
        StringAssert.Contains(page, "Twoje uprawnienia");
        StringAssert.Contains(page, "Plik ustawień");
        StringAssert.Contains(page, "Aplikacja");
        StringAssert.Contains(page, Users);
        StringAssert.Contains(page, "Akcje");

        var operatorPage = WebUtility.HtmlDecode(await Member(Users).GetStringAsync("/access"));
        Assert.IsFalse(operatorPage.Contains("Twoje uprawnienia") || operatorPage.Contains("Your access"), "Operator does not see the page.");
    }
}
