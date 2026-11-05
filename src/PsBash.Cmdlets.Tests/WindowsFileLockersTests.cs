using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The Restart Manager attribution behind <c>rm: cannot remove 'x': ... being used by another process [held by: ...]</c>.
/// Oracle note: bash unlinks open files, so there is no bash equivalent of "who holds this file".
/// </summary>
public class WindowsFileLockersTests
{
    [SkippableFact]
    public void Describe_AFileHeldOpenByThisProcess_NamesThisProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Restart Manager is Windows-only");
        var dir = Directory.CreateTempSubdirectory("ps-bash-lockers-").FullName;
        try
        {
            var file = Path.Combine(dir, "held.txt");
            using var held = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read);   // no FileShare.Delete

            // This process holds the file, so it must be named - unless a scanner (Defender) opened the fresh file
            // too, in which case the Restart Manager refuses to list holders and the report must say so.
            Assert.True(Holds(WindowsFileLockers.Describe(file)), WindowsFileLockers.Describe(file));
            Assert.True(Holds(WindowsFileLockers.Describe(dir)), WindowsFileLockers.Describe(dir));   // a directory reports its files' holders
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static bool Holds(string described)
        => described.Contains($"pid {Environment.ProcessId}", StringComparison.Ordinal)
           || described == WindowsFileLockers.ProtectedHolder;

    [SkippableFact]
    public void Describe_AFileNobodyHolds_IsEmpty_AndAMissingPathNeverThrows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Restart Manager is Windows-only");
        var dir = Directory.CreateTempSubdirectory("ps-bash-lockers-").FullName;
        try
        {
            var file = Path.Combine(dir, "free.txt");
            File.WriteAllText(file, "x");

            Assert.Equal("", WindowsFileLockers.Describe(file));
            Assert.Equal("", WindowsFileLockers.Describe(Path.Combine(dir, "missing")));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
