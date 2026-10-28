using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RIS.Core.Configuration;
using RIS.Core.Options;
using RIS.Hosting.Tests;
using RIS.Server.Services;

namespace RIS.Server.Tests;

[TestClass]
public sealed class BulkEditTests
{
    private static JobDefinition Job(string cron) => new()
    {
        Name = "J",
        Group = "G",
        Schedule = new ScheduleOptions { Cron = cron },
        Options = new ExportJobOptions
        {
            Connection = "C", DataProject = "D", Company = "USMF", DownloadSuccessDir = @"\\OLD-SRV\ris\export",
            SftpOutbound = new SftpTransferOptions { Server = "S", RemoteFolder = "/old-srv/in" }
        }
    };

    [TestMethod]
    [DataRow("0 0 2 * * ?", 30, "0 30 2 * * ?")]
    [DataRow("0 45 23 * * ?", 30, "0 15 0 * * ?")]
    [DataRow("0 15 0 * * ?", -30, "0 45 23 * * ?")]
    [DataRow("0 50 * * * ?", 15, "0 5 * * * ?")]
    public void Shift_MovesCronMinutesAndHours(string cron, int minutes, string expected)
    {
        var job = Job(cron);
        var warnings = new List<string>();
        BulkEditService.Shift(job, minutes, warnings);
        Assert.AreEqual(expected, job.Schedule.Cron);
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    [DataRow("0 45 23 ? * MON-FRI")]
    [DataRow("0 0 2,14 * * ?")]
    [DataRow("0 0/15 * * * ?")]
    public void Shift_RefusesCronsThatCannotBeMovedSafely(string cron)
    {
        var job = Job(cron);
        var warnings = new List<string>();
        BulkEditService.Shift(job, 30, warnings);
        Assert.AreEqual(cron, job.Schedule.Cron, "Unchanged.");
        Assert.AreEqual(1, warnings.Count);
    }

    [TestMethod]
    public void Shift_MovesIntervalStart_AndStaggerSetsTimes()
    {
        var job = Job("0 0 2 * * ?");
        job.Schedule = new ScheduleOptions { Interval = TimeSpan.FromHours(2), StartAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2)) };
        BulkEditService.Shift(job, -15, []);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 11, 45, 0, TimeSpan.FromHours(2)), job.Schedule.StartAt);

        var cron = Job("0 0 2 * * ?");
        BulkEditService.Stagger(cron, new TimeOnly(1, 40), []);
        Assert.AreEqual("0 40 1 * * ?", cron.Schedule.Cron);
    }

    [TestMethod]
    public void ReplacePaths_ChangesFoldersInOptionsAndSections_Only()
    {
        var job = Job("0 0 2 * * ?");
        var options = (ExportJobOptions)job.Options!;
        BulkEditService.ReplacePaths(options, "old-srv", "new-srv", StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(@"\\new-srv\ris\export", options.DownloadSuccessDir);
        Assert.AreEqual("/new-srv/in", options.SftpOutbound!.RemoteFolder);
        Assert.AreEqual("D", options.DataProject, "Non-path fields are not touched.");
    }

    [TestMethod]
    public async Task Api_PreviewDoesNotSave_ApplySavesAll_InvalidSavesNothing()
    {
        await using var factory = new RisServerFactory();
        var admin = factory.Client("Admin", @"TEST\admin");
        (await admin.PutRisAsync("/api/v1/connections/TEST", RisTestHost.Connection)).EnsureSuccessStatusCode();
        foreach (var (name, cron) in new[] { ("A", "0 0 2 * * ?"), ("B", "0 0 3 * * ?") })
        {
            (await admin.PutRisAsync($"/api/v1/jobs/Tests/{name}", RisTestHost.Export(name, @"\\old\export", new ScheduleOptions { Cron = cron }))).EnsureSuccessStatusCode();
        }

        var request = new { jobs = new[] { "Tests.A", "Tests.B" }, edit = new { shiftMinutes = 30, pathFind = @"\\old", pathReplace = @"\\new", missedRuns = "Skip" } };
        var preview = await (await admin.PostAsJsonAsync("/api/v1/jobs/bulk/edit?preview=true", request)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(6, preview.GetProperty("changes").GetArrayLength(), preview.ToString());
        Assert.IsFalse(preview.GetProperty("saved").GetBoolean());
        Assert.AreEqual("0 0 2 * * ?", await CronAsync(admin, "A"), "Preview saves nothing.");

        Assert.AreEqual(HttpStatusCode.Forbidden, (await factory.Client("Operator").PostAsJsonAsync("/api/v1/jobs/bulk/edit", request)).StatusCode);
        var applied = await admin.PostAsJsonAsync("/api/v1/jobs/bulk/edit", request);
        Assert.AreEqual(HttpStatusCode.OK, applied.StatusCode, await applied.Content.ReadAsStringAsync());
        Assert.AreEqual("0 30 2 * * ?", await CronAsync(admin, "A"));
        Assert.AreEqual("0 30 3 * * ?", await CronAsync(admin, "B"));
        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/v1/audit");
        Assert.IsTrue(audit.EnumerateArray().Any(a => a.GetProperty("action").GetString() == "BulkUpdated" && a.GetProperty("user").GetString() == @"TEST\admin"));

        var invalid = new { jobs = new[] { "Tests.A", "Tests.B" }, edit = new { connection = "MISSING", shiftMinutes = 30 } };
        var rejected = await admin.PostAsJsonAsync("/api/v1/jobs/bulk/edit", invalid);
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        StringAssert.Contains(await rejected.Content.ReadAsStringAsync(), "MISSING");
        Assert.AreEqual("0 30 2 * * ?", await CronAsync(admin, "A"), "Nothing saved when any job is invalid.");
    }

    private static async Task<string?> CronAsync(HttpClient client, string name) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/Tests/{name}")).GetProperty("definition").GetProperty("schedule").GetProperty("cron").GetString();
}
