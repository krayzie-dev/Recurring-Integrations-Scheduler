using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using System.Xml.Linq;
using RIS.Core.Errors;
using RIS.Core.Options;
using RIS.D365;

namespace RIS.Jobs.Tests;

/// <summary>Envelope (Manifest.xml + PackageHeader.xml) generated from the data project instead of a template.</summary>
[TestClass]
public sealed class GeneratedEnvelopeTests
{
    private static DataProjectEntity Entity(string name, int level = 1, int sequence = 1, bool disabled = false) =>
        new(name, "CSV UTF", 1, level, sequence, RunBusinessValidation: true, RunBusinessLogic: true,
            FailExecutionUnitOnError: false, FailLevelOnError: false, ExcelSheetName: null, Disabled: disabled);

    private static ImportJobOptions Options() => new()
    {
        DataProject = "RIS5_TEST_IMP_Multi",
        Company = "2020",
        InputDir = @"C:\ris\in",
        UploadSuccessDir = @"C:\ris\success",
        UploadErrorsDir = @"C:\ris\errors",
        Envelope = PackageEnvelope.Generated,
        OverwriteDataProject = true
    };

    private static Dictionary<string, string> Entries(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        return zip.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open()).ReadToEnd());
    }

    private static List<(string Entity, string File, string Level)> ManifestEntities(string manifest)
    {
        var xml = XDocument.Parse(manifest);
        return xml.Descendants().Where(e => e.Name.LocalName == "DataManagementPackageEntityData")
            .Select(e => (Value(e, "EntityName"), Value(e, "InputFilePath"), Value(e, "LevelInExecutionUnit"))).ToList();

        static string Value(XElement parent, string name) => parent.Elements().Single(e => e.Name.LocalName == name).Value;
    }

    [TestMethod]
    public void Match_SingleEntityTakesAnyFile_SeveralEntitiesByName()
    {
        var single = DataPackageBuilder.Match("P", [Entity("Customer groups")], ["export_2026-10-28.csv"], allowPartial: false);
        Assert.AreEqual("Customer groups", single.Single().Entity.EntityName);

        var entities = new[] { Entity("Vendor groups", level: 2), Entity("Customer groups"), Entity("Old entity", disabled: true) };
        var parts = DataPackageBuilder.Match("P", entities, ["vendor_groups.csv", "CustomerGroups.CSV"], allowPartial: false);
        CollectionAssert.AreEqual(new[] { "Customer groups", "Vendor groups" }, parts.Select(p => p.Entity.EntityName).ToArray(), "Execution order, disabled entities ignored.");
        Assert.AreEqual("CustomerGroups.CSV", parts[0].FileName);
    }

    [TestMethod]
    public void Match_ReportsMissingUnmatchedAndDuplicateFiles()
    {
        var entities = new[] { Entity("Customer groups"), Entity("Vendor groups", level: 2) };

        var missing = Assert.ThrowsExactly<RisException>(() => DataPackageBuilder.Match("P", entities, ["Customer groups.csv"], allowPartial: false));
        Assert.AreEqual(ErrorCodes.PackageFilesMismatch, missing.ErrorCode);
        StringAssert.Contains(missing.Message, "entities without file: Vendor groups");
        Assert.AreEqual(1, DataPackageBuilder.Match("P", entities, ["Customer groups.csv"], allowPartial: true).Count);

        var unmatched = Assert.ThrowsExactly<RisException>(() => DataPackageBuilder.Match("P", entities, ["Customer groups.csv", "Vendor groups.csv", "notes.txt"], allowPartial: false));
        StringAssert.Contains(unmatched.Message, "files without entity: notes.txt");

        var duplicate = Assert.ThrowsExactly<RisException>(() => DataPackageBuilder.Match("P", entities, ["Customer groups.csv", "customer_groups.txt", "Vendor groups.csv"], allowPartial: false));
        StringAssert.Contains(duplicate.Message, "several files for one entity");
    }

    [TestMethod]
    public void Manifest_HasTheElementsD365Needs_InDataContractOrder()
    {
        var manifest = DataPackageBuilder.Manifest("RIS5_TEST_IMP_Multi", [new PackagePart(Entity("Customer groups"), "Customer groups.csv")]);
        var entity = manifest.Descendants().Single(e => e.Name.LocalName == "DataManagementPackageEntityData");
        var names = entity.Elements().Select(e => e.Name.LocalName).ToList();
        CollectionAssert.AreEqual(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names, "DataContract serializer reads elements in alphabetical order.");
        CollectionAssert.IsSubsetOf(new[] { "EntityName", "InputFilePath", "SourceFormat", "ExecutionUnit", "LevelInExecutionUnit", "SequenceInLevel" }, names);
        Assert.AreEqual("RIS5_TEST_IMP_Multi", manifest.Root!.Elements().First().Value);
        Assert.AreEqual("DefinitionGroup", DataPackageBuilder.Header("P").Descendants().Single(e => e.Name.LocalName == "PackageType").Value);
    }

    [TestMethod]
    public async Task SingleFile_IsWrappedInGeneratedEnvelope()
    {
        using var host = new JobsTestHost();
        host.D365.ProjectEntities.Add(Entity("Customer groups"));
        host.FileSystem.AddFile(@"C:\ris\in\groups_20261028.csv", new MockFileData("CUSTOMERGROUPID;DESCRIPTION\r\nRIS5T1;Test"));
        var options = Options();
        options.DataProject = "RIS5_TEST_IMP_CustGroups";

        var outcome = await host.RunAsync(options);

        Assert.AreEqual(JobOutcomeStatus.Succeeded, outcome.Status);
        var entries = Entries(host.D365.UploadedPackages.Single().Package);
        CollectionAssert.AreEquivalent(new[] { "groups_20261028.csv", "Manifest.xml", "PackageHeader.xml" }, entries.Keys.ToArray());
        Assert.AreEqual(("Customer groups", "groups_20261028.csv", "1"), ManifestEntities(entries["Manifest.xml"]).Single());
        Assert.AreEqual(1, host.D365.Calls.Count(c => c == "GetDataProjectEntities RIS5_TEST_IMP_CustGroups"));
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\success\groups_20261028.csv"));
    }

    [TestMethod]
    public async Task ZipWithDataFilesOfSeveralEntities_BecomesOnePackage_ZipWithManifestIsKept()
    {
        using var host = new JobsTestHost();
        host.D365.ProjectEntities.AddRange([Entity("Customer groups"), Entity("Vendor groups", level: 2)]);
        host.FileSystem.AddFile(@"C:\ris\in\a-batch.zip", new MockFileData(TestFiles.Zip(("Vendor groups.csv", "V"), ("Customer groups.csv", "C"))));
        host.FileSystem.AddFile(@"C:\ris\in\b-ready.zip", new MockFileData(TestFiles.Zip(("Manifest.xml", TestFiles.TemplateManifest), ("PackageHeader.xml", "<h/>"), ("x.csv", "X"))));

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(JobOutcomeStatus.Succeeded, outcome.Status, outcome.Summary);
        var batch = Entries(host.D365.UploadedPackages[0].Package);
        CollectionAssert.AreEqual(new[] { ("Customer groups", "Customer groups.csv", "1"), ("Vendor groups", "Vendor groups.csv", "2") },
            ManifestEntities(batch["Manifest.xml"]));
        Assert.AreEqual("C", batch["Customer groups.csv"]);
        Assert.AreEqual(TestFiles.TemplateManifest, Entries(host.D365.UploadedPackages[1].Package)["Manifest.xml"], "A ready package is imported unchanged.");
    }

    [TestMethod]
    public async Task MissingEntityFile_FileGoesToErrors_D365WouldImportTheRestSilently()
    {
        using var host = new JobsTestHost();
        host.D365.ProjectEntities.AddRange([Entity("Customer groups"), Entity("Vendor groups", level: 2)]);
        host.FileSystem.AddFile(@"C:\ris\in\batch.zip", new MockFileData(TestFiles.Zip(("Customer groups.csv", "C"))));

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(JobOutcomeStatus.Failed, outcome.Status);
        Assert.AreEqual(ErrorCodes.PackageFilesMismatch, outcome.ErrorCode);
        Assert.AreEqual(0, host.D365.UploadedPackages.Count, "Nothing is sent to D365.");
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\errors\batch.zip"));
        StringAssert.Contains(host.FileSystem.File.ReadAllText(@"C:\ris\errors\batch.Status"), "entities without file: Vendor groups");
        Assert.AreEqual(0, host.FileSystem.Directory.GetFiles(host.FileSystem.Path.GetTempPath(), "ris-*.zip").Length, "Temporary package deleted.");
    }

    [TestMethod]
    public async Task UnknownDataProject_JobFails_FilesStayInInput()
    {
        using var host = new JobsTestHost();
        host.FileSystem.AddFile(@"C:\ris\in\a.csv", new MockFileData("A"));

        var outcome = await host.RunAsync(Options());

        Assert.AreEqual(JobOutcomeStatus.Failed, outcome.Status);
        Assert.AreEqual(ErrorCodes.DataProjectNotFound, outcome.ErrorCode);
        Assert.IsTrue(host.FileSystem.FileExists(@"C:\ris\in\a.csv"));
    }
}
