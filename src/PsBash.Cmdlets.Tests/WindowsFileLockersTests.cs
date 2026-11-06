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

    [SkippableFact]
    public void Describe_ADirectoryThatAnotherProcessUsesAsItsCwd_NamesThatProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "working directories pin directories on Windows only");
        var dir = Directory.CreateTempSubdirectory("ps-bash-lockers-cwd-").FullName;
        System.Diagnostics.Process? child = null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 11 127.0.0.1 >nul")
            {
                WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
            };
            child = System.Diagnostics.Process.Start(psi)!;

            var described = WindowsFileLockers.Describe(dir);

            Assert.Contains($"pid {child.Id}, parent {Environment.ProcessId} (", described);
            Assert.Contains("ping -n 11", described);   // the command line is reported too
            Assert.Contains(", cwd ", described);
        }
        finally
        {
            try { child?.Kill(entireProcessTree: true); child?.WaitForExit(5000); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static bool Holds(string described)
        => described.Contains($"pid {Environment.ProcessId}", StringComparison.Ordinal)
           || described.StartsWith(WindowsFileLockers.ProtectedHolder, StringComparison.Ordinal)
           || described.Contains("open elsewhere", StringComparison.Ordinal)   // the exclusive-open probe saw it locked
           || described.Contains("locked files:", StringComparison.Ordinal);

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
