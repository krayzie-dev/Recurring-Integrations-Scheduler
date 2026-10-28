namespace RIS.Hosting.Tests;

/// <summary>Windows account lookup for the "Access" page: every way of writing a local group of the server is accepted.</summary>
[TestClass]
public sealed class WindowsAccountsTests
{
    private const string Administrators = "S-1-5-32-544";

    [TestMethod]
    public void LocalGroup_IsFound_ByName_DotPrefix_ComputerPrefix_AndSid()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows only.");

        // The name depends on the language of Windows (BUILTIN\Administrators, BUILTIN\Administratorzy, ...).
        var bySid = WindowsAccounts.Resolve(Administrators);
        Assert.IsNotNull(bySid);
        Assert.IsTrue(bySid.IsGroup);
        StringAssert.Contains(bySid.Name, "\\");
        var name = bySid.Name[(bySid.Name.IndexOf('\\') + 1)..];

        foreach (var form in new[] { name, bySid.Name, @".\" + name, Environment.MachineName + "\\" + name })
        {
            var account = WindowsAccounts.Resolve(form);
            Assert.IsNotNull(account, form);
            Assert.AreEqual(Administrators, account.Sid, form);
            Assert.AreEqual(bySid.Name, account.Name, "Canonical name for " + form);
        }

        Assert.IsNull(WindowsAccounts.Resolve(@".\no-such-group-ris5"));
        Assert.IsNull(WindowsAccounts.Resolve(@"NO-SUCH-DOMAIN\no-such-group-ris5"));
    }

    [TestMethod]
    public void LocalGroups_AreListed_WithCanonicalNames()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows only.");

        var groups = WindowsAccounts.LocalGroups();
        CollectionAssert.Contains(groups.Select(g => g.Name).ToList(), WindowsAccounts.Resolve(Administrators)!.Name);
    }
}
