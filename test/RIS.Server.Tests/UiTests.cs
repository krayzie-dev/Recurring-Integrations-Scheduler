using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using RIS.Core.Configuration;
using RIS.Hosting.Tests;

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
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, url + ": " + await response.Content.ReadAsStringAsync());
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
    public async Task Pages_RenderJobsDashboardDetailsAndRun()
    {
        var admin = _factory.Client("Admin");
        (await admin.PutRisAsync("/api/v1/connections/TEST", RisTestHost.Connection)).EnsureSuccessStatusCode();
        (await admin.PutRisAsync("/api/v1/jobs/Tests/Daily",
            RisTestHost.Export("Daily", Path.Combine(_factory.DataDirectory, "export"), new ScheduleOptions { Cron = "0 0 2 * * ?" }))).EnsureSuccessStatusCode();
        Assert.AreEqual(HttpStatusCode.Accepted, (await admin.PostAsync("/api/v1/jobs/Tests/Daily/run", null)).StatusCode);
        string? runId = null;
        for (var i = 0; i < 100 && runId is null; i++)
        {
            var history = await admin.GetFromJsonAsync<JsonElement>("/api/v1/jobs/Tests/Daily/history");
            runId = history.GetArrayLength() > 0 ? history[0].GetProperty("runId").GetString() : null;
            await Task.Delay(100);
        }

        var jobs = await GetPageAsync("/jobs", "Viewer", "pl-PL");
        StringAssert.Contains(jobs, "Daily");
        StringAssert.Contains(jobs, "Eksport");
        Assert.IsFalse(jobs.Contains("Uruchom teraz"), "Viewer has no operator actions.");
        StringAssert.Contains(await GetPageAsync("/jobs", "Operator", "pl-PL"), "Uruchom teraz");

        var dashboard = await GetPageAsync("/", "Viewer", "en-US");
        StringAssert.Contains(dashboard, "Needs attention");
        StringAssert.Contains(dashboard, "Runs in the last 24 h");

        var details = await GetPageAsync("/jobs/Tests/Daily", "Admin", "en-US");
        StringAssert.Contains(details, "Next runs");
        StringAssert.Contains(details, "0 0 2 * * ?");

        var run = await GetPageAsync($"/runs/{runId}", "Viewer", "en-US");
        StringAssert.Contains(run, "Job log");
        StringAssert.Contains(run, "Succeeded");
    }


    [TestMethod]
    public async Task PlanAndHistory_RenderGanttCharts()
    {
        var admin = _factory.Client("Admin");
        (await admin.PutRisAsync("/api/v1/connections/TEST", RisTestHost.Connection)).EnsureSuccessStatusCode();
        (await admin.PutRisAsync("/api/v1/jobs/Tests/Frequent", RisTestHost.Export("Frequent", Path.Combine(_factory.DataDirectory, "export"),
            new ScheduleOptions { Interval = TimeSpan.FromMinutes(5), StartAt = DateTimeOffset.UtcNow.Date }))).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/v1/jobs/Tests/Frequent/run", null)).EnsureSuccessStatusCode();
        await Task.Delay(1500);

        var plan = await GetPageAsync("/plan", "Viewer", "pl-PL");
        StringAssert.Contains(plan, "id=\"plan-gantt\"");
        StringAssert.Contains(plan, "class=\"bar band\"", "Every 5 minutes is shown as a band.");
        StringAssert.Contains(plan, "Frequent");

        var history = await GetPageAsync("/history", "Viewer", "en-US");
        StringAssert.Contains(history, "id=\"history-gantt\"");
        StringAssert.Contains(history, "class=\"bar succeeded\"");
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

    [TestMethod]
    public void Translations_HaveMatchingPositionalPlaceholders()
    {
        var folder = Path.Combine(RepoRoot(), "src", "RIS.Server", "Resources");
        static Dictionary<string, string> Values(string file) =>
            XDocument.Load(file).Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);
        static string Placeholders(string text) =>
            string.Join(",", System.Text.RegularExpressions.Regex.Matches(text, @"\{[^}]*\}").Select(m => m.Value).Distinct().Order());

        var english = Values(Path.Combine(folder, "UiText.resx"));
        var polish = Values(Path.Combine(folder, "UiText.pl.resx"));
        var problems = new List<string>();
        foreach (var (key, value) in polish)
        {
            // string.Format is used for every text (also by MudBlazor): only {0}, {1}, ... are allowed.
            if (System.Text.RegularExpressions.Regex.IsMatch(value, @"\{(?!\d+(:[^}]*)?\})")) problems.Add($"{key}: non-positional placeholder in '{value}'");
            if (english.TryGetValue(key, out var en) && Placeholders(en) != Placeholders(value)) problems.Add($"{key}: '{en}' vs '{value}'");
        }
        Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
    }


    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RIS5.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
