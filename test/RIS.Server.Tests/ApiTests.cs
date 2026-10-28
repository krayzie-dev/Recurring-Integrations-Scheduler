using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RIS.Core.Configuration;
using RIS.Core.Options;
using RIS.Hosting.Tests;

namespace RIS.Server.Tests;

[TestClass]
public sealed class ApiTests
{
    private RisServerFactory _factory = null!;

    [TestInitialize]
    public void Initialize() => _factory = new RisServerFactory();

    [TestCleanup]
    public async Task Cleanup() => await _factory.DisposeAsync();

    private string ExportDirectory => Path.Combine(_factory.DataDirectory, "export");

    private async Task ConfigureDailyJobAsync(HttpClient admin)
    {
        (await admin.PutRisAsync("/api/v1/connections/TEST", RisTestHost.Connection)).EnsureSuccessStatusCode();
        (await admin.PutRisAsync("/api/v1/jobs/Tests/Daily",
            RisTestHost.Export("Daily", ExportDirectory, new ScheduleOptions { Cron = "0 0 2 * * ?" }))).EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task Roles_AreEnforced()
    {
        var admin = _factory.Client("Admin");
        await ConfigureDailyJobAsync(admin);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await _factory.Client(null).GetAsync("/api/v1/jobs")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _factory.Client("").GetAsync("/api/v1/jobs")).StatusCode, "Authenticated user without a RIS role.");

