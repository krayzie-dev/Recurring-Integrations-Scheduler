using RIS.Persistence;
using RIS.Server.Services;

namespace RIS.Server.Tests;

[TestClass]
public sealed class AuditDiffTests
{
    [TestMethod]
    public void Compare_FlattensNestedFields_AndClassifiesChanges()
    {
        const string before = """{ "schedule": { "cron": "0 0 2 * * ?", "paused": false }, "options": { "type": "Export", "company": "USMF", "polling": { "timeout": "12:00:00" } }, "description": "old" }""";
        const string after = """{ "schedule": { "cron": "0 0 3 * * ?", "paused": false }, "options": { "type": "Export", "company": "USMF", "polling": { "timeout": "12:00:00" }, "unzipPackage": true } }""";

        var changes = AuditDiff.Compare(before, after)!.ToDictionary(c => c.Path);

        Assert.AreEqual(AuditChangeKind.Changed, changes["schedule.cron"].Kind);
        Assert.AreEqual("0 0 2 * * ?", changes["schedule.cron"].Before);
        Assert.AreEqual("0 0 3 * * ?", changes["schedule.cron"].After);
        Assert.AreEqual(AuditChangeKind.Same, changes["options.polling.timeout"].Kind);
        Assert.AreEqual(AuditChangeKind.Added, changes["options.unzipPackage"].Kind);
        Assert.AreEqual("true", changes["options.unzipPackage"].After);
        Assert.AreEqual(AuditChangeKind.Removed, changes["description"].Kind);
    }

    [TestMethod]
    public void Compare_CreatedDeletedAndText_OldPlaceholderShownAsHiddenSecret()
    {
        Assert.IsTrue(AuditDiff.Compare(null, """{ "name": "A" }""")!.All(c => c.Kind == AuditChangeKind.Added));
        Assert.IsTrue(AuditDiff.Compare("""{ "name": "A" }""", null)!.All(c => c.Kind == AuditChangeKind.Removed));
        Assert.IsNull(AuditDiff.Compare("""{ "name": "A" }""", "Export jobs.Renamed"), "A moved job stores the new key as text.");

        // Entries written before the secret markers contain the export placeholder (escaped by the old serializer).
        var old = AuditDiff.Compare("""{ "clientSecret": "<SET-SECRET>" }""", """{ "clientSecret": "*** (new)" }""")!.Single();
        Assert.AreEqual(ConfigurationRepository.AuditSecretStored, old.Before);
        Assert.AreEqual(AuditChangeKind.Changed, old.Kind);
        Assert.AreEqual("{\n  \"tenant\": \"żółć\"\n}".ReplaceLineEndings(), AuditDiff.Pretty("""{"tenant":"żółć"}""").ReplaceLineEndings());
    }
}
