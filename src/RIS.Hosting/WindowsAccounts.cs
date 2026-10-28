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
        if (!OperatingSystem.IsWindows()) return null;
        // Local groups of this server: Windows accepts built-in groups as ".\Name" but not "SERVER\Name", and other local
        // groups the other way round - every form works when the lookup is repeated with the bare name.
        return LookupName(account) ?? (LocalName(account) is { } name ? LookupName(name) : null);
    }

    /// <summary>The name after ".\" or "&lt;this computer&gt;\", otherwise null.</summary>
    private static string? LocalName(string account)
    {
        var slash = account.IndexOf('\\');
        if (slash <= 0 || slash == account.Length - 1) return null;
        var prefix = account[..slash];
        return prefix == "." || string.Equals(prefix, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? account[(slash + 1)..] : null;
    }

    /// <summary>Local groups of this computer (name as Windows writes it, comment), for suggestions on the "Access" page.</summary>
    public static IReadOnlyList<(string Name, string? Comment)> LocalGroups()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var result = new List<(string Name, string? Comment)>();
        nint resume = 0;
        int status;
        do
        {
            status = NetLocalGroupEnum(null, 1, out var buffer, -1, out var read, out _, ref resume);
            if (status != 0 && status != ErrorMoreData) break;
            try
            {
                var size = Marshal.SizeOf<LocalGroupInfo1>();
                for (var i = 0; i < read; i++)
                {
                    var info = Marshal.PtrToStructure<LocalGroupInfo1>(buffer + i * size);
                    if (info.Name is null) continue;
                    // Canonical name from the SID: BUILTIN\Administrators, SERVER\RIS-Operators.
                    result.Add((LookupName(info.Name)?.Name ?? info.Name, string.IsNullOrWhiteSpace(info.Comment) ? null : info.Comment));
                }
            }
            finally
            {
                if (buffer != 0) NetApiBufferFree(buffer);
            }
        } while (status == ErrorMoreData);
        return result.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupInfo1
    {
        public string? Name;
        public string? Comment;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupEnum(string? serverName, int level, out nint buffer, int preferredMaxLength, out int entriesRead,
        out int totalEntries, ref nint resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(nint buffer);

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

