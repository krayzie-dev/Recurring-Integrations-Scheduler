using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace RIS.Hosting;

/// <summary>RIS roles (a higher role includes the lower ones): server authorization, page "Access" and "ris access".</summary>
public static class AccessRoles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Admin, Operator, Viewer];

    /// <summary>Role name as RIS uses it (any case), or null for an unknown role.</summary>
    public static string? Normalize(string role) => All.FirstOrDefault(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Windows account resolved by name or SID: canonical name, SID and whether it is a group.</summary>
public sealed record WindowsAccount(string Name, string Sid, bool IsGroup);

/// <summary>Lookup of Windows (domain or local) accounts through LookupAccountName / LookupAccountSid.</summary>
public static class WindowsAccounts
{
    /// <summary>The account, or null when Windows does not know it (or on other platforms, where only SIDs are accepted).</summary>
    public static WindowsAccount? Resolve(string account)
    {
        account = account.Trim();
        if (account.Length == 0) return null;
        if (account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            SecurityIdentifier sid;
            try { sid = new SecurityIdentifier(account); }
            catch (ArgumentException) { return null; }
            if (!OperatingSystem.IsWindows()) return new WindowsAccount(sid.Value, sid.Value, true);
            return LookupSid(sid) ?? new WindowsAccount(sid.Value, sid.Value, true);
        }
        return OperatingSystem.IsWindows() ? LookupName(account) : null;
    }

    private static WindowsAccount? LookupName(string account)
    {
        var sid = new byte[68];
        var sidSize = (uint)sid.Length;
        var domain = new StringBuilder(256);
        var domainSize = (uint)domain.Capacity;
        if (!LookupAccountName(null, account, sid, ref sidSize, domain, ref domainSize, out _)) return null;
        // The canonical name ("DOMAIN\Name" as Windows writes it) and the type come from the SID.
        return LookupSid(new SecurityIdentifier(sid, 0));
    }

    private static WindowsAccount? LookupSid(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var name = new StringBuilder(256);
        var nameSize = (uint)name.Capacity;
        var domain = new StringBuilder(256);
        var domainSize = (uint)domain.Capacity;
        if (!LookupAccountSid(null, bytes, name, ref nameSize, domain, ref domainSize, out var use)) return null;
        var canonical = domain.Length > 0 ? $@"{domain}\{name}" : name.ToString();
        // SidTypeGroup, SidTypeAlias (local group), SidTypeWellKnownGroup.
        return new WindowsAccount(canonical, sid.Value, use is 2 or 4 or 5);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupAccountName(string? systemName, string accountName, byte[] sid, ref uint sidSize,
        StringBuilder domainName, ref uint domainSize, out int use);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupAccountSid(string? systemName, byte[] sid, StringBuilder name, ref uint nameSize,
        StringBuilder domainName, ref uint domainSize, out int use);
}

