using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace PsBash.Core.Runtime.Ipc;

/// <summary>
/// Facts about this process's token that decide which IPC transport can work under a
/// sandbox that runs ps-bash with a <i>restricted token</i> (codex's Windows sandbox:
/// restricting SIDs = a capability SID, the logon SID and Everyone).
/// </summary>
/// <remarks>
/// <para><b>AF_UNIX.</b> Binding creates the socket FILE, a filesystem write. A read-only
/// sandbox denies it and the host's <c>bind</c> fails with WSAEACCES (10013); creating a
/// named pipe needs no filesystem write, so a restricted-token launcher uses the pipe
/// scheme.</para>
/// <para><b>Named pipes.</b> A restricted token passes an access check only if the DACL
/// grants BOTH its normal SIDs and at least one restricting SID. An owner-only pipe DACL
/// fails the second check, so the pipe also grants the token's restricting logon SIDs
/// (<c>S-1-5-5-X-Y</c>). A logon SID is scoped to one logon session — the same principal
/// the Windows default DACL grants — so this does not widen access to other users.
/// Broad restricting SIDs (Everyone, a capability SID) are deliberately never granted.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class RestrictedToken
{
    private const string LogonSidPrefix = "S-1-5-5-";

    /// <summary>True when the current process token carries restricting SIDs.</summary>
    public static bool IsCurrentProcessRestricted()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return false;
        try { return IsTokenRestricted(token) != 0; }
        finally { CloseHandle(token); }
    }

    /// <summary>
    /// The logon SIDs among the current token's restricting SIDs — the SIDs a pipe DACL
    /// must also grant for a restricted client to connect. Empty for an unrestricted token.
    /// </summary>
    public static IReadOnlyList<SecurityIdentifier> CurrentRestrictingLogonSids()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return [];
        try { return FilterLogonSids(ReadRestrictedSids(token)); }
        finally { CloseHandle(token); }
    }

    /// <summary>Keeps only logon SIDs: never Everyone, a capability SID, or a domain group.</summary>
    internal static IReadOnlyList<SecurityIdentifier> FilterLogonSids(IEnumerable<SecurityIdentifier> sids)
        => sids.Where(s => s.Value.StartsWith(LogonSidPrefix, StringComparison.Ordinal)).ToList();

    private static List<SecurityIdentifier> ReadRestrictedSids(IntPtr token)
    {
        var result = new List<SecurityIdentifier>();
        GetTokenInformation(token, TokenRestrictedSids, IntPtr.Zero, 0, out var length); // sizing call
        if (length <= 0) return result;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenRestrictedSids, buffer, length, out _)) return result;
            // TOKEN_GROUPS: DWORD GroupCount, then SID_AND_ATTRIBUTES[] (pointer-aligned).
            var count = Marshal.ReadInt32(buffer);
            var entrySize = IntPtr.Size * 2;
            for (var i = 0; i < count; i++)
                result.Add(new SecurityIdentifier(Marshal.ReadIntPtr(buffer, IntPtr.Size + i * entrySize)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    private const uint TokenQuery = 0x0008;
    private const int TokenRestrictedSids = 11;

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [LibraryImport("advapi32.dll")]
    private static partial int IsTokenRestricted(IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
