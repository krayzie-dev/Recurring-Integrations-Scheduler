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
        StringAssert.Contains(plan, "class=\"comb ts t-export", "Every 5 minutes is shown as a series (default chart style Neon).");
        StringAssert.Contains(plan, "gantt style-neon", "Chart style and theme classes on the chart.");
        StringAssert.Contains(plan, "Frequent");

        var history = await GetPageAsync("/history", "Viewer", "en-US");
        StringAssert.Contains(history, "id=\"history-gantt\"");
        StringAssert.Contains(history, "class=\"bar succeeded\"");
        StringAssert.Contains(history, "ris-history-filters", "Filter bar above the run list.");
        StringAssert.Contains(history, "Filter: job, summary, error code");
        StringAssert.Contains(await GetPageAsync("/", "Admin", "pl-PL"), "Instancje D365", "Menu: D365 instances.");
    }


    [TestMethod]
    public async Task EditorConnectionsAndAudit_AdminOnly_NoSecretsInHtml()
    {
        var admin = _factory.Client("Admin");
        (await admin.PutRisAsync("/api/v1/connections/TEST", RisTestHost.Connection)).EnsureSuccessStatusCode();
        (await admin.PutRisAsync("/api/v1/jobs/Tests/Daily",
            RisTestHost.Export("Daily", Path.Combine(_factory.DataDirectory, "export"), new ScheduleOptions { Cron = "0 0 2 * * ?" }))).EnsureSuccessStatusCode();

        var editor = await GetPageAsync("/jobs/Tests/Daily/edit", "Admin", "pl-PL");
        StringAssert.Contains(editor, "Edycja Tests.Daily");
        StringAssert.Contains(editor, "2. Dynamics 365", "Wizard steps as tabs.");
        StringAssert.Contains(editor, "pobiera pakiet do folderu", "Job type cards with descriptions.");
        StringAssert.Contains(editor, "Wybierz, co robi zadanie", "Help panel of the step.");
        Assert.IsFalse(editor.Contains("Pole wymagane"), "No required-field errors before the user tries to continue.");

        var wizard = await GetPageAsync("/jobs/new", "Admin", "en-US");
        StringAssert.Contains(wizard, "New job");
        StringAssert.Contains(wizard, "Based on an existing job");
        var copy = await GetPageAsync("/jobs/new?from=Tests.Daily", "Admin", "en-US");
        StringAssert.Contains(copy, "Daily (copy)", "Copy of an existing job.");

        var connections = await GetPageAsync("/connections", "Admin", "en-US");
        StringAssert.Contains(connections, "TEST");
        StringAssert.Contains(connections, "APP-", "The instance shows its app registration (inline sign-in of the request moved there).");
        Assert.IsFalse(connections.Contains("secret\"") || connections.Contains(">secret<"), "Secret value never rendered.");
        var registrations = await GetPageAsync("/app-registrations", "Admin", "en-US");
        StringAssert.Contains(registrations, "App registrations");
        StringAssert.Contains(registrations, "Client secret", "Sign-in method with its secret state.");
        Assert.IsFalse(registrations.Contains("Client secret: "), "The secret is set (no \"missing\" marker).");
        Assert.IsFalse(registrations.Contains("secret\"") || registrations.Contains(">secret<"), "Secret value never rendered.");
        StringAssert.Contains(registrations, "instances: 1, jobs: 0");
        Assert.IsFalse((await GetPageAsync("/app-registrations", "Operator", "en-US")).Contains("New app registration"), "Admin only.");
        StringAssert.Contains(await GetPageAsync("/sftp-servers", "Admin", "en-US"), "New SFTP server");
        // The audit grid pages on the server (rows come after the interactive circuit starts): the page itself, and the
        // entries through the same query via the API (one page, total in X-Total-Count, no secrets).
        StringAssert.Contains(await GetPageAsync("/audit", "Admin", "en-US"), "Audit");
        var audit = await _factory.Client("Admin").GetAsync("/api/v1/audit?take=1");
        var auditText = await audit.Content.ReadAsStringAsync();
        StringAssert.Contains(auditText, "Created");
        Assert.IsTrue(int.Parse(audit.Headers.GetValues("X-Total-Count").Single()) >= 2);
        Assert.IsFalse(auditText.Contains("\"secret\"", StringComparison.Ordinal));

        // Operator: no access to configuration pages.
        var operatorClient = _factory.Client("Operator");
        operatorClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        StringAssert.Contains(WebUtility.HtmlDecode(await (await operatorClient.GetAsync("/jobs/Tests/Daily/edit")).Content.ReadAsStringAsync()), "No access");
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
            // Texts formatted with arguments (also by MudBlazor) may only use {0}, {1}, ... Field help is shown without
            // arguments (never formatted) and documents file name tokens such as {timestamp}.
            var neverFormatted = key.StartsWith("FieldHint_", StringComparison.Ordinal) || key.StartsWith("FieldHelp_", StringComparison.Ordinal);
            if (!neverFormatted && System.Text.RegularExpressions.Regex.IsMatch(value, @"\{(?!\d+(:[^}]*)?\})")) problems.Add($"{key}: non-positional placeholder in '{value}'");
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
