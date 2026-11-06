using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// A host spawned while the spawner's cwd is a caller's directory must not keep that directory as ITS cwd: on
/// Windows a live process's working directory makes the directory undeletable, so `cd "$d"; ...; cd /; rm -rf "$d"`
/// failed with "being used by another process" whenever a host was spawned mid-command (CI, intermittent).
/// bash on Linux deletes a directory regardless. Hand-written: there is no bash analogue of a spawned host.
/// </summary>
[Collection("EnvVar")]   // changes the process-wide current directory
public class WindowsHostSpawnWorkingDirectoryTests
{
    [SkippableFact]
    public void Start_ChildSpawnedInsideADirectory_DoesNotPinThatDirectory()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: Linux can remove a directory that is another process's cwd");
        var dir = Directory.CreateTempSubdirectory("ps-bash-spawn-cwd-").FullName;
        var original = Environment.CurrentDirectory;
        System.Diagnostics.Process? child = null;
        try
        {
            Environment.CurrentDirectory = dir;
            // Lives ~10 s: long enough to still be running when the directory is removed.
            child = WindowsHostSpawn.Start("cmd.exe", ["/c", "ping -n 11 127.0.0.1 >nul"]);
            Environment.CurrentDirectory = original;

            Directory.Delete(dir, recursive: true);

            Assert.False(Directory.Exists(dir));
            Assert.False(child.HasExited, "the child must still be alive, or the test proves nothing");
        }
        finally
        {
            Environment.CurrentDirectory = original;
            try { child?.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
