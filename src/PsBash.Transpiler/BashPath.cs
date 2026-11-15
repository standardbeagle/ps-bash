namespace PsBash.Core;

/// <summary>
/// The ONE outbound path mapping: a filesystem path as the OS / .NET / the PowerShell provider spells it →
/// the path a bash script sees (<c>$PWD</c>, <c>$OLDPWD</c>, <c>$HOME</c>, <c>~</c>, <c>$0</c>, <c>pwd</c>,
/// <c>dirs</c>, <c>mktemp</c>, <c>realpath</c>, <c>type</c>, printed operands and error messages).
/// On Windows that is forward slashes with the drive letter kept: <c>C:\Users\x</c> → <c>C:/Users/x</c>,
/// UNC <c>\\server\share</c> → <c>//server/share</c>, a <c>\\?\</c> long-path prefix dropped. Relative paths
/// stay relative (<c>a\b</c> → <c>a/b</c>). Off Windows a backslash is an ordinary file-name character, so
/// the path is returned unchanged.
/// <para>
/// The inbound twin (a path the script wrote → the native path the filesystem wants) is
/// <see cref="RuntimePath.Map"/> (cmdlets: <c>FileSystemHelpers.NormalizeOperandPath</c>; emitted text:
/// <c>BashRuntime.MapPath</c>), which accepts <c>C:/x</c>, <c>/c/x</c>, <c>/mnt/c/x</c> and <c>/tmp</c>.
/// Every <c>\</c> → <c>/</c> conversion of a path goes through <see cref="ToShell(string?)"/>; a guard test
/// (<c>PathMappingGuardTests</c>) rejects ad-hoc <c>Replace('\\', '/')</c> elsewhere. Spec:
/// <c>docs/specs/paths.md</c>.
/// </para>
/// <para>
/// Lives in the leaf <c>PsBash.Transpiler</c> assembly beside <see cref="WindowsPath"/> so the emitter, the
/// cmdlets and the host share it. Emitted PowerShell reaches it through
/// <c>[PsBash.Cmdlets.BashRuntime]::ToShellPath</c> (the load-order rule in <c>BashRuntime.NormalizeWindowsPath</c>).
/// Pure string logic: no I/O, idempotent. Call it on PATHS only: it does not decide what is a path.
/// </para>
/// </summary>
public static class BashPath
{
    /// <summary>The bash-visible spelling of <paramref name="path"/> on this OS (see the type summary).
    /// Null becomes the empty string.</summary>
    public static string ToShell(string? path) => ToShell(path, System.OperatingSystem.IsWindows());

    /// <summary>
    /// <see cref="ToShell(string?)"/> with the platform explicit: <paramref name="windows"/> true converts
    /// separators (a Windows path, whatever the host OS); false returns the path unchanged.
    /// </summary>
    public static string ToShell(string? path, bool windows)
    {
        if (string.IsNullOrEmpty(path) || !windows) return path ?? string.Empty;
        // \\?\C:\x (and \\?\UNC\server\share) is the Win32 long-path form of the same path: bash has no use for it.
        if (path.StartsWith(@"\\?\UNC\", System.StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path.Substring(8);
        else if (path.StartsWith(@"\\?\", System.StringComparison.Ordinal) && path.Length >= 6 && path[5] == ':')
            path = path.Substring(4);
        return path.IndexOf('\\') < 0 ? path : path.Replace('\\', '/');
    }
}
