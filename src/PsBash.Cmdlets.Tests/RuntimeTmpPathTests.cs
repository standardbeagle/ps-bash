using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The runtime /tmp policy (<c>PsBash.Core.RuntimePath</c>): a /tmp path that reaches a cmdlet at run
/// time — the value of an expansion, which the emitter's literal rewrite never sees — names the same
/// directory as the literal rewrite (<c>$env:TEMP</c> on Windows). Hand-written, not oracle: on Linux
/// /tmp is a real directory, the mapping is the Windows-only policy itself. Every assertion points
/// $env:TEMP at a private directory, so nothing here depends on (or touches) a real C:\tmp.
/// </summary>
[Trait("Platform", "Windows")]
public class RuntimeTmpPathTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public RuntimeTmpPathTests(SharedPwshFixture fixture) => _fixture = fixture;

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private static string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash", "tmproot-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [SkippableFact]
    public void NormalizeOperandPath_TmpRooted_MapsToEnvTemp()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the /tmp mapping is the Windows policy");
        var saved = Environment.GetEnvironmentVariable("TEMP");
        var root = NewTempRoot();
        try
        {
            Environment.SetEnvironmentVariable("TEMP", root);
            Assert.Equal(Path.Combine(root, "a", "b.txt"), FileSystemHelpers.NormalizeOperandPath("/tmp/a/b.txt"));
            Assert.Equal(root, FileSystemHelpers.NormalizeOperandPath("/tmp"));
            Assert.Equal("/tmpx/a", FileSystemHelpers.NormalizeOperandPath("/tmpx/a"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", saved);
            Directory.Delete(root, recursive: true);
        }
    }

    // Each command receives /tmp/… as a RUNTIME string (a direct cmdlet call, exactly what an
    // expanded `$d/f` delivers). It must land in $env:TEMP — the file a literal /tmp/f names.
    [SkippableFact]
    public void Cmdlets_TmpRootedOperand_UseEnvTemp()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the /tmp mapping is the Windows policy");
        var root = NewTempRoot();
        try
        {
            var r = Run(
                $"$saved = $env:TEMP; $env:TEMP = '{root}'; try {{ " +
                "Invoke-BashMkdir '/tmp/d'; Invoke-BashTouch '/tmp/d/t'; " +
                "Invoke-BashEcho x | Invoke-BashRedirect -Path '/tmp/f'; " +
                "Invoke-BashCp '/tmp/f' '/tmp/g'; Invoke-BashMv '/tmp/g' '/tmp/h'; " +
                "Invoke-BashCat '/tmp/h'; Invoke-BashFind '/tmp/d' '-name' 't'; " +
                "Invoke-BashRm '/tmp/f' " +
                "} finally { $env:TEMP = $saved }");
            r.AssertSuccess();
            Assert.True(Directory.Exists(Path.Combine(root, "d")));
            Assert.True(File.Exists(Path.Combine(root, "d", "t")));
            Assert.True(File.Exists(Path.Combine(root, "h")));
            Assert.False(File.Exists(Path.Combine(root, "f")), "rm /tmp/f did not reach $env:TEMP");
            Assert.Equal("x\n", File.ReadAllText(Path.Combine(root, "h")));
            Assert.Contains("x", r.Stdout);
            Assert.Contains("/tmp/d/t", r.Stdout.Replace('\\', '/'));   // find walked $env:TEMP\d, shown as typed
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
