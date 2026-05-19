using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 Phase 1 migration of
/// Invoke-BashBasename / Invoke-BashDirname from PsBash.psm1 script functions
/// to binary cmdlets (PsBash.Cmdlets.dll).
///
/// Oracle: GNU coreutils basename/dirname semantics, which the original psm1
/// functions were modeled on. These are pure string transforms with no
/// pipeline / file / environment surface, so the failure-surface axes that
/// apply are empty input, missing operand, unicode, CRLF-in-arg, and
/// quoting/injection (a path that looks like a PowerShell scriptblock must be
/// treated as a literal string, never executed). Large-input / broken-pipe /
/// signal axes do not apply: basename/dirname take fixed argv operands and
/// produce one line per operand with no streaming.
///
/// The PwshTestFixture loads psm1 (which no longer defines these functions)
/// then imports PsBash.Cmdlets.dll, mirroring the host load order — so these
/// tests also prove the function-shadowing removal worked and the psm1
/// `Set-Alias basename/dirname` lines still resolve to the cmdlet.
/// </summary>
public class InvokeBashBasenameDirnameCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashBasenameDirnameCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var err = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();
        Assert.True(err.Count == 0 || err[0] == null,
            $"Unexpected error running [{script}]: {(err.Count > 0 ? err[0]?.ToString() : "none")}");

        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    // ---- basename: core behavior ----

    [Theory]
    [InlineData("/usr/bin/sort", "sort")]
    [InlineData("sort", "sort")]
    [InlineData("/usr/bin/", "bin")]          // trailing slash trimmed
    [InlineData("/usr/bin//", "bin")]         // multiple trailing slashes
    [InlineData("/", "/")]                     // root collapses to "/"
    [InlineData("", "/")]                      // empty operand -> "/" (psm1 oracle parity)
    [InlineData("file.txt", "file.txt")]
    [InlineData("dir/sub/file.txt", "file.txt")]
    public void Basename_StripsDirectory(string input, string expected)
    {
        var lines = RunLines($"Invoke-BashBasename '{input.Replace("'", "''")}'");
        Assert.Equal(new[] { expected }, lines);
    }

    [Theory]
    [InlineData("/usr/include/stdio.h", ".h", "stdio")]
    [InlineData("/usr/bin/sort", ".txt", "sort")]   // suffix not present -> unchanged
    [InlineData("file.txt", ".txt", "file")]
    [InlineData(".txt", ".txt", ".txt")]            // basename == suffix -> NOT stripped (len not strictly greater)
    public void Basename_StripsSuffix_WithDashS(string input, string suffix, string expected)
    {
        var lines = RunLines(
            $"Invoke-BashBasename '-s' '{suffix}' '{input.Replace("'", "''")}'");
        Assert.Equal(new[] { expected }, lines);
    }

    [Fact]
    public void Basename_SuffixEqualsForm_StripsSuffix()
    {
        var lines = RunLines("Invoke-BashBasename '--suffix=.h' '/usr/include/stdio.h'");
        Assert.Equal(new[] { "stdio" }, lines);
    }

    [Fact]
    public void Basename_MultipleOperands_OneLineEach()
    {
        var lines = RunLines("Invoke-BashBasename '/a/b/c' '/x/y' 'z'");
        Assert.Equal(new[] { "c", "y", "z" }, lines);
    }

    [Fact]
    public void Basename_NoOperands_ProducesNoOutput()
    {
        var lines = RunLines("Invoke-BashBasename");
        Assert.Empty(lines);
    }

    [Fact]
    public void Basename_UnicodeOperand_PreservedExactly()
    {
        var lines = RunLines("Invoke-BashBasename '/tmp/é你好\U0001F600.txt'");
        Assert.Equal(new[] { "é你好\U0001F600.txt" }, lines);
    }

    [Fact]
    public void Basename_OperandLookingLikeScriptBlock_TreatedAsLiteral()
    {
        // Quoting/injection axis: an operand containing PowerShell scriptblock
        // characters must be treated as a literal path, never executed.
        var lines = RunLines("Invoke-BashBasename '/tmp/$(rm -rf x);{evil}'");
        Assert.Equal(new[] { "$(rm -rf x);{evil}" }, lines);
    }

    [Fact]
    public void Basename_AliasResolvesToCmdlet()
    {
        // psm1 `Set-Alias basename -> Invoke-BashBasename` must still resolve
        // after the function was removed from psm1.
        var lines = RunLines("basename '/usr/local/bin/pwsh'");
        Assert.Equal(new[] { "pwsh" }, lines);
    }

    // ---- dirname: core behavior ----

    [Theory]
    [InlineData("/usr/bin/sort", "/usr/bin")]
    [InlineData("sort", ".")]                  // no slash -> "."
    [InlineData("/usr/bin/", "/usr")]          // trailing slash trimmed first
    [InlineData("/bin", "/")]                  // slash at index 0 -> "/"
    [InlineData("/", "/")]                      // root
    [InlineData("", "/")]                       // empty operand -> "/" (psm1 oracle parity)
    [InlineData("dir/sub/file.txt", "dir/sub")]
    [InlineData("file.txt", ".")]
    public void Dirname_StripsLastComponent(string input, string expected)
    {
        var lines = RunLines($"Invoke-BashDirname '{input.Replace("'", "''")}'");
        Assert.Equal(new[] { expected }, lines);
    }

    [Fact]
    public void Dirname_MultipleOperands_OneLineEach()
    {
        var lines = RunLines("Invoke-BashDirname '/a/b/c' '/x/y' 'z'");
        Assert.Equal(new[] { "/a/b", "/x", "." }, lines);
    }

    [Fact]
    public void Dirname_NoOperands_ProducesNoOutput()
    {
        var lines = RunLines("Invoke-BashDirname");
        Assert.Empty(lines);
    }

    [Fact]
    public void Dirname_BackslashPathNormalized()
    {
        // psm1 oracle normalizes backslash -> slash before splitting.
        var lines = RunLines(@"Invoke-BashDirname 'C:\Users\me\file.txt'");
        Assert.Equal(new[] { "C:/Users/me" }, lines);
    }

    [Fact]
    public void Dirname_OperandLookingLikeScriptBlock_TreatedAsLiteral()
    {
        var lines = RunLines("Invoke-BashDirname '/tmp/$(rm -rf x);{evil}/leaf'");
        Assert.Equal(new[] { "/tmp/$(rm -rf x);{evil}" }, lines);
    }

    [Fact]
    public void Dirname_AliasResolvesToCmdlet()
    {
        var lines = RunLines("dirname '/usr/local/bin/pwsh'");
        Assert.Equal(new[] { "/usr/local/bin" }, lines);
    }

    // ---- --help delegation ----

    [Fact]
    public void Basename_Help_DelegatesToShowBashHelp()
    {
        var lines = RunLines("Invoke-BashBasename --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("Usage: basename"));
    }

    [Fact]
    public void Dirname_Help_DelegatesToShowBashHelp()
    {
        var lines = RunLines("Invoke-BashDirname --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("Usage: dirname"));
    }
}
