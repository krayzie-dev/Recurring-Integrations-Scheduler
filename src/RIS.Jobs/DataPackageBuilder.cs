using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using RIS.Core.Errors;
using RIS.D365;

namespace RIS.Jobs;

/// <summary>Data file of a package and the data project entity it is imported into.</summary>
public sealed record PackagePart(DataProjectEntity Entity, string FileName);

/// <summary>
/// Builds the "envelope" of a D365 data package (Manifest.xml + PackageHeader.xml) from the entities of the data project,
/// instead of a package template. Verified against D365 (10.0.4x, 28.10.2026):
/// <list type="bullet">
/// <item>PackageHeader.xml is required (constant content);</item>
/// <item>the manifest needs per entity only name, source format, file, execution unit/level/sequence and flags; without
/// field mapping D365 maps the columns by name (any order, subset of columns allowed);</item>
/// <item>a manifest without some entities of the project imports the others and does not change the project definition;</item>
/// <item>a missing data file of a listed entity is silently skipped by D365 - therefore files are checked here.</item>
/// </list>
/// </summary>
public static class DataPackageBuilder
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/dynamics/2015/01/DataManagement";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public const string ManifestName = "Manifest.xml";
    public const string HeaderName = "PackageHeader.xml";

    /// <summary>
    /// Matches data files to the enabled entities of the project: a single file and a single entity always match; otherwise the
    /// file name without extension must equal the entity name (case and spaces ignored), e.g. "Customer groups.csv".
    /// </summary>
    /// <exception cref="RisException"><see cref="ErrorCodes.PackageFilesMismatch"/>: files without entity, entities without file
    /// (unless <paramref name="allowPartial"/>) or two files for one entity.</exception>
    public static IReadOnlyList<PackagePart> Match(string dataProject, IReadOnlyList<DataProjectEntity> entities, IReadOnlyList<string> fileNames, bool allowPartial)
    {
        var enabled = entities.Where(e => !e.Disabled).ToList();
        if (enabled.Count == 1 && fileNames.Count == 1)
        {
            return [new PackagePart(enabled[0], fileNames[0])];
        }

        var parts = new List<PackagePart>();
        var unmatched = new List<string>();
        foreach (var file in fileNames)
        {
            var key = Key(Path.GetFileNameWithoutExtension(file));
            var entity = enabled.FirstOrDefault(e => Key(e.EntityName) == key);
            if (entity is null) unmatched.Add(file);
            else parts.Add(new PackagePart(entity, file));
        }

        var duplicates = parts.GroupBy(p => p.Entity.EntityName).Where(g => g.Count() > 1).Select(g => $"{g.Key} ({string.Join(", ", g.Select(p => p.FileName))})").ToList();
        var missing = allowPartial ? [] : enabled.Where(e => parts.All(p => p.Entity != e)).Select(e => e.EntityName).ToList();
        if (unmatched.Count > 0 || missing.Count > 0 || duplicates.Count > 0)
        {
            var problems = new List<string>();
            if (unmatched.Count > 0) problems.Add($"files without entity: {string.Join(", ", unmatched)}");
            if (missing.Count > 0) problems.Add($"entities without file: {string.Join(", ", missing)}");
            if (duplicates.Count > 0) problems.Add($"several files for one entity: {string.Join("; ", duplicates)}");
            throw new RisException(ErrorCodes.PackageFilesMismatch,
                $"Files do not match the entities of data project '{dataProject}' ({string.Join(", ", enabled.Select(e => e.EntityName))}): {string.Join("; ", problems)}. "
                + "Name each file after its entity, e.g. 'Customer groups.csv'.",
                new Dictionary<string, object?> { ["DataProject"] = dataProject });
        }

        return parts.OrderBy(p => p.Entity.ExecutionUnit).ThenBy(p => p.Entity.Level).ThenBy(p => p.Entity.Sequence).ToList();
    }

    /// <summary>Writes a package (zip) with the generated envelope and the data files (<paramref name="openFile"/> by file name).</summary>
    public static void Write(Stream output, string dataProject, IReadOnlyList<PackagePart> parts, Func<string, Stream> openFile)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var part in parts)
        {
            using var data = openFile(part.FileName);
            using var entry = zip.CreateEntry(part.FileName, CompressionLevel.Fastest).Open();
            data.CopyTo(entry);
        }

        Save(zip, ManifestName, Manifest(dataProject, parts));
        Save(zip, HeaderName, Header(dataProject));
    }

    /// <summary>Manifest.xml; element order matters (DataContract serializer of D365: alphabetical).</summary>
    public static XDocument Manifest(string dataProject, IReadOnlyList<PackagePart> parts) => new(
        new XElement(Ns + "DataManagementPackageManifest",
            new XAttribute(XNamespace.Xmlns + "i", Xsi),
            new XElement(Ns + "DefinitionGroupName", dataProject),
            new XElement(Ns + "Description", dataProject),
            new XElement(Ns + "PackageEntityList", parts.Select(p => new XElement(Ns + "DataManagementPackageEntityData",
                new XElement(Ns + "DefaultRefreshType", "IncrementalPush"),
                new XElement(Ns + "Disable", "false"),
                new XElement(Ns + "EntityName", p.Entity.EntityName),
                new XElement(Ns + "ExcelSheetName", p.Entity.ExcelSheetName ?? ""),
                new XElement(Ns + "ExecutionUnit", p.Entity.ExecutionUnit),
                new XElement(Ns + "FailExecutionUnitOnError", Bool(p.Entity.FailExecutionUnitOnError)),
                new XElement(Ns + "FailLevelOnError", Bool(p.Entity.FailLevelOnError)),
                new XElement(Ns + "InputFilePath", p.FileName),
                new XElement(Ns + "LevelInExecutionUnit", p.Entity.Level),
                new XElement(Ns + "RunBusinessLogic", Bool(p.Entity.RunBusinessLogic)),
                new XElement(Ns + "RunBusinessValidation", Bool(p.Entity.RunBusinessValidation)),
                new XElement(Ns + "SequenceInLevel", p.Entity.Sequence),
                new XElement(Ns + "SourceFormat", p.Entity.SourceFormat)))),
            new XElement(Ns + "ProjectCategory", 2)));

    public static XDocument Header(string dataProject) => new(
        new XElement(Ns + "DataManagementPackageHeader",
            new XAttribute(XNamespace.Xmlns + "i", Xsi),
            new XElement(Ns + "Description", dataProject),
            new XElement(Ns + "ManifestType", "Microsoft.Dynamics.AX.Framework.Tools.DataManagement.Serialization.DataManagementPackageManifest"),
            new XElement(Ns + "PackageType", "DefinitionGroup"),
            new XElement(Ns + "PackageVersion", 2)));

    /// <summary>The entry name of the expected data file of an entity in a package, e.g. "Customer groups.csv".</summary>
    public static string ExpectedFileName(DataProjectEntity entity) =>
        entity.EntityName + (entity.SourceFormat.StartsWith("EXCEL", StringComparison.OrdinalIgnoreCase) ? ".xlsx"
            : entity.SourceFormat.StartsWith("XML", StringComparison.OrdinalIgnoreCase) ? ".xml" : ".csv");

    private static string Key(string name) => new string(name.Where(c => !char.IsWhiteSpace(c) && c is not ('_' or '-')).ToArray()).ToUpperInvariant();

    private static string Bool(bool value) => value ? "true" : "false";

    private static void Save(ZipArchive zip, string name, XDocument document)
    {
        using var stream = zip.CreateEntry(name).Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = false });
        document.Save(writer);
    }
}
