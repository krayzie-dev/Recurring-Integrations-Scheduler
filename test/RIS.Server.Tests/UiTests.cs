using System.Net;
using System.Xml.Linq;

namespace RIS.Server.Tests;

[TestClass]
public sealed class UiTests
{
    private RisServerFactory _factory = null!;

    [TestInitialize]
    public void Initialize() => _factory = new RisServerFactory();

    [TestCleanup]
    public async Task Cleanup() => await _factory.DisposeAsync();

    private async Task<string> GetPageAsync(string url, string roles, string? language = null, string? cultureCookie = null)
    {
        var client = _factory.Client(roles);
        if (language is not null) client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        if (cultureCookie is not null) client.DefaultRequestHeaders.Add("Cookie", $".AspNetCore.Culture=c={cultureCookie}|uic={cultureCookie}");
        var response = await client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, url);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Layout_FollowsBrowserLanguage_AndCultureCookie()
    {
        StringAssert.Contains(await GetPageAsync("/", "Viewer", "pl-PL"), "Pulpit");
        StringAssert.Contains(await GetPageAsync("/", "Viewer", "en-US"), "Dashboard");
        StringAssert.Contains(await GetPageAsync("/", "Viewer", "en-US", cultureCookie: "pl-PL"), "Pulpit", "Cookie wins over the browser language.");
        StringAssert.Contains(await GetPageAsync("/", "Viewer", "de-DE"), "Dashboard", "Unsupported language falls back to English.");
    }

    [TestMethod]
    public async Task UserWithoutRole_SeesNoAccessPage()
    {
        var client = _factory.Client("");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pl-PL");
        var page = WebUtility.HtmlDecode(await (await client.GetAsync("/")).Content.ReadAsStringAsync());
        StringAssert.Contains(page, "Brak dostępu");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/jobs")).StatusCode, "API keeps 403.");
    }

    [TestMethod]
    public async Task AdminMenu_OnlyForAdmin()
    {
        Assert.IsFalse((await GetPageAsync("/", "Operator", "en-US")).Contains("href=\"/audit\""));
        StringAssert.Contains(await GetPageAsync("/", "Admin", "en-US"), "href=\"/audit\"");
    }

    [TestMethod]
    public async Task CultureEndpoint_SetsCookie_AndRedirectsLocallyOnly()
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", "u");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Viewer");

        var response = await client.GetAsync("/culture/set?culture=pl-PL&redirect=%2Fjobs");
        Assert.AreEqual("/jobs", response.Headers.Location?.ToString());
        StringAssert.Contains(string.Join(";", response.Headers.GetValues("Set-Cookie")), "pl-PL");

        var external = await client.GetAsync("/culture/set?culture=pl-PL&redirect=https%3A%2F%2Fevil.example");
        Assert.AreEqual("/", external.Headers.Location?.ToString());
    }

    [TestMethod]
    public void Translations_AreComplete()
    {
        var folder = Path.Combine(RepoRoot(), "src", "RIS.Server", "Resources");
        static HashSet<string> Keys(string file) =>
            XDocument.Load(file).Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();

        var english = Keys(Path.Combine(folder, "UiText.resx"));
        var polish = Keys(Path.Combine(folder, "UiText.pl.resx"));
        var missingInPolish = english.Except(polish).ToList();
        // "Mud*" keys exist only in Polish: English texts come from MudBlazor.
        var missingInEnglish = polish.Where(k => !k.StartsWith("Mud", StringComparison.Ordinal)).Except(english).ToList();

        Assert.AreEqual(0, missingInPolish.Count, "Missing in UiText.pl.resx: " + string.Join(", ", missingInPolish));
        Assert.AreEqual(0, missingInEnglish.Count, "Missing in UiText.resx: " + string.Join(", ", missingInEnglish));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RIS5.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
