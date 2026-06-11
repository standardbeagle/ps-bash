using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashRealpath
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 function used Resolve-Path with a
/// GetUnresolvedProviderPathFromPSPath fallback for paths that do not exist.
/// The cmdlet preserves the exact contract — successful resolution emits the
/// PSPath form, failed resolution emits the canonical path string.
///
/// Failure-surface axes that apply: existing/missing paths, multiple operands,
/// flag-suppression (any -prefixed token is silently skipped), --help dispatch,
/// quoting/injection (Directive 12). Pipeline / file-content / signal axes do
/// not apply: realpath has no streaming or file-read surface.
/// </summary>
public class InvokeBashRealpathCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;
    private readonly string _existingFile;

    public InvokeBashRealpathCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), $"psb-realpath-{Guid.NewGuid():N}".Substring(0, 25));
        Directory.CreateDirectory(_tmpDir);
        _existingFile = Path.Combine(_tmpDir, "exists.txt");
        File.WriteAllText(_existingFile, "hi\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
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

    [Fact]
    public void Realpath_E_ExistingPath_ResolvesNormally()
    {
        var lines = RunLines($"Invoke-BashRealpath -e '{_existingFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.Contains("exists.txt", lines[0]);
    }

    [Fact]
    public void Realpath_E_MissingPath_ErrorsAndEmitsNoPath()
    {
        // REGRESSION: -e was declared but a no-op — a missing path was silently
        // resolved. GNU realpath -e fails on a non-existent path.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "nope.txt").Replace("'", "''");
        var result = pwsh.AddScript($"Invoke-BashRealpath -e '{missing}'").Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        Assert.Empty(result);
        Assert.Contains(errs, m => m.Contains("No such file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Realpath_ExistingFile_ReturnsResolvedPath()
    {
        var lines = RunLines($"Invoke-BashRealpath '{_existingFile.Replace("'", "''")}'");
        Assert.Single(lines);
        // The resolved path should reference the same file. Use FileInfo for
        // canonical comparison (handles case-insensitivity on Windows).
        Assert.True(new FileInfo(lines[0]).FullName.Equals(
            new FileInfo(_existingFile).FullName,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Realpath_NonexistentPath_ReturnsCanonicalForm()
    {
        // psm1 oracle: when Resolve-Path fails, fall back to
        // GetUnresolvedProviderPathFromPSPath, which always succeeds.
        var ghost = Path.Combine(_tmpDir, "ghost.txt");
        var lines = RunLines($"Invoke-BashRealpath '{ghost.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.True(lines[0].EndsWith("ghost.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Realpath_MultipleOperands_EmitsOneLinePerOperand()
    {
        var second = Path.Combine(_tmpDir, "ghost.txt");
        var lines = RunLines(
            $"Invoke-BashRealpath '{_existingFile.Replace("'", "''")}' '{second.Replace("'", "''")}'");
        Assert.Equal(2, lines.Length);
        Assert.True(lines[0].EndsWith("exists.txt", StringComparison.OrdinalIgnoreCase));
        Assert.True(lines[1].EndsWith("ghost.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Realpath_FlagsAreSkipped()
    {
        // psm1 oracle silently skips any -prefixed token (it never implemented
        // -e / -m / --relative-to). A bare flag with no operand produces zero
        // output.
        var lines = RunLines("Invoke-BashRealpath -e");
        Assert.Empty(lines);
    }

    [Fact]
    public void Realpath_FlagInterleavedWithOperand_OnlyOperandResolved()
    {
        var lines = RunLines($"Invoke-BashRealpath -e '{_existingFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.True(lines[0].EndsWith("exists.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Realpath_ViaAlias_Works()
    {
        var lines = RunLines($"realpath '{_existingFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.True(lines[0].EndsWith("exists.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Realpath_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashRealpath --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("realpath", StringComparison.OrdinalIgnoreCase));
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Realpath_PathWithScriptblockChars_TreatedAsLiteralPath()
    {
        // Path containing $() chars. Must not be evaluated as PowerShell code.
        // Result is the canonical (unresolved) form of the literal string.
        var weirdPath = Path.Combine(_tmpDir, "$(throw'pwn').txt");
        var lines = RunLines($"Invoke-BashRealpath '{weirdPath.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.DoesNotContain(lines, l => l.Contains("pwn") && !l.Contains("$(throw"));
        Assert.True(lines[0].EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }
}
