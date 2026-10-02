using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PsBash.Cmdlets;

/// <summary>
/// Creating hard and symbolic links with the OS primitive and a GNU-worded failure reason
/// (<c>File exists</c>, <c>Invalid cross-device link</c>, <c>Operation not permitted</c>). OS-interface helper
/// for <c>cp -l/-s</c>, <c>ln</c>: the platform branches live here, the commands only ask.
/// </summary>
internal static class FileLinks
{
    /// <summary>Hard-links <paramref name="target"/> (existing file) at <paramref name="link"/>. Null on success, else the strerror text.</summary>
    public static string? TryCreateHardLink(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (CreateHardLinkW(link, target, IntPtr.Zero)) return null;
                return Win32Reason(Marshal.GetLastWin32Error());
            }
            if (LinkUnix(target, link) == 0) return null;
            return ErrnoReason(Marshal.GetLastPInvokeError());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ex.Message;
        }
    }

    /// <summary>Creates a symbolic link at <paramref name="link"/> holding the text <paramref name="targetText"/>.
    /// <paramref name="targetIsDirectory"/> picks the Windows link flavour. Null on success, else the reason.</summary>
    public static string? TryCreateSymlink(string link, string targetText, bool targetIsDirectory)
    {
        try
        {
            if (targetIsDirectory) Directory.CreateSymbolicLink(link, targetText);
            else File.CreateSymbolicLink(link, targetText);
            return null;
        }
        catch (UnauthorizedAccessException) { return "Operation not permitted"; }
        catch (IOException ex) when (File.Exists(link) || Directory.Exists(link)) { _ = ex; return "File exists"; }
        catch (Exception ex) when (ex is IOException or Win32Exception or PlatformNotSupportedException)
        {
            return ex.Message;
        }
    }

    private static string Win32Reason(int error) => error switch
    {
        183 or 80 => "File exists",          // ERROR_ALREADY_EXISTS / ERROR_FILE_EXISTS
        17 => "Invalid cross-device link",   // ERROR_NOT_SAME_DEVICE
        2 or 3 => "No such file or directory",
        5 => "Operation not permitted",
        1 => "Operation not permitted",
        _ => new Win32Exception(error).Message,
    };

    private static string ErrnoReason(int errno) => errno switch
    {
        1 => "Operation not permitted",
        2 => "No such file or directory",
        13 => "Permission denied",
        17 => "File exists",
        18 => "Invalid cross-device link",
        20 => "Not a directory",
        21 => "Is a directory",
        _ => $"errno {errno}",
    };

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int LinkUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