        var viewer = _factory.Client("Viewer");
        Assert.AreEqual(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/jobs")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v1/jobs/Tests/Daily/run", null)).StatusCode);

        var operatorClient = _factory.Client("Operator");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await operatorClient.GetAsync("/api/v1/connections")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await operatorClient.DeleteAsync("/api/v1/jobs/Tests/Daily")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await operatorClient.PostAsJsonAsync("/api/v1/jobs/Tests/Daily/pause", new { reason = "test" })).StatusCode);
    }

    [TestMethod]
    public async Task Connection_SecretIsNeverReturned_NorAudited()
    {
        var admin = _factory.Client("Admin", @"TEST\admin");
        var registration = new AppRegistrationOptions { Name = "TEST-APP", Tenant = "contoso.onmicrosoft.com", ClientId = Guid.NewGuid().ToString(), ClientSecret = "initial-secret-value" };
        var response = await admin.PutRisAsync("/api/v1/app-registrations/TEST-APP", registration);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, SecretPlaceholder.Value);
        Assert.IsFalse(body.Contains("initial-secret-value"), body);
        var instance = await admin.PutRisAsync("/api/v1/connections/TEST",
            new D365ConnectionOptions { Name = "TEST", AosUri = RisTestHost.Connection.AosUri, AppRegistration = "TEST-APP" });
        StringAssert.Contains(await instance.Content.ReadAsStringAsync(), "TEST-APP");

        // Secret of the app registration, also through the instance (older clients, ris db set-secret connection).
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/v1/app-registrations/TEST-APP/secret", new { value = "new-secret-value" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/v1/connections/TEST/secret", new { value = "newer-secret-value" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/connections/TEST/secret", new { value = "" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.DeleteAsync("/api/v1/app-registrations/TEST-APP")).StatusCode, "Used by an instance.");

        foreach (var url in new[] { "/api/v1/app-registrations", "/api/v1/app-registrations/TEST-APP", "/api/v1/connections", "/api/v1/connections/TEST", "/api/v1/config/export", "/api/v1/audit" })
        {
            var text = await admin.GetStringAsync(url);
            Assert.IsFalse(text.Contains("secret-value") || text.Contains("\"secret\""), $"{url} leaks a secret: {text}");
        }

        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/v1/audit");
        Assert.IsTrue(audit.EnumerateArray().Any(a => a.GetProperty("action").GetString() == "SecretChanged" && a.GetProperty("user").GetString() == @"TEST\admin"));
    }

    [TestMethod]
    public async Task InvalidChanges_Return400_WithErrors()
    {
        var admin = _factory.Client("Admin");
        await ConfigureDailyJobAsync(admin);

        var response = await admin.PutRisAsync("/api/v1/jobs/Tests/Broken",
            RisTestHost.Export("Broken", ExportDirectory, new ScheduleOptions { Interval = TimeSpan.FromHours(2) }));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errors = problem.GetProperty("errors").GetProperty("configuration").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.IsTrue(errors.Any(e => e!.Contains("StartAt")), string.Join(" | ", errors));
        Assert.AreEqual("RIS-CFG-001", problem.GetProperty("errorCode").GetString());

        var delete = await admin.DeleteAsync("/api/v1/connections/TEST");
        Assert.AreEqual(HttpStatusCode.BadRequest, delete.StatusCode, "Connection used by a job cannot be deleted.");

        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/v1/jobs/Tests/Missing")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PutRisAsync("/api/v1/jobs/Tests/Other",
            RisTestHost.Export("Daily", ExportDirectory, new ScheduleOptions { Cron = "0 0 2 * * ?" }))).StatusCode, "Name mismatch.");
    }

    [TestMethod]
    public async Task Jobs_ListRunPauseAndHistory()
    {
        var admin = _factory.Client("Admin");
        await ConfigureDailyJobAsync(admin);
        var operatorClient = _factory.Client("Operator", @"TEST\operator");

        var job = await WaitForAsync(async () =>
        {
            var jobs = await operatorClient.GetFromJsonAsync<JsonElement>("/api/v1/jobs");
            var daily = jobs.EnumerateArray().Single();
            return daily.TryGetProperty("nextRunUtc", out _) ? daily : (JsonElement?)null;
        });
        Assert.AreEqual("Tests.Daily", job.GetProperty("key").GetString());
        Assert.AreEqual("Normal", job.GetProperty("state").GetString());

        Assert.AreEqual(HttpStatusCode.Accepted, (await operatorClient.PostAsync("/api/v1/jobs/Tests/Daily/run", null)).StatusCode);
        var run = await WaitForAsync(async () =>
        {
            var history = await operatorClient.GetFromJsonAsync<JsonElement>("/api/v1/jobs/Tests/Daily/history");
            return history.GetArrayLength() == 1 ? history[0] : (JsonElement?)null;
        });
        Assert.AreEqual("Succeeded", run.GetProperty("result").GetString(), run.ToString());
        Assert.IsTrue(run.GetProperty("manual").GetBoolean());

        var details = await operatorClient.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{run.GetProperty("runId").GetString()}");
        StringAssert.Contains(details.GetProperty("log").GetString(), "Daily");
        Assert.AreEqual(HttpStatusCode.NotFound, (await operatorClient.GetAsync("/api/v1/runs/unknown")).StatusCode);

        Assert.AreEqual(HttpStatusCode.NoContent, (await operatorClient.PostAsJsonAsync("/api/v1/jobs/Tests/Daily/pause", new { reason = "D365 maintenance" })).StatusCode);
        var paused = await operatorClient.GetFromJsonAsync<JsonElement>("/api/v1/jobs/Tests/Daily");
        Assert.AreEqual("Paused", paused.GetProperty("summary").GetProperty("state").GetString());
        Assert.AreEqual(5, paused.GetProperty("upcomingRunsUtc").GetArrayLength());

        Assert.AreEqual(HttpStatusCode.NoContent, (await operatorClient.PostAsync("/api/v1/jobs/Tests/Daily/resume", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await operatorClient.PostAsync("/api/v1/jobs/Tests/Missing/run", null)).StatusCode);
    }

    [TestMethod]
    public async Task Health_IsAnonymous_AndReportsRequiredJob()
    {
        var anonymous = _factory.Client(null);
        var response = await anonymous.GetAsync("/health");
        // The required job has no recorded success yet; it is judged from the first evaluation, so the report is healthy.
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task ConfigValidateAndExport()
    {
        var admin = _factory.Client("Admin");
        await ConfigureDailyJobAsync(admin);

        var export = await admin.GetFromJsonAsync<JsonElement>("/api/v1/config/export");
        Assert.AreEqual(1, export.GetProperty("jobs").GetArrayLength());
        Assert.AreEqual(SecretPlaceholder.Value, export.GetProperty("appRegistrations")[0].GetProperty("clientSecret").GetString());
        Assert.IsFalse(export.GetProperty("connections")[0].TryGetProperty("clientSecret", out var inline) && inline.ValueKind == JsonValueKind.String,
            "The instance has no secret of its own (the inline sign-in of the request was moved to an app registration).");

        var invalid = new RisConfiguration { Jobs = [RisTestHost.Export("X", ExportDirectory, new ScheduleOptions { Cron = "0 0 2 * * ?" })] };
        var result = await (await admin.PostAsync("/api/v1/config/validate", JsonContent.Create(invalid, options: RisJson.Options))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsFalse(result.GetProperty("isValid").GetBoolean());
        Assert.IsTrue(result.GetProperty("errors").GetArrayLength() > 0);

        var me = await admin.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.AreEqual("Admin", me.GetProperty("roles")[0].GetString());
    }

    [TestMethod]
    public async Task BulkOperations_PerJobResults_AndRoles()
    {
        var admin = _factory.Client("Admin");
        await ConfigureDailyJobAsync(admin);
        (await admin.PutRisAsync("/api/v1/jobs/Tests/Second",
            RisTestHost.Export("Second", ExportDirectory, new ScheduleOptions { Cron = "0 0 3 * * ?" }))).EnsureSuccessStatusCode();
        var operatorClient = _factory.Client("Operator", @"TEST\operator");
        await WaitForAsync(async () =>
        {
            var jobs = await operatorClient.GetFromJsonAsync<JsonElement>("/api/v1/jobs");
            return jobs.EnumerateArray().All(j => j.GetProperty("state").GetString() == "Normal") ? jobs : (JsonElement?)null;
        });

        var request = new { jobs = new[] { "Tests.Daily", "Tests.Second", "Tests.Missing" }, reason = "D365 upgrade" };
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _factory.Client("Viewer").PostAsJsonAsync("/api/v1/jobs/bulk/pause", request)).StatusCode);
        var results = await (await operatorClient.PostAsJsonAsync("/api/v1/jobs/bulk/pause", request)).Content.ReadFromJsonAsync<JsonElement>();
        CollectionAssert.AreEqual(new[] { true, true, false }, results.EnumerateArray().Select(r => r.GetProperty("succeeded").GetBoolean()).ToArray());

        var jobs = (await operatorClient.GetFromJsonAsync<JsonElement>("/api/v1/jobs")).EnumerateArray().ToList();
        Assert.IsTrue(jobs.All(j => j.GetProperty("state").GetString() == "Paused"));
        Assert.IsTrue(jobs.All(j => j.GetProperty("pauseReason").GetString() == "D365 upgrade" && j.GetProperty("pausedBy").GetString() == @"TEST\operator"));

        Assert.AreEqual(HttpStatusCode.OK, (await operatorClient.PostAsJsonAsync("/api/v1/jobs/bulk/resume", request)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await operatorClient.PostAsJsonAsync("/api/v1/jobs/bulk/delete", request)).StatusCode);
        var deleted = await (await admin.PostAsJsonAsync("/api/v1/jobs/bulk/delete", new { jobs = new[] { "Tests.Second" } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(deleted[0].GetProperty("succeeded").GetBoolean());

        Assert.AreEqual(HttpStatusCode.Accepted, (await operatorClient.PostAsync("/api/v1/jobs/Tests/Daily/run", null)).StatusCode);
        var history = await WaitForAsync(async () =>
        {
            var runs = await operatorClient.GetFromJsonAsync<JsonElement>($"/api/v1/history?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"))}&result=Succeeded");
            return runs.GetArrayLength() == 1 ? runs : (JsonElement?)null;
        });
        Assert.AreEqual("Tests.Daily", history[0].GetProperty("job").GetString());
    }


    private static async Task<JsonElement> WaitForAsync(Func<Task<JsonElement?>> probe)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(100);
        }
        throw new AssertFailedException("Condition not met in time.");
    }

    [TestMethod]
    public async Task PutJob_TypeAnywhereInOptions_AndMissingTypeIs400()
    {
        await using var factory = new RisServerFactory();
        var admin = factory.Client("Admin");
        (await admin.PutRisAsync("/api/v1/connections/TEST", RisTestHost.Connection)).EnsureSuccessStatusCode();

        // "type" after the other option fields (e.g. JSON built from a PowerShell hashtable).
        const string job = """
            { "name": "Late type", "group": "Tests", "schedule": { "cron": "0 0 2 * * ?" },
              "options": { "connection": "TEST", "dataProject": "Customers", "company": "USMF", "downloadSuccessDir": "C:\\ris\\out", "type": "Export" } }
            """;
        var ok = await admin.PutAsync("/api/v1/jobs/Tests/Late%20type", new StringContent(job, System.Text.Encoding.UTF8, "application/json"));
        Assert.IsTrue(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());

        // Broken JSON (unescaped backslash) is a client error too.
        var broken = await admin.PutAsync("/api/v1/jobs/Tests/Broken", new StringContent(job.Replace(@"C:\\ris\\out", @"C:\ris\out"), System.Text.Encoding.UTF8, "application/json"));
        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, broken.StatusCode, await broken.Content.ReadAsStringAsync());

        var missing = await admin.PutAsync("/api/v1/jobs/Tests/No%20type",
            new StringContent(job.Replace(", \"type\": \"Export\"", "").Replace("Late type", "No type"), System.Text.Encoding.UTF8, "application/json"));
        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, missing.StatusCode);
        StringAssert.Contains(await missing.Content.ReadAsStringAsync(), "type discriminator");
    }

    [TestMethod]
    [DataRow("""{ "jobs": null }""", 204)]
    [DataRow("""{ "jobs": [ null ] }""", 400)]
    [DataRow("""{ "connections": [ null ], "jobs": [] }""", 400)]
    public async Task ImportConfiguration_EmptyListsAndItems(string body, int expected)
    {
        await using var factory = new RisServerFactory();
        var response = await factory.Client("Admin").PostAsync("/api/v1/config/import", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.AreEqual(expected, (int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task JobList_MoreThanOnePageOfTriggers_AllJobsWithState()
    {
        // Quartz queries return 250 items unless told otherwise: the list must not be cut there.
        var admin = _factory.Client("Admin");
        (await admin.PutRisAsync("/api/v1/connections/TEST", RisTestHost.Connection)).EnsureSuccessStatusCode();
        var jobs = Enumerable.Range(1, 300).Select(i => RisTestHost.Export($"Job {i:000}", ExportDirectory, new ScheduleOptions { Cron = "0 0 2 * * ?" })).ToList();
        (await admin.PostAsJsonAsync("/api/v1/config/import", new { jobs }, RisJson.Options)).EnsureSuccessStatusCode();

        System.Text.Json.JsonElement[] list = [];
        for (var i = 0; i < 100; i++)
        {
            list = (await admin.GetFromJsonAsync<System.Text.Json.JsonElement[]>("/api/v1/jobs"))!;
            if (list.Length == 300 && list.All(j => j.GetProperty("state").GetString() == "Normal")) break;
            await Task.Delay(200);
        }
        Assert.AreEqual(300, list.Length);
        Assert.AreEqual(300, list.Count(j => j.GetProperty("state").GetString() == "Normal"), "Every job has its trigger state.");
        Assert.IsTrue(list.All(j => j.GetProperty("nextRunUtc").ValueKind == System.Text.Json.JsonValueKind.String));
    }
}
