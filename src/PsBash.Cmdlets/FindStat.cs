using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PsBash.Cmdlets;

/// <summary>
/// The stat(2)-shaped facts <c>find</c> needs about one entry (<c>-perm -user -uid -links -inum -ctime -printf -ls</c>),
/// read lazily and once per visited item. .NET exposes only part of stat, so the rest comes from the platform:
/// <list type="bullet">
/// <item><b>Unix</b>: one bounded <c>stat -c</c> child (<see cref="BashRuntime.RunChildProcess"/>) for mode, uid/gid and
/// names, inode, link count, blocks and ctime; times/size from the .NET info object.</item>
/// <item><b>Windows</b> (documented mapping, GNU has no equivalent): mode is synthesised (directory 0755, file 0644, 0755
/// for an executable extension, read-only clears the write bits, link 0777); owner/group are the owner / primary-group
/// SIDs of the ACL (user name = account name without the domain; <c>uid</c>/<c>gid</c> = the SID's last sub-authority);
/// inode = NTFS file index; link count = <c>nNumberOfLinks</c>; ctime = NTFS ChangeTime; birth time = CreationTime;
/// device = volume serial of the drive root.</item>
/// </list>
/// </summary>
internal sealed class FindStat
{
    public int Mode;                    // permission bits incl. setuid/setgid/sticky (07777)
    public char TypeChar = 'f';         // f d l p s c b of THIS entry (lstat view)
    public long Size;
    public long Uid, Gid;
    public string UserName = "", GroupName = "";
    public bool UserKnown, GroupKnown;
    public string OwnerSid = "", GroupSid = "";   // Windows only
    public ulong Inode;
    public uint Nlink = 1;
    public long BlocksK;                // 1 KiB blocks
    public DateTime Atime, Mtime, Ctime, Btime;
    public ulong Device;
    public string? LinkTarget;
    public char TargetType = 'f';       // %Y

    public long Blocks512 => BlocksK * 2;
    public string User => UserKnown ? UserName : Uid.ToString(CultureInfo.InvariantCulture);
    public string Group => GroupKnown ? GroupName : Gid.ToString(CultureInfo.InvariantCulture);

    /// <summary>True when the entry itself is a link (lstat view), whatever <see cref="TargetType"/> says.</summary>
    public bool IsLink => TypeChar == 'l';

    internal static bool IsLinkEntry(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try { return item.LinkTarget is not null; } catch { return false; }
    }

