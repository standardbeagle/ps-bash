using System.Runtime.InteropServices;
using System.Text;
using PsBash.Core;
using PsBash.Testing;
using Xunit;

namespace PsBash.Core.Tests.Transpiler;

/// <summary>
/// `cd /tmp; [[ $PWD == /tmp ]]` on a Windows CI runner: TEMP is the 8.3 spelling (C:\Users\RUNNER~1\…) but the
/// working directory the OS resolves is the long one. The two are the same directory and must both read as /tmp.
/// Oracle note: bash has no 8.3 names; this is a Windows-only spelling concern, so a hand-written test.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class RuntimePathShortNameTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint cch);

    [SkippableFact]
    public void TryUnmapTmp_ShortSpelledTemp_MatchesTheLongSpelledWorkingDirectory()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "8.3 names are Windows-only");
        var root = Directory.CreateTempSubdirectory("ps-bash-a-rather-long-name-").FullName;
        try
        {
            var sb = new StringBuilder(1024);
            Skip.If(GetShortPathName(root, sb, (uint)sb.Capacity) == 0 || sb.ToString().Equals(root, StringComparison.OrdinalIgnoreCase),
                "8.3 short names are disabled on this volume");
            using var env = ProcessEnv.Set("TEMP", sb.ToString());

            Assert.True(RuntimePath.TryUnmapTmp(root, out var bash), "long spelling of the short TEMP");
            Assert.Equal("/tmp", bash);
            Assert.True(RuntimePath.TryUnmapTmp(Path.Combine(root, "sub", "f"), out var under));
            Assert.Equal("/tmp/sub/f", under);
            Assert.True(RuntimePath.TryUnmapTmp(BashPath.ToShell(root) + "/sub", out var shell), "$PWD's forward-slash form");
            Assert.Equal("/tmp/sub", shell);
            Assert.False(RuntimePath.TryUnmapTmp(root + "x", out _), "a sibling sharing the prefix is not /tmp");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
