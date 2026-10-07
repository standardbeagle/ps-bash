using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The strerror text readers report for a file they cannot open (<see cref="FileSystemHelpers.ReadErrorMessage"/>).
/// Hand-written, not oracle: a file held open WITHOUT sharing exists only on Windows, so there is no
/// bash case to diff — bash's closest is EACCES "Permission denied". grep used its own copy and printed
/// the raw .NET text ("The process cannot access the file … being used by another process").
/// </summary>
public class FileReadErrorMessageTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public FileReadErrorMessageTests(SharedPwshFixture fixture) => _fixture = fixture;

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    [Trait("Platform", "Windows")]
    [SkippableTheory]
    [InlineData("Invoke-BashGrep x '{0}'", "grep: {0}: Permission denied")]
    [InlineData("Invoke-BashCat '{0}'", "cat: {0}: Permission denied")]
    [InlineData("Invoke-BashWc -l '{0}'", "wc: {0}: Permission denied")]
    public void Read_FileHeldOpenWithoutSharing_ReportsPermissionDenied(string script, string stderr)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "sharing violations are Windows-only");
        var path = Path.Combine(Path.GetTempPath(), "ps-bash", "locked-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x\n");
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // Forward slashes in: every reader shows the operand as typed.
                var shown = path.Replace('\\', '/');
                Run(string.Format(script, shown)).AssertFailed(
                    script.StartsWith("Invoke-BashGrep") ? 2 : 1, string.Format(stderr, shown));
            }
        }
        finally { File.Delete(path); }
    }

    // Every reader that had its own `notFound ? … : ex.Message` copy now uses the shared helper.
    // Per-command wording varies (GNU's does too), so assert the invariant, not the full line.
    [Trait("Platform", "Windows")]
    [SkippableTheory]
    [InlineData("Invoke-BashSort '{0}'")]
    [InlineData("Invoke-BashCut -c1 '{0}'")]
    [InlineData("Invoke-BashUniq '{0}'")]
    [InlineData("Invoke-BashNl '{0}'")]
    [InlineData("Invoke-BashFold '{0}'")]
    [InlineData("Invoke-BashPaste '{0}'")]
    public void Read_FileHeldOpenWithoutSharing_NeverShowsRawDotNetText(string script)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "sharing violations are Windows-only");
        var path = Path.Combine(Path.GetTempPath(), "ps-bash", "locked-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x\n");
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var r = Run(string.Format(script, path.Replace('\\', '/')));
                Assert.NotEqual(0, r.ExitCode);
                Assert.Contains("Permission denied", r.Stderr);
                Assert.DoesNotContain("being used by another process", r.Stderr);
            }
        }
        finally { File.Delete(path); }
    }
}
