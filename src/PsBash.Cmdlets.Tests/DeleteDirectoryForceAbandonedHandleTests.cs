using System.Runtime.CompilerServices;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Windows refuses to delete a directory holding a file this very process opened without FILE_SHARE_DELETE and then
/// abandoned (never disposed - only the finalizer closes it). bash's rm never fails on that: unlink ignores open
/// handles. The force-delete must release such handles itself rather than report "used by another process".
/// Hand-written (no bash equivalent of "an abandoned handle").
/// </summary>
public class DeleteDirectoryForceAbandonedHandleTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeakAHandle(string file)
    {
        var leaked = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read);   // no FileShare.Delete
        leaked.WriteByte(1);
        // deliberately not disposed
    }

    [SkippableFact]
    public void DeleteDirectoryForce_AFileThisProcessAbandonedOpen_IsStillRemoved()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: Linux unlink ignores open handles");
        var dir = Directory.CreateTempSubdirectory("ps-bash-leak-").FullName;
        LeakAHandle(Path.Combine(dir, "leaked.txt"));

        FileSystemHelpers.DeleteDirectoryForce(dir);

        Assert.False(Directory.Exists(dir));
    }
}
