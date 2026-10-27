using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using RIS.Core.Errors;

namespace RIS.Jobs;

/// <summary>
/// Reads and updates Manifest.xml of D365 data packages (zip).
/// </summary>
public static class PackageManifest
{
    public const string ManifestEntryName = "Manifest.xml";

    /// <summary>Name of the data file referenced by the first InputFilePath in the manifest.</summary>
    public static string GetInputFileName(Stream package, string packageDescription)
    {
        using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = LoadManifest(zip, packageDescription);
        return manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "InputFilePath")?.Value
               ?? throw new RisException(ErrorCodes.PackageTemplateInvalid,
                   $"Package template '{packageDescription}' has no InputFilePath in Manifest.xml.",
                   new Dictionary<string, object?> { ["PackageTemplate"] = packageDescription });
    }

    /// <summary>Entity names listed in the manifest.</summary>
    public static IReadOnlyList<string> GetEntityNames(Stream package, string packageDescription)
    {
        using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        return LoadManifest(zip, packageDescription).Descendants()
            .Where(e => e.Name.LocalName == "EntityName")
            .Select(e => e.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
    }

    /// <summary>
    /// Puts a data file into a package (template copy): sets InputFilePath to the original file name
    /// (end-to-end traceability, as in 4.x) and replaces any existing entry with that name.
    /// </summary>
    /// <returns>Names of entries that were unexpectedly present in the template and removed.</returns>
    public static IReadOnlyList<string> WrapFile(Stream packageCopy, Stream dataFile, string originalFileName, string templateInputFileName)
    {
        var removed = new List<string>();
        using var zip = new ZipArchive(packageCopy, ZipArchiveMode.Update, leaveOpen: true);

        foreach (var name in new[] { templateInputFileName, originalFileName }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existing = zip.GetEntry(name);
            if (existing is not null)
            {
                existing.Delete();
                removed.Add(name);
            }
        }

        var manifestEntry = zip.GetEntry(ManifestEntryName)
                            ?? throw new RisException(ErrorCodes.PackageTemplateInvalid, "Package template has no Manifest.xml.");
        XDocument manifest;
        using (var reader = manifestEntry.Open())
        {
            manifest = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }

        var inputPath = manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "InputFilePath")
                        ?? throw new RisException(ErrorCodes.PackageTemplateInvalid, "Package template has no InputFilePath in Manifest.xml.");
        inputPath.Value = originalFileName;
        manifestEntry.Delete();

        using (var writer = zip.CreateEntry(ManifestEntryName).Open())
        using (var xml = XmlWriter.Create(writer, new XmlWriterSettings { Indent = true }))
        {
            manifest.Save(xml);
        }

        using (var entry = zip.CreateEntry(originalFileName, CompressionLevel.Fastest).Open())
        {
            dataFile.CopyTo(entry);
        }

        return removed;
    }

    private static XDocument LoadManifest(ZipArchive zip, string packageDescription)
    {
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new RisException(ErrorCodes.PackageTemplateInvalid, $"Package '{packageDescription}' has no Manifest.xml.",
                        new Dictionary<string, object?> { ["Package"] = packageDescription });
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }
}
