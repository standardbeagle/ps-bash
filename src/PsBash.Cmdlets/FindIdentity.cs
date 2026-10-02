using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PsBash.Cmdlets;

/// <summary>A resolved <c>-user</c>/<c>-group</c> argument: a numeric id and/or (Windows) the account's SID.</summary>
internal readonly record struct FindIdSpec(long? Id, string? Sid);

/// <summary>
/// Name -&gt; id resolution for <c>find -user/-group</c> (GNU: a name is tried first, an all-digit word is then a numeric id;
/// anything else is <c>‘x’ is not the name of a known user</c>). Unix asks the system (<c>id -u</c>, <c>getent group</c>);
/// Windows translates the account name to a SID (the id is the SID's last sub-authority, the same synthetic uid/gid
/// <see cref="FindStat"/> reports) so a file matches by owner SID.
/// </summary>
internal static class FindIdentity
{
    internal static bool TryResolve(string name, bool isUser, out FindIdSpec spec)
    {
        spec = default;
        if (OperatingSystem.IsWindows()) { if (TryWindows(name, out spec)) return true; }
        else if (TryUnix(name, isUser, out spec)) return true;

        if (name.Length > 0 && name.All(char.IsAsciiDigit) && long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            spec = new FindIdSpec(n, null);
            return true;
        }
        return false;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TryWindowsCore(string name, out FindIdSpec spec)
    {
        spec = default;
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
            spec = new FindIdSpec(FindStat.LastSubAuthority(sid.Value), sid.Value);
            return true;
        }
        catch { return false; }
    }

    private static bool TryWindows(string name, out FindIdSpec spec)
    {
        spec = default;
        return OperatingSystem.IsWindows() && TryWindowsCore(name, out spec);
    }

    private static bool TryUnix(string name, bool isUser, out FindIdSpec spec)
    {
        spec = default;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(isUser ? "id" : "getent")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (isUser) { psi.ArgumentList.Add("-u"); psi.ArgumentList.Add("--"); psi.ArgumentList.Add(name); }
            else { psi.ArgumentList.Add("group"); psi.ArgumentList.Add(name); }
            var r = BashRuntime.RunChildProcess(psi);
            if (r.ExitCode != 0) return false;
            string text = r.Stdout.Trim();
            string idText = isUser ? text : (text.Split(':').Length > 2 ? text.Split(':')[2] : "");
            if (!long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return false;
            spec = new FindIdSpec(id, null);
            return true;
        }
        catch { return false; }
    }
}

/// <summary><c>-readable -writable -executable</c>: the effective user's access (GNU uses access(2)).</summary>
internal static class FindAccess
{
    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int AccessNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    internal static bool Check(FileSystemInfo info, string predicate)
    {
        if (!OperatingSystem.IsWindows())
        {
            int mode = predicate == "-readable" ? 4 : predicate == "-writable" ? 2 : 1;
            try { return AccessNative(info.FullName, mode) == 0; } catch { return false; }
        }
        bool dir = info is DirectoryInfo;
        switch (predicate)
        {
            case "-executable":
                return dir || info.Extension.ToLowerInvariant() is ".exe" or ".bat" or ".cmd" or ".ps1" or ".sh" or ".com";
            case "-writable":
                return dir || (info.Attributes & FileAttributes.ReadOnly) == 0;
            default:
                try
                {
                    if (info is DirectoryInfo di) { using var e = di.EnumerateFileSystemInfos().GetEnumerator(); e.MoveNext(); }
                    else using (new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }
                    return true;
                }
                catch { return false; }
        }
    }
}
