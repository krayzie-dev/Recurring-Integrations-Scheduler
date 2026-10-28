using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RIS.Migration;

/// <summary>Azure app registration of the RIS 4.x client ("Parameters" - "Azure App registrations"); the secret is DPAPI encrypted.</summary>
public sealed record AppRegistration(string Name, string ClientId, string? EncryptedSecret, bool IsService);

/// <summary>
/// Reads the settings of the RIS 4.x client (portable.config next to the client, PortableSettingsProvider). The client validates
/// instances with these app registrations, so their secret is the one maintained by the users, while every 4.x job carries its
/// own copy of the secret taken when the job was saved - copies that are not updated when the secret is renewed.
/// </summary>
public static partial class ClientSettingsReader
{
    public const string FileName = "portable.config";

    public static IReadOnlyList<AppRegistration> ReadAppRegistrations(string path)
    {
        var text = File.ReadAllText(path);
        // Settings of a complex type may be stored as nested XML or as escaped XML text: unescape and look for the elements.
        if (!text.Contains("<AadApplication>", StringComparison.Ordinal)) text = WebUtility.HtmlDecode(text);
        var result = new List<AppRegistration>();
        foreach (Match match in AadApplicationElement().Matches(text))
        {
            XElement element;
            try { element = XElement.Parse(match.Value); }
            catch (System.Xml.XmlException) { continue; }
            string? Value(string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
            if (Value("ClientId") is not { } clientId) continue;
            result.Add(new AppRegistration(Value("Name") ?? clientId, clientId, Value("Secret"),
                !string.Equals(Value("AuthenticationType"), "User", StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    [GeneratedRegex(@"<AadApplication>.*?</AadApplication>", RegexOptions.Singleline)]
    private static partial Regex AadApplicationElement();
}