    internal static FileSystemInfo? TryResolveLink(FileSystemInfo item)
    {
        try
        {
            var t = item.ResolveLinkTarget(returnFinalTarget: true);
            return t is not null && t.Exists ? t : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Read the stat of <paramref name="eff"/> (the entry as find sees it: the link target under <c>-L</c>/<c>-H</c>, else the
    /// entry itself); <paramref name="item"/> is the lstat view and supplies the type letter and link target.
    /// </summary>
    internal static FindStat Read(FileSystemInfo item, FileSystemInfo eff, bool following)
    {
        var s = new FindStat();
        bool linkItem = IsLinkEntry(item);
        bool resolved = following && linkItem && !ReferenceEquals(item, eff);
        bool isDir = eff is DirectoryInfo && !(linkItem && !resolved);

        s.TypeChar = linkItem && !resolved ? 'l' : isDir ? 'd' : 'f';
        if (linkItem)
        {
            try { s.LinkTarget = item.LinkTarget; } catch { s.LinkTarget = null; }
            var target = resolved ? eff : TryResolveLink(item);
            s.TargetType = target is null ? 'N' : target is DirectoryInfo ? 'd' : 'f';
        }
        else s.TargetType = s.TypeChar;

        s.Atime = eff.LastAccessTime; s.Mtime = eff.LastWriteTime; s.Btime = eff.CreationTime;
        s.Ctime = s.Mtime;
        long len = 0;
        if (eff is FileInfo fi && !(linkItem && !resolved)) { try { len = fi.Length; } catch { /* vanished */ } }
        if (s.TypeChar == 'l') len = s.LinkTarget is null ? 0 : s.LinkTarget.Length;
        else if (isDir) len = 4096;
        s.Size = len;
        s.BlocksK = s.TypeChar == 'l' ? 0 : isDir ? 4 : (len + 4095) / 4096 * 4;

        if (OperatingSystem.IsWindows()) ReadWindows(s, eff, following, linkItem && !resolved);
        else ReadUnix(s, eff, following);
        return s;
    }

    // ───────────── Windows ─────────────

    private static readonly Dictionary<string, (string Name, bool Known)> SidNames = new();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ReadWindows(FindStat s, FileSystemInfo eff, bool following, bool linkLeaf)
    {
        bool isDir = s.TypeChar == 'd';
        bool readOnly = (eff.Attributes & FileAttributes.ReadOnly) != 0;
        bool exec = isDir || IsExecExtension(eff.Extension);
        if (s.TypeChar == 'l') s.Mode = 0x1FF;                                             // 0777
        else s.Mode = (readOnly ? 0x124 : 0x1A4) | (exec ? 0x049 : 0);                      // 0444/0644 (+x)
        if (isDir) s.Mode = readOnly ? 0x16D : 0x1ED;                                       // 0555/0755

        string path = eff.FullName;
        s.Device = DriveSerial(path);
        if (FileIdentity.TryGetInode(path, out var inode)) s.Inode = inode;
        if (!isDir && FileIdentity.TryGetLinkCount(path, out var n)) s.Nlink = Math.Max(1u, n);
        TryChangeTime(path, following && !linkLeaf, s);
        try
        {
            FileSystemSecurity sec = eff is DirectoryInfo di
                ? di.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group)
                : ((FileInfo)eff).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group);
            if (sec.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier o)
            {
                s.OwnerSid = o.Value; s.Uid = LastSubAuthority(o.Value);
                var (name, known) = SidName(o); s.UserName = name; s.UserKnown = known;
            }
            if (sec.GetGroup(typeof(SecurityIdentifier)) is SecurityIdentifier g)
            {
                s.GroupSid = g.Value; s.Gid = LastSubAuthority(g.Value);
                var (name, known) = SidName(g); s.GroupName = name; s.GroupKnown = known;
            }
        }
        catch
        {
            s.UserName = Environment.UserName; s.UserKnown = true; s.GroupName = s.UserName; s.GroupKnown = true;
        }
    }

    internal static long LastSubAuthority(string sid)
    {
        int i = sid.LastIndexOf('-');
        return i >= 0 && long.TryParse(sid.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string Name, bool Known) SidName(SecurityIdentifier sid)
    {
        lock (SidNames)
        {
            if (SidNames.TryGetValue(sid.Value, out var hit)) return hit;
            (string, bool) r;
            try
            {
                var acct = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
                int bs = acct.LastIndexOf('\\');
                r = (bs >= 0 ? acct.Substring(bs + 1) : acct, true);
            }
            catch { r = ("", false); }
            SidNames[sid.Value] = r;
            return r;
        }
    }

    private static bool IsExecExtension(string ext) => ext.ToLowerInvariant() is ".exe" or ".bat" or ".cmd" or ".ps1" or ".sh" or ".com";

    private static ulong DriveSerial(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? 0UL : (ulong)char.ToUpperInvariant(root[0]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo { public long Creation, Access, Write, Change; public uint Attributes; public uint Pad; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FileBasicInfo info, uint size);

    private static void TryChangeTime(string path, bool followed, FindStat s)
    {
        try
        {
            // FILE_READ_ATTRIBUTES, share all, OPEN_EXISTING, BACKUP_SEMANTICS (+ OPEN_REPARSE_POINT for the link itself)
            uint flags = 0x02000000 | (followed ? 0u : 0x00200000u);
            using var h = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, flags, IntPtr.Zero);
            if (h.IsInvalid) return;
            if (GetFileInformationByHandleEx(h, 0, out var info, (uint)Marshal.SizeOf<FileBasicInfo>()) && info.Change > 0)
                s.Ctime = DateTime.FromFileTimeUtc(info.Change).ToLocalTime();
        }
        catch { /* ctime falls back to mtime */ }
    }

    // ───────────── Unix ─────────────

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void ReadUnix(FindStat s, FileSystemInfo eff, bool following)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("stat")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("%f|%u|%g|%U|%G|%i|%h|%b|%B|%Z|%d");
            if (following) psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(eff.FullName);
            var r = BashRuntime.RunChildProcess(psi);
            var f = r.Stdout.Trim().Split('|');
            if (f.Length >= 11)
            {
                int raw = int.Parse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                s.Mode = raw & 0xFFF;
                s.TypeChar = (raw & 0xF000) switch { 0x4000 => 'd', 0xA000 => 'l', 0x1000 => 'p', 0xC000 => 's', 0x2000 => 'c', 0x6000 => 'b', _ => 'f' };
                s.Uid = long.Parse(f[1], CultureInfo.InvariantCulture); s.Gid = long.Parse(f[2], CultureInfo.InvariantCulture);
                // stat prints UNKNOWN when there is no passwd/group entry
                s.UserKnown = f[3] != "UNKNOWN"; s.UserName = f[3];
                s.GroupKnown = f[4] != "UNKNOWN"; s.GroupName = f[4];
                s.Inode = ulong.Parse(f[5], CultureInfo.InvariantCulture);
                s.Nlink = uint.Parse(f[6], CultureInfo.InvariantCulture);
                long blocks = long.Parse(f[7], CultureInfo.InvariantCulture), bsz = long.Parse(f[8], CultureInfo.InvariantCulture);
                s.BlocksK = blocks * bsz / 1024;
                s.Ctime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[9], CultureInfo.InvariantCulture)).LocalDateTime;
                s.Device = ulong.Parse(f[10], CultureInfo.InvariantCulture);
            }
        }
        catch { /* leave the .NET-derived defaults */ }
        if (s.Mode == 0)
        {
            try { s.Mode = (int)eff.UnixFileMode; } catch { s.Mode = 0x1A4; }
        }
    }

    // ───────────── helpers shared by -printf / -ls ─────────────

    /// <summary>The 10-character <c>ls -l</c> mode string (setuid/setgid/sticky shown as s/S/t/T).</summary>
    internal static string SymbolicMode(char type, int mode)
    {
        var c = new char[10];
        c[0] = type == 'f' ? '-' : type;
        const string rwx = "rwxrwxrwx";
        for (int i = 0; i < 9; i++) c[i + 1] = (mode & (0x100 >> i)) != 0 ? rwx[i] : '-';
        if ((mode & 0x800) != 0) c[3] = (mode & 0x40) != 0 ? 's' : 'S';
        if ((mode & 0x400) != 0) c[6] = (mode & 0x8) != 0 ? 's' : 'S';
        if ((mode & 0x200) != 0) c[9] = (mode & 0x1) != 0 ? 't' : 'T';
        return new string(c);
    }

    /// <summary>Ctime of an arbitrary path (reference files of <c>-newerXY</c>), mtime when unavailable.</summary>
    internal static DateTime ChangeTimeOf(FileSystemInfo info)
    {
        var s = new FindStat { Ctime = info.LastWriteTime };
        if (OperatingSystem.IsWindows()) TryChangeTime(info.FullName, followed: false, s);
        else ReadUnixCtimeOnly(info, s);
        return s.Ctime;
    }

    private static void ReadUnixCtimeOnly(FileSystemInfo info, FindStat s)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("stat") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("%Z"); psi.ArgumentList.Add("--"); psi.ArgumentList.Add(info.FullName);
            var o = BashRuntime.RunChildProcess(psi).Stdout.Trim();
            if (long.TryParse(o, out var secs)) s.Ctime = DateTimeOffset.FromUnixTimeSeconds(secs).LocalDateTime;
        }
        catch { /* keep mtime */ }
    }
}
